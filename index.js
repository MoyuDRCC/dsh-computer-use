/**
 * DSH 电脑操作插件（Host 半）。
 *
 * 给模型一个 `computer` 工具：用 Windows UI Automation 读取原生应用的界面结构，
 * 并按元素索引点击/写值/按键，必要时用窗口截图确认像素结果。
 *
 * 真正的界面操作在独立的 helper 子进程里（.NET Framework 4.8）：UIA 是跨进程同步调用，
 * 目标应用无响应时只废掉一个请求，不会冻住整个会话。
 *
 * @module dsh-computer-use
 */

import { HelperClient } from './lib/helper-client.js'
import { ObservationState } from './lib/state.js'
import { registerComputerTool } from './lib/tool.js'

/** Cordis 插件名。 */
export const name = 'computer-use'

/** 注册工具前必须先有工具注册表。 */
export const inject = ['tools']

/** 系统提示段在工具指引带中的相对位置（排在浏览器操作之后）。 */
const SECTION_ORDER = 159

/** 面向模型的插件公告：这里是行为约束的主要落点。 */
const GUIDANCE = [
  '本机已安装 dsh-computer-use 插件：`computer` 工具可以读取并操作原生桌面应用（无障碍元素 + 键鼠）。',
  '',
  '标准流程：',
  '1. `computer {action:"apps"}` 看有哪些应用；必要时 `action:"windows"` 看窗口。',
  '2. `computer {action:"observe", app:"..."}` 读界面结构，返回的每一行以元素索引开头。',
  '3. 用索引执行动作：click / set_value / scroll 走无障碍接口，后台可用、不抢焦点。',
  '4. 动作后再 `observe` 一次，确认界面真的变成了预期状态。',
  '',
  '约束：',
  '- 元素优先。只有元素表达不了时才用坐标或键盘：坐标必须是 `observe{screenshot:true}` 那张图的像素，否则会被拒绝。',
  '- 右键用 `click{button:"right"}`，双击用 `click{click_count:2}`；这两类会走真实指针点击，需要窗口在前台。',
  '- 只有 hover 才出现的 tooltip / 悬浮菜单，用 `action:"hover"` 停一下，再 observe 看新出现的元素。',
  '- 坐标点击、拖拽、键盘、粘贴要求目标窗口已在前台。Windows 在用户最近输入后的 200 秒内拒绝后台进程抢焦点，',
  '  这类动作要么只完成激活（收据里 `activated=topmost` 就是这个意思，需要再点一次），要么直接报 `FOREGROUND_DENIED`。',
  '  元素动作（click 元素 / set_value / 元素级 scroll）走无障碍接口，不受此限。',
  '- 应用名必须精确匹配（显示名 / exe 文件名 / AUMID）；报错会给出候选，不要改写名字反复试。',
  '- 坐标与键盘是全局输入：工具会先把目标窗口带到前台再发；窗口抢不到前台时会直接拒绝，不会打到别的窗口。',
  '- 网页里的操作请用 `browser` 工具，不要用这里。',
  '- 收据里出现 `dispatch=possibly_sent` 表示动作可能已经生效：先重新观察，不要直接重试。',
].join('\n')

/**
 * 挂载 helper 与 `computer` 工具。
 *
 * @param {import('@deepseek-ai/cordis').Context} ctx - 宿主上下文。
 * @param {object} [config] - 插件配置。
 * @param {boolean} [config.enabled] - 总开关（默认 true）。
 * @param {boolean} [config.announceToAgent] - 是否注入系统提示公告（默认 true）。
 * @param {number} [config.maxNodes] - 单次观察的元素上限（默认 300）。
 * @returns {void}
 */
export function apply (ctx, config = {}) {
  if (config.enabled === false) return

  const client = new HelperClient({ logger: ctx.logger })
  const state = new ObservationState()

  ctx.effect(() => () => { void client.stop() }, 'computer-use: helper 生命周期')

  registerComputerTool({
    ctx,
    client,
    state,
    config: {
      maxNodes: typeof config.maxNodes === 'number' ? config.maxNodes : 300,
    },
  })

  if (config.announceToAgent !== false) {
    const systemPrompt = ctx.get('systemPrompt')
    if (systemPrompt !== undefined) {
      ctx.effect(
        () => systemPrompt.section({
          name: 'plugin:dsh-computer-use',
          order: SECTION_ORDER,
          text: GUIDANCE,
        }),
        'computer-use: 模型公告',
      )
    }
  }
}
