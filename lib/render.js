/**
 * 把 helper 返回的节点数组渲染成模型可读的文本。
 *
 * 渲染放在 Node 侧而不是 C# 侧：调整措辞、折叠策略、裁剪规则都不用重编译 helper。
 * 头部三行固定是应用身份 / 窗口身份 / 元素计数，任何裁剪都不会让模型丢掉这几项。
 *
 * @module dsh-computer-use/render
 */

const NAME_LIMIT = 80
const VALUE_LIMIT = 120

/** 纯容器：没有名字、没有值、没有动作，只起分组作用。 */
function isPlainContainer (node) {
  return (node.name ?? '') === ''
    && (node.value ?? '') === ''
    && (node.automationId ?? '') === ''
    && node.actions.length === 0
    && node.focused !== true
    && node.checked === undefined
    && node.selected !== true
}

function clip (text, limit) {
  const value = String(text)
  return value.length <= limit ? value : `${value.slice(0, limit - 1)}…`
}

function describeNode (node) {
  const parts = [`[${node.index}] ${node.kind}`]

  if (node.secret === true) {
    parts.push('<password>')
  } else if (!isPlainContainer(node)) {
    if (node.name) parts.push(`"${clip(node.name, NAME_LIMIT)}"`)
    if (node.value) parts.push(`value="${clip(node.value, VALUE_LIMIT)}"`)
  }

  if (node.automationId) parts.push(`#${clip(node.automationId, 40)}`)

  if (node.actions.length > 0) parts.push(`actions=[${node.actions.join(',')}]`)
  // 开关类状态必须显式写出来：模型靠它判断「刚才那一下到底生效了没有」
  if (node.checked === true) parts.push('checked')
  if (node.checked === false) parts.push('unchecked')
  if (node.selected === true) parts.push('selected')
  if (node.focused === true) parts.push('focused')
  if (node.editable === true) parts.push('editable')
  if (node.enabled === false) parts.push('disabled')
  if (node.offscreen === true) parts.push('offscreen')

  const line = parts.join('  ')
  if (node.hiddenChildren > 0) return `${line}  …+${node.hiddenChildren}`
  if (node.hiddenChildren < 0) return `${line}  …more`
  return line
}

/**
 * 渲染一次观察结果。
 *
 * @param {object} payload - helper 的 observe 返回值。
 * @param {object} [options] - 渲染选项。
 * @param {string} [options.captureNote] - 截图环节的说明（成功时为空，失败时给出原因）。
 * @returns {string} 面向模型的文本。
 */
export function renderObservation (payload, options = {}) {
  const app = payload.app ?? {}
  const window = payload.window ?? {}
  const frame = Array.isArray(window.frame) ? window.frame : [0, 0, 0, 0]
  const exe = app.exe ? app.exe.split(/[\\/]/).pop() : (app.aumid ?? '')

  const header = [
    `app ${app.name ?? '?'} (pid ${app.pid}${exe ? `, ${exe}` : ''})`,
    `window ${window.windowId} "${window.title ?? ''}"  frame ${frame.join(',')}`,
  ]

  const status = [`elements ${payload.nodes.length}`, `state ${payload.stateId}`]
  if (payload.truncated) status.push('truncated（索引不连续，需要完整表用 full:true）')
  if (options.captureNote) status.push(options.captureNote)
  else if (payload.capture !== undefined) status.push('已附窗口截图，坐标取这张图的像素')
  header.push(status.join('  '))

  const lines = payload.nodes.map(node => `${'  '.repeat(node.depth)}${describeNode(node)}`)
  return `${header.join('\n')}\n\n${lines.join('\n')}`
}

/**
 * 渲染应用列表。
 *
 * @param {object[]} apps - helper 的 apps 返回值中的数组。
 * @returns {string} 面向模型的文本。
 */
export function renderApps (apps) {
  if (apps.length === 0) return '没有找到带可见窗口的应用'
  const lines = apps.map(app => {
    const flags = []
    if (app.active) flags.push('active')
    if (app.elevated) flags.push('elevated')
    const suffix = flags.length > 0 ? `  [${flags.join(' ')}]` : ''
    const exe = app.exe ? app.exe.split(/[\\/]/).pop() : (app.aumid ?? '')
    return `${app.pid}\t${app.name}${exe ? ` (${exe})` : ''}  窗口 ${app.windows}${suffix}`
  })
  return ['pid\t应用', ...lines].join('\n')
}

/**
 * 渲染窗口列表。
 *
 * @param {object} app - 应用身份。
 * @param {object[]} windows - 窗口数组。
 * @returns {string} 面向模型的文本。
 */
export function renderWindows (app, windows) {
  if (windows.length === 0) return `应用 ${app.name ?? app.pid} 当前没有可见窗口`
  const lines = windows.map(window => {
    const flags = []
    if (window.main) flags.push('main')
    if (window.focused) flags.push('focused')
    if (window.minimized) flags.push('minimized')
    const rect = Array.isArray(window.rect) ? window.rect.join(',') : ''
    return `${window.windowId}  ${window.title || '(无标题)'}  ${rect}${flags.length > 0 ? `  [${flags.join(' ')}]` : ''}`
  })
  return [`app ${app.name ?? '?'} (pid ${app.pid})`, ...lines].join('\n')
}

/**
 * 渲染一次动作的结果。
 *
 * @param {string} action - 动作名。
 * @param {object} result - helper 的动作返回值。
 * @param {string} [detail] - 目标描述，例如 `[12] "保存"`。
 * @returns {string} 单行收据 + 下一步提示。
 */
export function renderAction (action, result, detail) {
  const target = detail ? ` ${detail}` : ''
  let activation = ''
  if (result.activated === 'before') {
    activation = '  activated=before（点击前窗口不在前台：系统会把这次点击用于激活，目标控件多半没响应，需要再点一次）'
  } else if (result.activated === 'topmost') {
    activation = '  activated=topmost（抢不到前台，已临时置顶让事件落到该窗口；这次点击多半只完成了激活，请再点一次）'
  }
  return `${action}${target}  · dispatch=${result.dispatch ?? 'accepted'}  sent=${result.actionSent === true}  via=${result.method ?? '?'}${activation}`
}
