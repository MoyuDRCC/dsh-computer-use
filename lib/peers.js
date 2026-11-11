/**
 * 解析 DSH 宿主提供的 peer 包（这里只用到 `@deepseek-ai/dsh-tools` 的 defineTool）。
 *
 * 插件被宿主加载时，DSH 自己的包不在本插件的依赖树里：ESM 不会像 CommonJS 那样
 * 顺着 profile 的 node_modules 往上找。所以先直接解析，失败后再按「本包祖先 →
 * $DSH_PROFILE_DIR → $DSH_HOME/profiles/<name> → 部署自带 app.asar」的顺序重启
 * 一个 require 去试。
 *
 * @module dsh-computer-use/peers
 */

import { existsSync } from 'node:fs'
import { createRequire } from 'node:module'
import { homedir } from 'node:os'
import { delimiter, dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const localRequire = createRequire(import.meta.url)
const here = dirname(fileURLToPath(import.meta.url))
const cache = new Map()

/** 候选 node_modules 根目录，按可信度排序；只返回真实存在的目录。 */
function candidateRoots () {
  const roots = [join(here, '..', 'node_modules')]
  const explicit = process.env.DSH_PEER_ROOT
  if (typeof explicit === 'string' && explicit !== '') {
    for (const root of explicit.split(delimiter)) {
      if (root !== '') roots.push(root)
    }
  }
  const dshHome = process.env.DSH_HOME ?? join(homedir(), '.dsh')
  const profileDir = process.env.DSH_PROFILE_DIR
  if (typeof profileDir === 'string' && profileDir !== '') roots.push(join(profileDir, 'node_modules'))
  const profile = process.env.DSH_PROFILE
  if (typeof profile === 'string' && profile !== '') roots.push(join(dshHome, 'profiles', profile, 'node_modules'))
  for (const name of ['desktop', 'default', 'web']) {
    roots.push(join(dshHome, 'profiles', name, 'node_modules'))
  }
  for (const resources of resourcesRoots()) {
    roots.push(join(resources, 'app.asar', 'dsh', 'node_modules'))
    roots.push(join(resources, 'app.asar', 'node_modules'))
    roots.push(join(resources, 'app.asar.unpacked', 'dsh', 'node_modules'))
  }
  return roots.filter(root => existsSync(root))
}

/** 当前部署的 Electron resources 目录（纯 Node 部署时为空）。 */
function resourcesRoots () {
  const candidates = []
  if (typeof process.resourcesPath === 'string' && process.resourcesPath !== '') {
    candidates.push(process.resourcesPath)
  }
  if (typeof process.execPath === 'string' && process.execPath !== '') {
    candidates.push(join(dirname(process.execPath), 'resources'))
  }
  return candidates.filter(root => existsSync(root))
}

/**
 * require 一个 DSH peer 包。
 *
 * @param {string} specifier - 包说明符。
 * @returns {any} 加载到的模块。
 * @throws {Error} 所有候选根都找不到时。
 */
export function requirePeer (specifier) {
  if (cache.has(specifier)) return cache.get(specifier)
  try {
    const direct = localRequire(specifier)
    cache.set(specifier, direct)
    return direct
  } catch (directError) {
    const attempts = []
    for (const root of candidateRoots()) {
      try {
        const loaded = createRequire(join(root, 'noop.cjs'))(specifier)
        cache.set(specifier, loaded)
        return loaded
      } catch (error) {
        attempts.push(`${root}: ${error.code ?? error.message}`)
      }
    }
    const detail = attempts.length === 0 ? '没有可用的 node_modules 根目录' : attempts.join('; ')
    throw new Error(`computer-use: 无法解析 DSH peer 包 ${JSON.stringify(specifier)}（${detail}）`, { cause: directError })
  }
}
