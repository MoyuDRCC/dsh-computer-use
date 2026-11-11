/**
 * helper 的编译与产物缓存。
 *
 * 源码是唯一事实源：每次启动按 helper/*.cs 的内容哈希定位产物目录，命中即复用，
 * 未命中才调用系统自带的 csc.exe 编译一次。产物目录里只保留当前版本，避免长期堆积。
 *
 * @module dsh-computer-use/helper-build
 */

import { createHash } from 'node:crypto'
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const helperSourceDir = join(here, '..', 'helper')

/** 参与哈希的源文件（顺序固定，保证哈希稳定）。 */
const SOURCES = [
  'Json.cs',
  'Codes.cs',
  'Native.cs',
  'Input.cs',
  'Clipboard.cs',
  'Capture.cs',
  'Uia.cs',
  'Program.cs',
]

/** 需要引用的 .NET Framework 程序集。 */
const REFERENCES = ['UIAutomationClient.dll', 'UIAutomationTypes.dll', 'WindowsBase.dll', 'System.Drawing.dll']

/**
 * 返回插件的数据目录（与 browser-control 相同的 plugin-data 约定）。
 *
 * @returns {string} 绝对路径。
 */
export function resolveDataDir () {
  const dshHome = process.env.DSH_HOME ?? join(homedir(), '.dsh')
  return join(dshHome, 'plugin-data', 'dsh-computer-use')
}

/**
 * 按源码内容算出版本哈希。
 *
 * @returns {string} 12 位十六进制。
 */
function sourceHash () {
  const hash = createHash('sha256')
  for (const name of SOURCES) {
    hash.update(name)
    hash.update(readFileSync(join(helperSourceDir, name)))
  }
  return hash.digest('hex').slice(0, 12)
}

/** 找到 .NET Framework 4.x 的 csc.exe。 */
function findCompiler () {
  const windir = process.env.WINDIR ?? 'C:\\Windows'
  const candidates = [
    join(windir, 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe'),
    join(windir, 'Microsoft.NET', 'Framework', 'v4.0.30319', 'csc.exe'),
  ]
  for (const candidate of candidates) {
    if (existsSync(candidate)) return candidate
  }
  throw new Error(
    '找不到 .NET Framework 编译器 csc.exe（应当在 %WINDIR%\\Microsoft.NET\\Framework64\\v4.0.30319）。' +
    '电脑操作插件需要 .NET Framework 4.x，Windows 10/11 自带；若被裁剪请先修复系统组件。',
  )
}

/** 找到 UIAutomation 等引用程序集所在目录。 */
function findReferenceDir () {
  const candidates = [
    join(process.env['ProgramFiles(x86)'] ?? 'C:\\Program Files (x86)', 'Reference Assemblies', 'Microsoft', 'Framework', '.NETFramework', 'v4.8'),
    join(process.env.WINDIR ?? 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'WPF'),
  ]
  for (const candidate of candidates) {
    if (REFERENCES.every(name => existsSync(join(candidate, name)))) return candidate
  }
  throw new Error(
    '找不到 .NET Framework 4.8 的引用程序集（UIAutomationClient.dll 等）。' +
    '请确认系统已安装 .NET Framework 4.8 运行时。',
  )
}

/** 删掉旧版本的产物目录，只保留当前这一份。 */
function pruneOtherVersions (root, keep) {
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    if (!entry.isDirectory() || entry.name === keep) continue
    try {
      rmSync(join(root, entry.name), { recursive: true, force: true })
    } catch {
      // 有进程正在用旧产物时删不掉，下次启动再试
    }
  }
}

/**
 * 确保 helper 可执行文件存在，必要时编译。
 *
 * @param {object} [options] - 选项。
 * @param {() => void} [options.onCompile] - 首次编译前的回调（用于提示）。
 * @returns {{ exePath: string, compiled: boolean }} 产物路径与是否刚刚编译。
 */
export function ensureHelper ({ onCompile } = {}) {
  const root = join(resolveDataDir(), 'helper')
  const version = sourceHash()
  const directory = join(root, version)
  const exePath = join(directory, 'ComputerUseHelper.exe')

  if (existsSync(exePath) && statSync(exePath).size > 0) {
    pruneOtherVersions(root, version)
    return { exePath, compiled: false }
  }

  mkdirSync(directory, { recursive: true })
  pruneOtherVersions(root, version)

  if (typeof onCompile === 'function') {
    try { onCompile() } catch { /* 提示失败不影响编译 */ }
  }

  const compiler = findCompiler()
  const referenceDir = findReferenceDir()
  const args = [
    '/nologo', '/noconfig', '/target:exe', '/optimize+', '/platform:anycpu',
    `/out:${exePath}`,
    ...REFERENCES.map(name => `/r:${join(referenceDir, name)}`),
    '/r:System.dll', '/r:System.Core.dll',
    ...SOURCES.map(name => join(helperSourceDir, name)),
  ]

  const result = spawnSync(compiler, args, { encoding: 'utf8', windowsHide: true })
  if (result.status !== 0 || !existsSync(exePath)) {
    const detail = `${result.stdout ?? ''}${result.stderr ?? ''}`.trim()
    throw new Error(
      '编译电脑操作 helper 失败。\n' +
      `编译器：${compiler}\n` +
      `引用目录：${referenceDir}\n` +
      `输出：\n${detail || '（csc 没有输出）'}`,
    )
  }
  return { exePath, compiled: true }
}
