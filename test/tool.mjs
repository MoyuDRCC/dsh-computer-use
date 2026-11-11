/**
 * 工具层集成测试：在最小 ctx 里注册 `computer`，直接调用它的 execute。
 *
 * 验证的是真实路径：peer 解析（@deepseek-ai/dsh-tools）、defineTool 的参数/输出 schema、
 * action 分发、错误码与渲染。不覆盖 DSH 的 UI 呈现。
 *
 * 运行：node test/tool.mjs
 */

import { spawn, spawnSync } from 'node:child_process'
import { existsSync, statSync, unlinkSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { tmpdir } from 'node:os'
import { fileURLToPath } from 'node:url'
import { HelperClient } from '../lib/helper-client.js'
import { ObservationState } from '../lib/state.js'
import { registerComputerTool } from '../lib/tool.js'

const here = dirname(fileURLToPath(import.meta.url))
const fixtureExe = join(here, 'fixture', 'FixtureWindow.exe')

// 独立 node 进程解析不到宿主里的 @deepseek-ai/dsh-tools，用最小桩替代
process.env.DSH_PEER_ROOT = join(here, 'shims')

const results = []
function check (name, ok, detail = '') {
  results.push({ name, ok, detail })
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`)
}

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms))

const registry = new Map()
const ctx = {
  tools: { register: definition => registry.set(definition.name, definition) },
  get: () => undefined,
  effect: register => register(),
  logger: console,
}

const client = new HelperClient({ logger: console })
registerComputerTool({
  ctx,
  client,
  state: new ObservationState(),
  config: { maxNodes: 300 },
})

const tool = registry.get('computer')
const exec = { signal: undefined, agent: undefined }

let fixture = null
try {
  check('工具注册成功', tool !== undefined)
  check('参数 schema 含 action', tool.parameters?.action?.enum?.length > 0)
  check('输出 schema 声明 image', tool.output?.schema?.properties?.image !== undefined)

  if (!existsSync(fixtureExe)) throw new Error('缺少 fixture，先运行 node test/smoke.mjs 编译')

  const apps = await tool.execute({ action: 'apps' }, exec)
  check('apps 通过工具层返回文本', typeof apps.text === 'string' && apps.text.includes('pid'), apps.text.split('\n')[0])

  fixture = spawn(fixtureExe, [], { detached: true, stdio: 'ignore' })
  fixture.unref()
  await sleep(1500)

  const appList = await client.call('apps', {})
  const target = appList.apps.find(app => app.pid === undefined ? false : /Fixture/i.test(`${app.name} ${app.exe}`))
  if (target === undefined) throw new Error('fixture 窗口没有出现')

  const observed = await tool.execute({ action: 'observe', app: 'FixtureWindow.exe' }, exec)
  check('observe 通过 app 名解析并返回树', observed.text.includes('elements'), observed.text.split('\n')[0])

  const input = /\n\s*\[(\d+)\] Edit/.exec(observed.text)
  check('渲染里能看到输入框', input !== null, input ? `index ${input[1]}` : observed.text.slice(0, 120))

  if (input) {
    const index = Number(input[1])
    await tool.execute({ action: 'set_value', app: 'FixtureWindow.exe', element_index: index, value: 'via tool layer' }, exec)
    const after = await tool.execute({ action: 'observe', app: 'FixtureWindow.exe' }, exec)
    check('set_value 经工具层生效', after.text.includes('via tool layer'), '')
  }

  let denied = null
  try {
    await tool.execute({ action: 'click', app: 'FixtureWindow.exe', x: 10, y: 10 }, exec)
  } catch (error) {
    denied = error
  }
  check('没有截图时坐标动作被拒（NO_RASTER）', denied?.code === 'NO_RASTER', denied?.message?.slice(0, 60) ?? '未抛错')

  let stopped = null
  await tool.execute({ action: 'stop', reason: 'test' }, exec)
  try {
    await tool.execute({ action: 'click', app: 'FixtureWindow.exe', element_index: 1 }, exec)
  } catch (error) {
    stopped = error
  }
  check('停止后动作被拒（CONTROL_STOPPED）', stopped?.code === 'CONTROL_STOPPED', stopped?.message?.slice(0, 60) ?? '未抛错')
  await tool.execute({ action: 'stop', resume: true }, exec)

  let notFound = null
  try {
    await tool.execute({ action: 'observe', app: 'definitely-not-an-app' }, exec)
  } catch (error) {
    notFound = error
  }
  check('未知应用名给 APP_NOT_FOUND 与候选', notFound?.code === 'APP_NOT_FOUND' && /当前应用/.test(notFound.message), notFound?.message?.slice(0, 60) ?? '未抛错')
} catch (error) {
  check('工具层测试未抛异常', false, `${error.code ? error.code + ': ' : ''}${error.message}`)
  if (client.diagnostics) console.error(client.diagnostics)
} finally {
  await client.stop()
  spawnSync('taskkill', ['/IM', 'FixtureWindow.exe', '/F'], { windowsHide: true })
}

const failed = results.filter(item => !item.ok)
console.log(`\n${results.length - failed.length}/${results.length} 通过`)
if (failed.length > 0) {
  console.log('失败项：' + failed.map(item => item.name).join('、'))
  process.exitCode = 1
}
