# dsh-computer-use

![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![license](https://img.shields.io/badge/license-MIT-green)

给 DSH（DeepSeek Harness）的电脑操作插件：注册一个 `computer` 工具，让模型用 Windows UI Automation 读取原生应用的界面结构，再按**元素索引**点击、写值、按键，必要时用窗口截图确认像素结果。

网页里的操作交给 `browser` 工具，这里只负责原生桌面应用与系统外壳——两者的边界是「有没有无障碍树可以走」。

## 为什么不是「截图 + 猜坐标」

- **元素优先**：`click` 元素、`set_value`、元素级 `scroll` 都走 UI Automation pattern，后台窗口可用、不抢焦点、与窗口大小和 DPI 无关。
- **状态可验证**：`observe` 带出 `checked` / `selected` / `value`，模型能判断操作是否真的生效，而不是只看事件发没发出去。
- **不冻会话**：界面操作跑在独立的 C# 子进程里，目标应用卡死只会废掉一个请求。
- **零依赖**：helper 用系统自带的 `csc.exe`（.NET Framework 4.x）首次运行时编译，不需要装 SDK、VS 或任何运行时。

## 环境要求

| 项 | 要求 |
|---|---|
| 系统 | Windows 10 / 11（x64 或 ARM64，helper 为 AnyCPU） |
| 运行时 | .NET Framework 4.x（Win10/11 自带） |
| DSH | 支持 cordis profile bundle 的发行版 |
| Node.js | ≥ 20（仅 DSH 侧需要） |

## 安装

作为 profile bundle 插件挂到 DSH 上：

```powershell
git clone https://github.com/MoyuDRCC/dsh-computer-use.git E:\plugins\dsh-computer-use
```

编辑当前 profile 的 `package.json`（例如 `~/.dsh/profiles/web/package.json`），把它加入依赖与 bundle 列表：

```json
{
  "dependencies": {
    "dsh-computer-use": "link:E:/plugins/dsh-computer-use"
  },
  "dsh": {
    "profile": {
      "bundles": ["dsh-computer-use"]
    }
  }
}
```

在 profile 目录执行一次依赖安装，然后重启 DSH：

```powershell
cd $env:USERPROFILE\.dsh\profiles\web
pnpm install
```

包内的 `cordis.patch.yml` 会自动把 `computer-use` 插件行插进插件表，无需手写 patch。重启后工具面板出现 `computer` 即安装成功。

## 快速开始

**1. 看有哪些应用**

```json
{ "action": "apps" }
```

返回每个有顶层窗口的应用：pid、显示名、exe、AUMID、是否前台。

**2. 读界面结构**

```json
{ "action": "observe", "app": "notepad.exe" }
```

返回无障碍树，每行以元素索引开头，索引直接用于后续动作。元素表达不了时才用截图：`{"action":"observe","app":"notepad.exe","screenshot":true}` 会附带一张窗口截图并启用坐标动作。

**3. 操作并复查**

```json
{ "action": "set_value", "element_index": 12, "value": "hello" }
```

动作完成后**再 observe 一次**，确认界面确实变成了预期状态——只看收据不足以证明界面动了。

## 工具参考

单一工具 `computer`，靠 `action` 判别：

| action | 主要参数 | 说明 |
|---|---|---|
| `apps` | — | 有顶层窗口的应用（pid / 名称 / exe / AUMID / 是否前台） |
| `windows` | `app` 或 `pid` | 该应用的窗口（window_id、标题、是否主窗、是否最小化） |
| `observe` | `app`/`pid`、`window_id?`、`screenshot?`、`full?` | 读元素树；`screenshot:true` 附带截图 |
| `click` | `element_index` 或 `x`+`y` | 元素优先，坐标兜底；配合 `button` / `click_count` / `modifiers` |
| `drag` | 起点 + `to_element_index` 或 `to_x`+`to_y` | 元素或坐标拖拽 |
| `scroll` | `element_index` 或 `x`+`y`、`direction`、`pages` | 元素级滚动不抢焦点 |
| `hover` | `element_index` 或 `x`+`y` | 只移指针，让 tooltip / 悬浮菜单显形 |
| `set_value` | `element_index`、`value` | ValuePattern 直接写值，后台可用 |
| `type` | `text` | UNICODE 键流，绕过输入法 |
| `key` | `key`（如 `ctrl+s`、`Return`）、`repeat`、`hold_ms` | 组合键与按键序列 |
| `paste` | `text` | 剪贴板 + Ctrl+V，给不认 UNICODE 键流的应用 |
| `stop` | `resume?`、`reason?` | 停止全部动作；`resume:true` 恢复 |

通用参数：`app` / `pid` 二选一定位应用（同名应用请用 `pid`），`window_id` 限定窗口（缺省用主窗口）。

## 关键语义

- **元素索引绑定「该窗口最近一次观察」**。元素消失由 helper 侧探活后报 `ELEMENT_UNAVAILABLE`，客户端不提前否决。
- **动作收据**写在正文第一行。出现 `dispatch=possibly_sent` 意味着动作可能已经下发——先重新观察，别盲目重试非幂等动作。
- **元素动作不抢焦点**（UIA pattern 直接调用）；坐标、拖拽、键盘、粘贴是全局输入，工具会先把目标窗口带到前台，抢不到就拒绝而不是打到别的窗口。
- **坐标只对那张截图成立**：没有 `observe{screenshot:true}` 就报 `NO_RASTER`；窗口在截图后被移动或缩放报 `STALE_STATE`。换算基准是 `DWMWA_EXTENDED_FRAME_BOUNDS`（不含阴影），与肉眼看到的边界一致。
- **仅主 agent**：子 agent 调用会被拒绝（`SUBAGENT_DENIED`）。
- **应用名必须精确匹配**（显示名 / exe 文件名 / AUMID），歧义报 `AMBIGUOUS_APP` 并列出候选 pid。

## 错误码

错误码由 helper 唯一定义、原样透传给模型：

| 码 | 含义 | 处理方式 |
|---|---|---|
| `APP_NOT_FOUND` | 应用名匹配不到 | 从报错给出的候选里挑，或改用 `apps` 查 pid |
| `AMBIGUOUS_APP` | 同名应用多个 | 改用 `pid` |
| `WINDOW_NOT_FOUND` | 窗口不存在 | 重新 `windows` |
| `ELEMENT_UNAVAILABLE` | 元素已消失 | 重新 `observe`，用新索引 |
| `STALE_STATE` | 截图后窗口变了 | 重新 `observe{screenshot:true}` |
| `NOT_SETTABLE` | 元素不支持写值 | 改用 `type` 或点增减按钮 |
| `DISABLED` | 元素被禁用 | 先让前置条件满足 |
| `NO_RASTER` | 没有坐标基准 | 先取截图 |
| `FOREGROUND_DENIED` | 抢不到前台 | 改用元素动作，或让用户先点一下目标窗口 |
| `ELEVATED_TARGET` | 目标以管理员运行 | UIPI 限制，需同权限运行 DSH |
| `SCREENSHOT_UNAVAILABLE` | 截图失败 | 用元素索引继续 |
| `SCREENSHOT_BLANK` | 截到纯色 | 全 GPU 直出窗口，改用元素索引 |
| `INVALID_ARGUMENT` | 参数不合法 | 按报错修正 |
| `TIMEOUT` | helper 超时 | 目标应用可能卡死，重试或重启应用 |
| `HELPER_BUSY` | helper 忙 | 稍后重试 |
| `INTERNAL` | 内部错误 | 附带堆栈，可用于报 issue |

## 配置

| 键 | 默认 | 说明 |
|---|---|---|
| `enabled` | `true` | 总开关 |
| `announceToAgent` | `true` | 是否向模型注入使用公告（关闭可省系统提示） |
| `maxNodes` | `300` | 单次观察的元素上限 |

## 工作原理

```
index.js            插件入口：config、模型公告、生命周期
lib/tool.js         computer 工具定义与 action 分发
lib/state.js        最近一次观察的快照、元素索引、坐标换算
lib/render.js       元素树 → 模型可读文本（折叠 / 裁剪 / 注记）
lib/helper-build.js 按源码哈希编译 helper 并缓存产物
lib/helper-client.js helper 子进程的 RPC 客户端
lib/peers.js        解析宿主提供的 @deepseek-ai/dsh-tools
helper/*.cs         UI Automation / 输入合成 / 截图（.NET Framework 4.8）
```

DSH 主机进程通过 stdio（父子管道、无端口无 token）驱动一个常驻的 `ComputerUseHelper.exe`：

```
DSH Host
 └─ computer 工具
      └─ ComputerUseHelper.exe（常驻子进程）
           ├─ Program.cs    主循环 / 串行 worker / 超时 / 看门狗 / 协议
           ├─ Uia.cs        应用与窗口解析、缓存遍历、语义动作
           ├─ Input.cs      SendInput、虚拟桌面坐标、前台激活
           ├─ Clipboard.cs  剪贴板读写
           └─ Capture.cs    PrintWindow → BitBlt 回退 → PNG
```

**为什么是子进程**：UIA 是跨进程同步调用，目标应用无响应时调用线程会被钉住；放在子进程里只废掉一个请求，放在主机进程里会冻住整条会话。
**为什么常驻**：UIA 首次调用要建 COM 代理（冷启 300–800ms），常驻后单次观察 20–80ms。

helper 源码首次使用时编译到 `$DSH_HOME/plugin-data/dsh-computer-use/helper/<源码哈希>/`，源码变更自动重编译，旧版本目录自动清理。

## 性能

测于 11 个应用 / 3200×1904 屏幕 / 200% DPI：

| 操作 | 耗时 |
|---|---|
| `apps`（11 个应用） | 97ms |
| `observe` 小窗口（10 元素） | 39ms |
| `observe` 大窗口（300 元素上限） | 376ms |
| `set_value` / 元素 `click` | 2ms |
| 截图 1252×706 | 73ms |
| 截图 3200×1904 | 485ms |

`observe` 的开销几乎全在跨进程读 UIA 树（每个容器一次 `FindAll`）；元素动作用 pattern 直接调用，与树大小无关。

## 测试

`test/fixture/FixtureWindow.cs` 是受控的 WinForms 窗口，覆盖菜单栏、按钮、复选框、单选按钮、下拉框、数值框、滑块、列表、树、选项卡、自绘区域、拖拽源与目标、右键菜单、快捷键。每个动作都把结果写进顶部状态标签——脚本读标签，就能区分「动作真的生效了」和「事件只是发出去了」。

```bash
node test/smoke.mjs   # 27 项：读取、元素动作、坐标、拖拽、键盘、悬停、错误路径
node test/tool.mjs    # 10 项：工具层注册、action 分发、错误码、渲染
```

两个脚本自己启停受控窗口，不碰用户数据；过程中会有真实键鼠输入。
需要窗口在前台的用例（坐标、键盘、拖拽）在系统前台锁定期间记为 SKIP，不会假装通过。

## 已知边界

- 只支持 Windows。
- **前台锁定**：Windows 在用户最近一次输入后的 `ForegroundLockTimeout`（默认 200 秒）内拒绝后台进程抢前台。后果是坐标点击可能只完成激活、控件不响应，键盘输入完全不可用。元素动作走 UIA pattern，不受影响——所以元素永远优先。
- 截图用 `PrintWindow`，全 GPU 直出的窗口可能截成纯色（`SCREENSHOT_BLANK`），此时改用元素索引。
- DPI 不感知的老窗口按系统缩放倍数放大截图，与肉眼所见一致。
- Chrome / Electron / UWP 的无障碍树可能很浅：网页内容请用 `browser` 工具，必要时给浏览器加 `--force-renderer-accessibility`。
- 管理员进程受 UIPI 限制，读不到也点不动（`ELEVATED_TARGET`），需要以同权限运行 DSH。
- **WinForms 数值框不可靠**：`NumericUpDown` 对外报 0–0 的假 `RangeValue`，直接设值会被控件拒绝，返回 `NOT_SETTABLE` 并建议改用 `type` 或点增减按钮。
- **MSAA 桥接控件的事件可能不触发**：对 WinForms 列表项调用 `SelectionItemPattern.Select()` 会正确选中，但控件自己的 `SelectedIndexChanged` 不一定执行；选中状态可从 `observe` 的 `selected` 字段确认。

## 许可证

MIT
