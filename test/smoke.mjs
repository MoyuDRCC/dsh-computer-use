/**
 * 完整覆盖测试：对着 test/fixture 里的受控窗口，逐项验证每一种操作。
 *
 * 受控窗口覆盖：菜单、按钮、复选框、单选、下拉框、数值框、滑块、列表、树、选项卡、
 * 自绘区域（无 UIA 语义）、拖拽源与目标、右键菜单。每个动作都会把结果写进 lblStatus，
 * 因此「动作真的生效」与「事件只是发出去了」能区分开。
 *
 * 分两类用例：
 * - 无障碍动作（click/set_value/元素级 scroll）：任何时候都能跑；
 * - 坐标与键盘动作：要求窗口在前台。系统前台锁定期间拿不到焦点，这类用例会记为 SKIP。
 *
 * 运行：node test/smoke.mjs
 */

import { spawn, spawnSync } from 'node:child_process'
import { existsSync, statSync, unlinkSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { tmpdir } from 'node:os'
import { HelperClient } from '../lib/helper-client.js'
import { renderApps, renderObservation } from '../lib/render.js'

const here = dirname(fileURLToPath(import.meta.url))
const fixtureSource = join(here, 'fixture', 'FixtureWindow.cs')
const fixtureExe = join(here, 'fixture', 'FixtureWindow.exe')

const results = []
function check (name, ok, detail = '') {
  results.push({ name, ok, detail })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`)
}

function skip (name, reason) {
  results.push({ name, ok: true, skipped: true, detail: reason })
  console.log(`SKIP  ${name}  — ${reason}`)
}

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms))

function ensureFixture () {
  if (existsSync(fixtureExe) && statSync(fixtureExe).mtimeMs >= statSync(fixtureSource).mtimeMs) return
  const csc = join(process.env.WINDIR ?? 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe')
  const result = spawnSync(csc, [
    '/nologo', '/noconfig', '/target:winexe', `/out:${fixtureExe}`,
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    fixtureSource,
  ], { encoding: 'utf8', windowsHide: true })
  if (result.status !== 0) throw new Error(`编译测试窗口失败：\n${result.stdout ?? ''}${result.stderr ?? ''}`)
}

const client = new HelperClient({ logger: { info () {}, warn () {}, error () {} } })
const capturePath = join(tmpdir(), `dsh-cua-coverage-${Date.now()}.png`)
let pid = null
let fixture = null

// ── 测试辅助 ────────────────────────────────────────────────

async function snap () {
  return client.call('observe', { pid })
}

async function clickById (id) {
  const snapshot = await snap()
  const node = snapshot.nodes.find(item => item.automationId === id)
  if (node === undefined) throw new Error(`元素 ${id} 不在树里`)
  return client.call('click', { pid, elementIndex: node.index })
}

async function clickByName (name) {
  const snapshot = await snap()
  const node = snapshot.nodes.find(item => item.name === name)
  if (node === undefined) throw new Error(`元素 "${name}" 不在树里`)
  return client.call('click', { pid, elementIndex: node.index })
}

async function setValueById (id, value) {
  const snapshot = await snap()
  const node = snapshot.nodes.find(item => item.automationId === id)
  if (node === undefined) throw new Error(`元素 ${id} 不在树里`)
  return client.call('set_value', { pid, elementIndex: node.index, value })
}

async function statusText () {
  const snapshot = await snap()
  const node = snapshot.nodes.find(item => item.automationId === 'lblStatus')
  return node === undefined ? '' : (node.name ?? '')
}

/** 坐标动作用的像素基准：每次重新截图，因为普通 observe 会替换掉上一次的快照。 */
async function freshShot () {
  return client.call('observe', { pid, capture: true, capturePath }, 25000)
}

/** 执行一次操作，然后用 lblStatus 的内容断言它真的生效了。 */
async function expect (name, run, expected) {
  try {
    const outcome = await run()
    await sleep(260)
    const text = await statusText()
    const via = outcome && outcome.method ? ` via=${outcome.method}` : ''
    check(name, text.includes(expected), `status="${text}"${via}`)
  } catch (error) {
    check(name, false, `${error.code ? error.code + ': ' : ''}${error.message}`)
  }
}

/** 执行一次操作，然后读控件自己的状态断言——应用事件不一定触发，状态一定看得见。 */
async function expectState (name, run, key, keyValue, field, expected) {
  try {
    const outcome = await run()
    await sleep(300)
    const snapshot = await snap()
    const node = snapshot.nodes.find(item => item[key] === keyValue)
    const actual = node === undefined ? undefined : node[field]
    const via = outcome && outcome.method ? ` via=${outcome.method}` : ''
    check(name, actual === expected, `${field}=${JSON.stringify(actual)}${via}`)
  } catch (error) {
    check(name, false, `${error.code ? error.code + ': ' : ''}${error.message}`)
  }
}

/** 需要窗口在前台才能生效的用例。 */
async function frontCase (name, run, expected) {  if (!foreground) {
    skip(name, '窗口拿不到前台（系统前台锁定），坐标/键盘动作无法验证')
    return
  }
  await expect(name, run, expected)
}

let foreground = false

// ── 测试流程 ────────────────────────────────────────────────

try {
  ensureFixture()
  await client.start()

  const apps = await client.call('apps', {})
  console.log(renderApps(apps.apps.slice(0, 3)))

  fixture = spawn(fixtureExe, [], { detached: true, stdio: 'ignore' })
  fixture.unref()

  const deadline = Date.now() + 15000
  while (Date.now() < deadline) {
    const list = await client.call('apps', {})
    const found = list.apps.find(app => /Fixture/i.test(app.name))
    if (found) { pid = found.pid; break }
    await sleep(200)
  }
  check('应用出现在 apps 列表', pid !== null, pid === null ? '未出现' : `pid ${pid}`)
  if (pid === null) throw new Error('测试窗口没有启动')

  const windows = await client.call('windows', { pid })
  check('windows 返回受控窗口', windows.windows.length === 1 && windows.windows[0].title.includes('Fixture'),
    `${windows.windows.length} 个窗口，标题 "${windows.windows[0].title}"`)
  foreground = windows.windows[0].focused === true

  const tree = await snap()
  console.log(renderObservation(tree))
  const required = ['menuStrip', 'lblStatus', 'txtInput', 'btnSubmit', 'chkOption', 'rdoA', 'cboChoice',
    'numCount', 'trkLevel', 'lstItems', 'treeNodes', 'tabs', 'canvas', 'dragSource', 'dropTarget']
  const missing = required.filter(id => !tree.nodes.some(node => node.automationId === id))
  check('observe 覆盖全部控件', missing.length === 0, missing.length === 0 ? `${tree.nodes.length} 个元素` : `缺 ${missing.join(',')}`)

  const kinds = new Set(tree.nodes.map(node => node.kind))
  check('树里能识别出多种控件类型', ['Button', 'CheckBox', 'RadioButton', 'ComboBox', 'Edit', 'List', 'Tree', 'Tab'].every(kind => kinds.has(kind)),
    [...kinds].join(','))

  // ── 无障碍动作（不需要前台）──────────────────────────────

  await expect('按钮 invoke 触发 Click', () => clickById('btnSubmit'), 'button:1')
  await expect('复选框 toggle 切换状态', () => clickById('chkOption'), 'check:True')
  await expect('单选按钮 select 选中', () => clickById('rdoA'), 'radio:A')

  await expect('下拉框展开后选中项目', async () => {
    await clickById('cboChoice')
    await sleep(300)
    await clickByName('乙选项')
  }, 'combo:1')

  await expectState('列表项 select 选中', () => clickByName('项目 3'), 'name', '项目 3', 'selected', true)

  await expect('树节点选中子节点', () => clickByName('子节点 A'), 'tree:子节点 A')

  // WinForms 的数值框对外报 0–0 的假范围，RangeValue 设置会被控件自己拒绝。
  // 这里要验证的是「拒绝时说清楚」，而不是静默失败或把值清零。
  try {
    await setValueById('numCount', '42')
    const snapshot = await snap()
    const node = snapshot.nodes.find(item => item.automationId === 'numCount')
    check('数值框 set_value 生效或明确拒绝', node?.value === '42', `value=${node?.value}`)
  } catch (error) {
    check('数值框 set_value 生效或明确拒绝',
      error.code === 'NOT_SETTABLE' && /type/.test(error.message),
      `${error.code}: ${error.message.slice(0, 50)}`)
  }
  await expectState('滑块 set_value（RangeValue）', () => setValueById('trkLevel', '75'), 'automationId', 'trkLevel', 'value', '75')

  await expect('选项卡切换', () => clickByName('第二页'), 'tab:1')

  await expect('菜单展开后点击菜单项', async () => {
    await clickByName('文件')
    await sleep(300)
    await clickByName('新建')
  }, 'menu:new')

  await expect('文本框 set_value 且不抢焦点', () => setValueById('txtInput', '由 set_value 写入'), 'text:由 set_value 写入')

  const scrolled = await (async () => {
    const snapshot = await snap()
    const node = snapshot.nodes.find(item => item.automationId === 'lstItems')
    return client.call('scroll', { pid, elementIndex: node.index, direction: 'down', pages: 1 })
  })()
  check('列表元素级滚动（ScrollPattern）', scrolled.dispatch === 'accepted', `via=${scrolled.method}`)

  // ── 需要前台的用例 ──────────────────────────────────────

  const shot = await client.call('observe', { pid, capture: true, capturePath }, 25000)
  const canvasNode = shot.nodes.find(node => node.automationId === 'canvas')
  const frame = shot.window.frame
  const toScreen = (x, y) => ({ x: frame[0] + x, y: frame[1] + y })
  const canvasPoint = canvasNode
    ? toScreen(canvasNode.rect[0] + canvasNode.rect[2] / 2 - frame[0], canvasNode.rect[1] + canvasNode.rect[3] / 2 - frame[1])
    : null

  check('截图写入文件', existsSync(capturePath) && statSync(capturePath).size > 500,
    `${statSync(capturePath).size} 字节 ${shot.capture?.width}x${shot.capture?.height}`)

  if (canvasPoint) {
    await frontCase('坐标点击自绘区域（无 UIA 语义）', async () => {
      await freshShot()
      await client.call('click', { pid, screenX: canvasPoint.x, screenY: canvasPoint.y })
    }, 'canvas:Left:1')
  }

  await frontCase('双击（click_count:2）', async () => {
    await freshShot()
    await client.call('click', { pid, screenX: canvasPoint.x, screenY: canvasPoint.y, clickCount: 2 })
  }, 'canvas:double:2')

  await frontCase('右键点击弹出上下文菜单', async () => {
    await freshShot()
    const node = (await snap()).nodes.find(item => item.automationId === 'canvas')
    await client.call('click', { pid, elementIndex: node.index, button: 'right' })
    await sleep(400)
    const opened = await snap()
    const menuItem = opened.nodes.find(item => item.name === '菜单项一')
    if (menuItem === undefined) throw new Error('右键没有弹出上下文菜单')
    await client.call('click', { pid, elementIndex: menuItem.index })
  }, 'context:1')

  await frontCase('元素到元素拖拽', async () => {
    const snapshot = await snap()
    const from = snapshot.nodes.find(item => item.automationId === 'dragSource')
    const to = snapshot.nodes.find(item => item.automationId === 'dropTarget')
    await client.call('drag', {
      pid,
      fromElementIndex: from.index,
      screenToX: Math.round(to.rect[0] + to.rect[2] / 2),
      screenToY: Math.round(to.rect[1] + to.rect[3] / 2),
    })
  }, 'drop:ok')

  await frontCase('键盘输入中文', async () => {
    const snapshot = await snap()
    const node = snapshot.nodes.find(item => item.automationId === 'txtInput')
    await client.call('click', { pid, elementIndex: node.index })
    await sleep(120)
    await client.call('key', { pid, text: 'ctrl+a' })
    await sleep(80)
    await client.call('type', { pid, text: '中文输入 ok' })
  }, 'text:中文输入 ok')

  await frontCase('组合键触发应用快捷键', async () => {
    await client.call('key', { pid, text: 'ctrl+s' })
  }, 'hotkey:ctrl+s')

  await frontCase('粘贴到文本框', async () => {
    await client.call('key', { pid, text: 'ctrl+a' })
    await sleep(80)
    await client.call('paste', { pid, text: '来自剪贴板' })
  }, 'text:来自剪贴板')

  if (canvasPoint) {
    if (!foreground) {
      skip('坐标滚轮', '窗口拿不到前台，坐标滚轮无法验证')
    } else {
      try {
        await freshShot()
        const rolled = await client.call('scroll', { pid, screenX: canvasPoint.x, screenY: canvasPoint.y, direction: 'down', pages: 1 })
        check('坐标滚轮', rolled.dispatch === 'accepted' && rolled.method === 'wheel', `via=${rolled.method}`)
      } catch (error) {
        check('坐标滚轮', false, `${error.code ? error.code + ': ' : ''}${error.message}`)
      }
    }
  }

  // ── 悬停（任何情况都能跑）───────────────────────────────
  try {
    const snapshot = await snap()
    const node = snapshot.nodes.find(item => item.automationId === 'btnSubmit')
    const moved = await client.call('hover', { pid, elementIndex: node.index })
    check('hover 只移动指针', moved.dispatch === 'accepted' && moved.method === 'pointer-move', `via=${moved.method}`)
  } catch (error) {
    check('hover 只移动指针', false, error.message)
  }

  // ── 错误路径 ────────────────────────────────────────────
  try {
    const snapshot = await snap()
    await client.call('set_value', { pid, elementIndex: 9999, value: 'x' })
    check('越界元素索引被拒绝', false, '没有抛错')
  } catch (error) {
    check('越界元素索引被拒绝', error.code === 'ELEMENT_UNAVAILABLE', `${error.code}`)
  }
} catch (error) {
  check('测试流程未抛异常', false, `${error.code ? error.code + ': ' : ''}${error.message}`)
  if (client.diagnostics) console.error('helper stderr:\n' + client.diagnostics)
} finally {
  await client.stop()
  if (pid !== null) spawnSync('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true })
  await sleep(200)
  if (existsSync(capturePath)) unlinkSync(capturePath)
}

const failed = results.filter(item => !item.ok)
const skipped = results.filter(item => item.skipped)
const passed = results.filter(item => item.ok && !item.skipped)
console.log(`\n${passed.length}/${results.length - skipped.length} 通过`
  + (skipped.length > 0 ? `，${skipped.length} 项跳过` : ''))
if (failed.length > 0) {
  console.log('失败项：' + failed.map(item => item.name).join('、'))
  process.exitCode = 1
}
