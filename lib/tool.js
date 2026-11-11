/**
 * `computer` 工具：一个工具覆盖应用/窗口枚举、观察、元素动作、坐标动作与停止开关。
 *
 * 为什么是一个工具：电脑操作天然是「观察 → 一个动作 → 再观察」的循环，拆成十几个工具
 * 会让工具表膨胀，模型每一步真正需要的只是「再来一个动作」。参数用扁平对象 + action 判别，
 * 与 `browser` 工具同构。
 *
 * @module dsh-computer-use/tool
 */

import { randomUUID } from 'node:crypto'
import { readFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { basename, join } from 'node:path'
import { requirePeer } from './peers.js'
import { ToolError } from './state.js'
import { renderAction, renderApps, renderObservation, renderWindows } from './render.js'

const ACTIONS = [
  'apps', 'windows', 'observe', 'click', 'drag', 'scroll', 'hover',
  'set_value', 'type', 'key', 'paste', 'stop',
]

/** 停止状态下仍然允许的动作：模型要能说明现状。 */
const READ_ONLY_ACTIONS = new Set(['apps', 'windows', 'observe', 'stop'])

const DESCRIPTION = [
  'Read or operate the UI of native desktop apps on this computer (mouse, keyboard, accessibility elements).',
  'Use it for native apps and the OS shell. For anything inside a web page, use the `browser` tool instead.',
  '',
  'Workflow: `apps` → `windows` (optional) → `observe` → one action → `observe` again to confirm.',
  '`observe` returns the accessibility tree; every line starts with the element index you pass to actions.',
  '',
  'Element first. `click` on an element index, `set_value` on an editable element and element-level `scroll`',
  'go through UI Automation, so they work on a background window without stealing focus. Coordinates and',
  'keystrokes are global input: they need the target window in front, so the tool brings it forward first and',
  'reports `activated=before` when it did. If it cannot, the action is refused and nothing is sent.',
  '',
  'Coordinates are pixels of the screenshot you asked for with `observe{screenshot:true}` — never guess them.',
  'A click by coordinates without a fresh screenshot is refused with NO_RASTER.',
  '',
  'Right-click and double-click go through `button` and `click_count`. Only a plain left single click uses UI',
  'Automation patterns; any other button or repeat count becomes a real pointer click at the element center, so it',
  'needs the window in front (that is where `activated=before` comes from). `hover` moves the pointer without',
  'clicking: use it to reveal tooltips, hover menus and previews, then observe again to read what appeared.',
  '',
  'Foreground matters for coordinates, dragging, keyboard and paste: Windows refuses to let a background process',
  'raise a window while the foreground lock is held (200s after the user last typed). Those actions either only',
  'activate the window — the receipt says `activated=topmost`, click again — or fail with FOREGROUND_DENIED.',
  'Element actions use accessibility patterns and work on a background window, so they stay the first choice.',
  '',
  'App names must match exactly (display name, exe file name or AUMID). An unknown name is an error with the',
  'candidate list, never a guess; pass `pid` when a name is ambiguous. Mutating actions return a receipt line:',
  '`dispatch=possibly_sent` means the action may already have reached the app, so observe again instead of retrying.',
  '',
  'Main agent only: this tool is refused when a subagent calls it.',
].join('\n')

const PARAMETERS = {
  action: {
    type: 'string',
    required: true,
    enum: ACTIONS,
    description: 'Operation to run. apps/windows/observe read state; the rest act on the target app.',
  },
  app: {
    type: 'string',
    description: 'Target app: display name, exe file name or AUMID. Give either `app` or `pid`. Used by every action except apps.',
  },
  pid: {
    type: 'number',
    description: 'Target process id from `apps`. More precise than `app` when several apps share a name.',
  },
  window_id: {
    type: 'number',
    description: 'Target window id from `windows`. Defaults to the app main window.',
  },
  element_index: {
    type: 'number',
    description: 'Element index from the latest `observe` of this app. For click/drag/scroll/set_value.',
  },
  x: { type: 'number', description: 'Pixel X in the latest screenshot (with `screenshot:true`). For click/drag/scroll.' },
  y: { type: 'number', description: 'Pixel Y in the latest screenshot. For click/drag/scroll.' },
  to_element_index: { type: 'number', description: 'Drag destination element index. For drag.' },
  to_x: { type: 'number', description: 'Drag destination pixel X in the latest screenshot. For drag.' },
  to_y: { type: 'number', description: 'Drag destination pixel Y in the latest screenshot. For drag.' },
  direction: {
    type: 'string',
    enum: ['up', 'down', 'left', 'right'],
    description: 'Scroll direction. For scroll.',
  },
  pages: { type: 'number', description: 'Scroll distance in pages, default 1. For scroll.' },
  text: { type: 'string', description: 'Text to type (type) or the key chord like `ctrl+s` / `Return` (key).' },
  value: { type: 'string', description: 'New value written through UI Automation. For set_value.' },
  key: { type: 'string', description: 'Key chord for the `key` action, e.g. `Return`, `ctrl+shift+Tab`, `alt+F4`.' },
  repeat: { type: 'number', description: 'Repeat count for `key`, default 1.' },
  hold_ms: { type: 'number', description: 'Hold duration in ms for `key`, default 30.' },
  button: {
    type: 'string',
    enum: ['left', 'right', 'middle'],
    description: 'Mouse button, default left. For click.',
  },
  click_count: { type: 'number', description: 'Click count, default 1; use 2 for double-click. For click.' },
  modifiers: {
    type: 'string',
    description: 'Modifier keys held during the action, e.g. `ctrl` or `ctrl+shift`. For click/drag.',
  },
  screenshot: {
    type: 'boolean',
    description: 'Attach a window screenshot to the observation. For observe. Also enables coordinate actions afterwards.',
  },
  full: {
    type: 'boolean',
    description: 'Return up to 5000 elements instead of the default 300. For observe.',
  },
  resume: { type: 'boolean', description: 'Pass true to `stop` and resume accepting actions.' },
  reason: { type: 'string', description: 'Why control is being stopped. For stop.' },
}

const IMAGE_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  properties: {
    attachmentId: { type: 'string', required: true },
    mediaType: { type: 'string', required: true },
    bytes: { type: 'number', required: true },
    width: { type: 'number', required: true },
    height: { type: 'number', required: true },
    name: { type: 'string' },
  },
}

