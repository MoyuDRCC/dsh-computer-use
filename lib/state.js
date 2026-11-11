/**
 * 最近一次观察的快照，以及由它派生的坐标换算。
 *
 * 只有一个快照：helper 侧也只有一份，语义是「动作只对最新一次观察成立」。
 * 观察另一个窗口会替换它，这与模型的心智一致，也让索引永远不会跨窗口漂移。
 *
 * @module dsh-computer-use/state
 */

/** 带错误码的工具错误，code 与 helper 的 Codes.cs 保持同一套字符串。 */
export class ToolError extends Error {
  constructor (code, message) {
    super(message)
    this.name = 'ToolError'
    this.code = code
  }
}

export class ObservationState {
  #last = null

  get last () {
    return this.#last
  }

  remember (payload) {
    this.#last = payload
  }

  clear () {
    this.#last = null
  }

  /** 元素索引必须属于同一 pid（给了 window_id 时还要同窗口）的最新观察。 */
  assertOwner (pid, windowId) {
    const last = this.#last
    if (last === null) {
      throw new ToolError('STALE_STATE', '还没有观察过任何窗口：请先调用 observe，再按返回的元素索引操作')
    }
    if (last.app.pid !== pid) {
      throw new ToolError('STALE_STATE',
        `元素索引来自 pid ${last.app.pid} 的那次观察，与请求的 pid ${pid} 不一致：请重新 observe 目标窗口`)
    }
    if (windowId !== undefined && last.window.windowId !== windowId) {
      throw new ToolError('STALE_STATE',
        `元素索引来自窗口 ${last.window.windowId} 的那次观察：请先 observe 窗口 ${windowId}`)
    }
    return last
  }

  /** 取某个元素在屏幕上的中心点。 */
  elementCenter (index) {
    const last = this.#last
    if (last === null) {
      throw new ToolError('STALE_STATE', '还没有观察过任何窗口：请先调用 observe')
    }
    const node = last.nodes.find(item => item.index === index)
    if (node === undefined) {
      throw new ToolError('ELEMENT_UNAVAILABLE', `元素索引 ${index} 不在最近一次观察里（共 ${last.nodes.length} 个）`)
    }
    const [x, y, width, height] = node.rect
    if (width <= 0 || height <= 0) {
      throw new ToolError('ELEMENT_UNAVAILABLE', `元素 ${index} 没有可见矩形，无法定位坐标`)
    }
    return { x: Math.round(x + width / 2), y: Math.round(y + height / 2) }
  }

  /**
   * 截图像素 → 屏幕坐标。
   *
   * 坐标只对「那张截图」成立：截图是按窗口视觉矩形裁的，所以换算基准必须是同一次
   * 观察的 frame 与截图尺寸，不能是当前窗口位置（窗口可能已经移动）。
   *
   * pid 必须与产生这张截图的观察一致，否则会把 A 窗口的像素算到 B 窗口的屏幕上。
   */
  screenPoint (pid, windowId, x, y) {
    const last = this.assertOwner(pid, windowId)
    if (last.capture === undefined) {
      throw new ToolError('NO_RASTER',
        '坐标需要一个像素基准：请先 observe 并带上 screenshot:true，再按那张图的像素给 x/y（也可以直接用元素索引）')
    }
    const frame = last.window.frame
    const scaleX = last.capture.width > 0 ? frame[2] / last.capture.width : 1
    const scaleY = last.capture.height > 0 ? frame[3] / last.capture.height : 1
    return {
      x: Math.round(frame[0] + x * scaleX),
      y: Math.round(frame[1] + y * scaleY),
    }
  }
}
