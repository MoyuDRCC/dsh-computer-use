/**
 * helper 子进程的 RPC 客户端。
 *
 * 一份 stdin/stdout 管道，一行一个 JSON；请求按 id 配对，超时后丢弃迟到响应。
 * 进程意外退出时所有在途请求立刻失败，下一次调用会自动重启。
 *
 * @module dsh-computer-use/helper-client
 */

import { spawn, spawnSync } from 'node:child_process'
import { ensureHelper } from './helper-build.js'

/** helper 返回的结构化错误；code 由 C# 侧唯一定义，这里只透传。 */
export class HelperError extends Error {
  constructor ({ code, message, actionSent = false }) {
    super(message)
    this.name = 'HelperError'
    this.code = code
    this.actionSent = actionSent === true
  }
}

/** 动作类方法：超时意味着「可能已经下发」，不能盲目重试。 */
const ACTION_METHODS = new Set(['click', 'drag', 'scroll', 'set_value', 'type', 'key', 'paste'])

export class HelperClient {
  #process = null
  #buffer = ''
  #pending = new Map()
  #nextId = 1
  #stderrTail = ''
  #starting = null
  #logger

  constructor ({ logger } = {}) {
    this.#logger = logger
  }

  get running () {
    return this.#process !== null
  }

  /** 最近 4KB stderr，用于诊断（helper 自己的异常与崩溃信息都走这里）。 */
  get diagnostics () {
    return this.#stderrTail
  }

  /**
   * 启动 helper（已启动则直接返回）。
   *
   * @returns {Promise<void>}
   */
  async start () {
    if (this.#process !== null) return
    if (this.#starting !== null) return await this.#starting

    this.#starting = (async () => {
      const { exePath, compiled } = ensureHelper({
        onCompile: () => this.#logger?.info?.('正在首次编译电脑操作 helper（约 2 秒）'),
      })
      if (compiled) this.#logger?.info?.('电脑操作 helper 编译完成')

      const child = spawn(exePath, ['--parent', String(process.pid)], {
        stdio: ['pipe', 'pipe', 'pipe'],
        windowsHide: true,
      })
      child.stdout.setEncoding('utf8')
      child.stderr.setEncoding('utf8')
      child.stdout.on('data', chunk => this.#onStdout(chunk))
      child.stderr.on('data', chunk => {
        this.#stderrTail = (this.#stderrTail + chunk).slice(-4096)
      })
      child.on('error', error => {
        this.#logger?.error?.('电脑操作 helper 启动失败', error)
        this.#failAll(new HelperError({ code: 'HELPER_UNAVAILABLE', message: `helper 启动失败：${error.message}` }))
        this.#process = null
      })
      child.on('exit', (code, signal) => {
        if (this.#process === child) this.#process = null
        const detail = this.#stderrTail.trim()
        this.#failAll(new HelperError({
          code: 'HELPER_UNAVAILABLE',
          message: `电脑操作 helper 已退出（code=${code}, signal=${signal ?? 'none'}）${detail ? `\n${detail}` : ''}`,
        }))
      })
      this.#process = child
    })()

    try {
      await this.#starting
      await this.call('ping', {}, 5000)
    } finally {
      this.#starting = null
    }
  }

  /**
   * 调用一个 helper 方法。
   *
   * @param {string} method - 方法名。
   * @param {object} [params] - 参数。
   * @param {number} [timeoutMs] - Node 侧超时（要大于 helper 自己的超时）。
   * @returns {Promise<any>} helper 的 result。
   */
  async call (method, params = {}, timeoutMs) {
    if (this.#process === null) await this.start()
    const child = this.#process
    if (child === null) throw new HelperError({ code: 'HELPER_UNAVAILABLE', message: '电脑操作 helper 不可用' })

    const id = this.#nextId++
    const budget = timeoutMs ?? (method === 'observe' ? 15000 : 12000)

    return await new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id)
        const action = ACTION_METHODS.has(method)
        reject(new HelperError({
          code: 'TIMEOUT',
          message: `电脑操作 helper 超过 ${budget} 毫秒没有响应${action ? '；该动作可能已经下发，请先重新观察再决定是否重试' : ''}`,
          actionSent: action,
        }))
      }, budget)

      this.#pending.set(id, { resolve, reject, timer })
      try {
        child.stdin.write(`${JSON.stringify({ id, method, params })}\n`)
      } catch (error) {
        clearTimeout(timer)
        this.#pending.delete(id)
        reject(new HelperError({ code: 'HELPER_UNAVAILABLE', message: `写入 helper 失败：${error.message}` }))
      }
    })
  }

  /** 停止 helper：先请求优雅退出，超时再强杀。 */
  async stop () {
    const child = this.#process
    this.#process = null
    if (child === null) return
    try {
      child.stdin.write(`${JSON.stringify({ id: this.#nextId++, method: 'shutdown', params: {} })}\n`)
    } catch { /* 管道可能已断 */ }
    await new Promise(resolve => {
      const timer = setTimeout(() => {
        try { child.kill() } catch { /* 已退出 */ }
        if (child.pid) spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true })
        resolve()
      }, 1500)
      child.once('exit', () => { clearTimeout(timer); resolve() })
    })
    this.#failAll(new HelperError({ code: 'HELPER_UNAVAILABLE', message: '电脑操作 helper 已停止' }))
  }

  // ── 内部 ─────────────────────────────────────────────────

  #onStdout (chunk) {
    this.#buffer += chunk
    let index = this.#buffer.indexOf('\n')
    while (index >= 0) {
      const line = this.#buffer.slice(0, index).trim()
      this.#buffer = this.#buffer.slice(index + 1)
      if (line !== '') this.#onLine(line)
      index = this.#buffer.indexOf('\n')
    }
  }

  #onLine (line) {
    let message
    try {
      message = JSON.parse(line)
    } catch {
      this.#logger?.warn?.(`电脑操作 helper 输出无法解析：${line.slice(0, 200)}`)
      return
    }
    const entry = this.#pending.get(message.id)
    if (entry === undefined) return
    this.#pending.delete(message.id)
    clearTimeout(entry.timer)

    if (message.ok === true) {
      entry.resolve(message.result)
      return
    }
    const error = message.error ?? {}
    entry.reject(new HelperError({
      code: error.code ?? 'INTERNAL',
      message: error.message ?? 'helper 调用失败',
      actionSent: error.actionSent === true,
    }))
  }

  #failAll (error) {
    for (const [, entry] of this.#pending) {
      clearTimeout(entry.timer)
      entry.reject(error)
    }
    this.#pending.clear()
  }
}