/**
 * 注册 `computer` 工具。
 *
 * @param {object} options - 注册参数。
 * @param {object} options.ctx - 宿主上下文。
 * @param {import('./helper-client.js').HelperClient} options.client - helper 客户端。
 * @param {import('./state.js').ObservationState} options.state - 观察快照。
 * @param {object} options.config - 插件配置。
 * @returns {void}
 */
export function registerComputerTool ({ ctx, client, state, config }) {
  let stopped = false

  const { defineTool } = requirePeer('@deepseek-ai/dsh-tools')

  // ── 应用解析 ─────────────────────────────────────────────

  async function resolvePid (args) {
    if (typeof args.pid === 'number' && Number.isFinite(args.pid) && args.pid > 0) return args.pid
    const name = typeof args.app === 'string' ? args.app.trim() : ''
    if (name === '') {
      throw new ToolError('INVALID_ARGUMENT', '需要 pid 或 app（先用 apps 看当前有哪些应用）')
    }
    const { apps } = await client.call('apps', {})
    const tiers = [
      app => app.name === name,
      app => (app.name ?? '').toLowerCase() === name.toLowerCase(),
      app => app.exe !== null && app.exe !== undefined && basename(app.exe).toLowerCase() === name.toLowerCase(),
      app => (app.aumid ?? '').toLowerCase() === name.toLowerCase(),
    ]
    for (const tier of tiers) {
      const hits = apps.filter(tier)
      if (hits.length === 1) return hits[0].pid
      if (hits.length > 1) {
        const list = hits.map(app => `${app.pid}:${app.name}`).join('、')
        throw new ToolError('AMBIGUOUS_APP', `"${name}" 匹配到多个应用（${list}）：请改用 pid 指定`)
      }
    }
    const candidates = apps.slice(0, 10).map(app => `${app.pid}:${app.name}`).join('、')
    throw new ToolError('APP_NOT_FOUND', `没有名为 "${name}" 的应用（名称必须精确匹配）。当前应用：${candidates}`)
  }

  // ── 截图与附件 ───────────────────────────────────────────

  async function assertImageRoute (exec) {
    const llm = ctx.get('llm')
    const routed = exec.agent?.session.requestHeader()?.config
    const provider = routed?.provider ?? exec.agent?.options?.provider
    const model = routed?.model ?? exec.agent?.options?.model
    if (llm === undefined || provider === undefined || model === undefined) return
    const info = await llm.resolveModelInfo(provider, model, exec.signal)
    if (!Array.isArray(info?.inputModalities) || !info.inputModalities.includes('image')) {
      throw new ToolError('IMAGE_UNSUPPORTED',
        `当前模型 ${model} 不接受图片输入：改用 screenshot:false，或换成支持图片的模型`)
    }
  }

  async function attachScreenshot (exec, capture) {
    const attachments = ctx.get('attachments')
    if (attachments === undefined) {
      throw new ToolError('IMAGE_UNSUPPORTED', '当前会话没有附件服务，无法把截图交给模型：请改用元素索引')
    }
    await assertImageRoute(exec)
    const data = await readFile(capture.path)
    const ref = await attachments.saveImage({
      data,
      mediaType: 'image/png',
      name: `window-${capture.width}x${capture.height}.png`,
    })
    return {
      attachmentId: ref.attachmentId,
      mediaType: ref.mediaType,
      bytes: ref.bytes,
      width: ref.width,
      height: ref.height,
      ...(ref.name === undefined ? {} : { name: ref.name }),
    }
  }

  // ── 动作分发 ─────────────────────────────────────────────

  function describeElement (index) {
    const node = state.last?.nodes.find(item => item.index === index)
    if (node === undefined) return `[${index}]`
    const label = node.name || node.automationId || node.kind
    return `[${index}] "${label}"`
  }

  function requireNumber (args, key) {
    const value = args[key]
    if (typeof value !== 'number' || !Number.isFinite(value)) {
      throw new ToolError('INVALID_ARGUMENT', `缺少数字参数 ${key}`)
    }
    return value
  }

  ctx.tools.register(defineTool({
    name: 'computer',
    description: DESCRIPTION,
    parameters: PARAMETERS,
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          text: { type: 'string', required: true },
          image: IMAGE_SCHEMA,
        },
      },
      render: (_args, value) => {
        const blocks = [{ type: 'text', text: value.text }]
        if (value.image !== undefined) {
          blocks.push({ type: 'image', attachment: value.image })
        }
        return blocks
      },
    },
    isConcurrencySafe: () => false,
    async execute (args, exec) {
      try {
        return await invoke(args, exec)
      } catch (error) {
        throw decorate(error)
      }
    },
  }))

  return { reset () { stopped = false } }

  // ── 执行主体 ─────────────────────────────────────────────

  async function invoke (args, exec) {
    const action = args.action

    // 电脑操作是全局输入：两个 agent 同时操作鼠标会互相踩踏，子 agent 一律拒绝
    if (exec.agent?.session?.header?.origin === 'subagent') {
      throw new ToolError('SUBAGENT_DENIED',
        'computer 只允许主 agent 调用：子 agent 操作桌面会与主 agent 争抢鼠标键盘')
    }

    if (stopped && !READ_ONLY_ACTIONS.has(action)) {
      throw new ToolError('CONTROL_STOPPED',
        '电脑操作已停止：apps/windows/observe 仍可用；要恢复请调用 computer{action:"stop", resume:true}')
    }

    switch (action) {
        case 'apps': {
          const result = await client.call('apps', {})
          return { text: renderApps(result.apps) }
        }

        case 'windows': {
          const pid = await resolvePid(args)
          const result = await client.call('windows', { pid })
          return { text: renderWindows(result.app, result.windows) }
        }

        case 'observe': {
          const pid = await resolvePid(args)
          const windowId = typeof args.window_id === 'number' ? args.window_id : undefined
          const wantCapture = args.screenshot === true
          const capturePath = join(tmpdir(), 'dsh-computer-use', `${randomUUID()}.png`)

          const payload = await client.call('observe', {
            pid,
            windowId,
            capture: wantCapture,
            capturePath,
            maxNodes: args.full === true ? 5000 : config.maxNodes,
            maxDepth: 40,
            maxChildren: 80,
          }, wantCapture ? 25000 : 15000)

          state.remember(payload)

          let image
          let captureNote
          if (payload.capture !== undefined) {
            try {
              image = await attachScreenshot(exec, payload.capture)
            } catch (error) {
              // 截图失败不该毁掉观察：树才是主要信息，缺图单独说明
              captureNote = `截图未能附上（${error.code ?? 'ERROR'}）：${error.message}`
            } finally {
              await rm(capturePath, { force: true })
            }
          }
          return {
            text: renderObservation(payload, { captureNote }),
            ...(image === undefined ? {} : { image }),
          }
        }

        case 'click': {
          const pid = await resolvePid(args)
          const windowId = typeof args.window_id === 'number' ? args.window_id : undefined
          const common = {
            button: args.button ?? 'left',
            clickCount: typeof args.click_count === 'number' ? args.click_count : 1,
            modifiers: args.modifiers,
          }
          if (typeof args.element_index === 'number') {
            state.assertOwner(pid, windowId)
            const result = await client.call('click', { pid, windowId, elementIndex: args.element_index, ...common })
            return { text: renderAction('click', result, describeElement(args.element_index)) }
          }
          if (typeof args.x === 'number' && typeof args.y === 'number') {
            const point = state.screenPoint(pid, windowId, args.x, args.y)
            const result = await client.call('click', { pid, windowId, screenX: point.x, screenY: point.y, ...common })
            return { text: renderAction('click', result, `(${args.x},${args.y})→屏幕(${point.x},${point.y})`) }
          }
          throw new ToolError('INVALID_ARGUMENT', 'click 需要 element_index，或者同时给 x 与 y（坐标要先 observe{screenshot:true}）')
        }

        case 'drag': {
          const pid = await resolvePid(args)
          const windowId = typeof args.window_id === 'number' ? args.window_id : undefined
          if (typeof args.element_index === 'number' || typeof args.to_element_index === 'number') {
            state.assertOwner(pid, windowId)
          }

          let to
          if (typeof args.to_element_index === 'number') to = state.elementCenter(args.to_element_index)
          else if (typeof args.to_x === 'number' && typeof args.to_y === 'number') to = state.screenPoint(pid, windowId, args.to_x, args.to_y)
          else throw new ToolError('INVALID_ARGUMENT', 'drag 需要 to_element_index 或 to_x/to_y 作为终点')

          // 终点是元素时可以让 helper 自己取元素中心；终点是像素时整条路径都按坐标发，
          // helper 那边的「窗口没动过」校验才管得住这张图的基准
          let result
          if (typeof args.to_element_index === 'number' && typeof args.element_index === 'number') {
            result = await client.call('drag', {
              pid,
              windowId,
              fromElementIndex: args.element_index,
              screenToX: to.x,
              screenToY: to.y,
              modifiers: args.modifiers,
            })
            return { text: renderAction('drag', result, `${describeElement(args.element_index)} → ${describeElement(args.to_element_index)}`) }
          }

          let from
          if (typeof args.element_index === 'number') from = state.elementCenter(args.element_index)
          else if (typeof args.x === 'number' && typeof args.y === 'number') from = state.screenPoint(pid, windowId, args.x, args.y)
          else throw new ToolError('INVALID_ARGUMENT', 'drag 需要 element_index 或 x/y 作为起点')

          result = await client.call('drag', {
            pid,
            windowId,
            screenFromX: from.x,
            screenFromY: from.y,
            screenToX: to.x,
            screenToY: to.y,
            modifiers: args.modifiers,
          })
          return { text: renderAction('drag', result, `(${from.x},${from.y})→(${to.x},${to.y})`) }
        }

        case 'hover': {
          const pid = await resolvePid(args)
          const windowId = typeof args.window_id === 'number' ? args.window_id : undefined
          if (typeof args.element_index === 'number') {
            state.assertOwner(pid, windowId)
            const result = await client.call('hover', { pid, windowId, elementIndex: args.element_index })
            return { text: renderAction('hover', result, describeElement(args.element_index)) }
          }
          if (typeof args.x === 'number' && typeof args.y === 'number') {
            const point = state.screenPoint(pid, windowId, args.x, args.y)
            const result = await client.call('hover', { pid, windowId, screenX: point.x, screenY: point.y })
            return { text: renderAction('hover', result, `(${args.x},${args.y})`) }
          }
          throw new ToolError('INVALID_ARGUMENT', 'hover 需要 element_index，或者同时给 x 与 y')
        }

        case 'scroll': {
          const pid = await resolvePid(args)
          const windowId = typeof args.window_id === 'number' ? args.window_id : undefined
          const base = {
            direction: args.direction ?? 'down',
            pages: typeof args.pages === 'number' ? args.pages : 1,
          }
          if (typeof args.element_index === 'number') {
            state.assertOwner(pid, windowId)
            const result = await client.call('scroll', { pid, windowId, elementIndex: args.element_index, ...base })
            return { text: renderAction('scroll', result, describeElement(args.element_index)) }
          }
          if (typeof args.x === 'number' && typeof args.y === 'number') {
            const point = state.screenPoint(pid, windowId, args.x, args.y)
            const result = await client.call('scroll', { pid, windowId, screenX: point.x, screenY: point.y, ...base })
            return { text: renderAction('scroll', result, `(${args.x},${args.y})`) }
          }
          throw new ToolError('INVALID_ARGUMENT', 'scroll 需要 element_index，或者同时给 x 与 y')
        }

        case 'set_value': {
          const pid = await resolvePid(args)
          const windowId = typeof args.window_id === 'number' ? args.window_id : undefined
          const index = requireNumber(args, 'element_index')
          if (typeof args.value !== 'string') throw new ToolError('INVALID_ARGUMENT', 'set_value 需要字符串 value')
          state.assertOwner(pid, windowId)
          const result = await client.call('set_value', { pid, windowId, elementIndex: index, value: args.value })
          return { text: renderAction('set_value', result, describeElement(index)) }
        }

        case 'type': {
          const pid = await resolvePid(args)
          if (typeof args.text !== 'string' || args.text === '') throw new ToolError('INVALID_ARGUMENT', 'type 需要非空 text')
          const result = await client.call('type', {
            pid,
            windowId: typeof args.window_id === 'number' ? args.window_id : undefined,
            text: args.text,
          })
          return { text: renderAction('type', result, `${args.text.length} 个字符`) }
        }

        case 'key': {
          const pid = await resolvePid(args)
          const chord = typeof args.key === 'string' ? args.key : args.text
          if (typeof chord !== 'string' || chord === '') throw new ToolError('INVALID_ARGUMENT', 'key 需要 key（例如 `ctrl+s`、`Return`）')
          const result = await client.call('key', {
            pid,
            windowId: typeof args.window_id === 'number' ? args.window_id : undefined,
            text: chord,
            repeat: typeof args.repeat === 'number' ? args.repeat : 1,
            holdMs: typeof args.hold_ms === 'number' ? args.hold_ms : 0,
          })
          return { text: renderAction('key', result, chord) }
        }

        case 'paste': {
          const pid = await resolvePid(args)
          if (typeof args.text !== 'string') throw new ToolError('INVALID_ARGUMENT', 'paste 需要 text')
          const result = await client.call('paste', {
            pid,
            windowId: typeof args.window_id === 'number' ? args.window_id : undefined,
            text: args.text,
          })
          return { text: renderAction('paste', result, `${args.text.length} 个字符`) }
        }

        case 'stop': {
          const resume = args.resume === true
          stopped = !resume
          if (resume) {
            state.clear()
            return { text: '电脑操作已恢复（旧观察已失效，请重新 observe）' }
          }
          return {
            text: `电脑操作已停止${args.reason ? `（${args.reason}）` : ''}：apps/windows/observe 仍可用，其余动作会被拒绝；恢复请用 stop{resume:true}`,
          }
        }

        default:
          throw new ToolError('INVALID_ARGUMENT', `未知 action：${action}`)
    }
  }
}

/**
 * 把内部错误码带到模型可见的消息里。
 *
 * 模型读的是 message 而不是结构化字段，不带 code 就只能看一段中文描述猜下一步；
 * 带上 `[NO_RASTER]` 这样的前缀，它才知道该去截图还是该用元素索引。
 */
function decorate (error) {
  if (error instanceof ToolError || error?.name === 'HelperError') {
    const wrapped = new Error(`[${error.code}] ${error.message}`)
    wrapped.code = error.code
    if (error.actionSent === true) wrapped.actionSent = true
    return wrapped
  }
  return error
}
