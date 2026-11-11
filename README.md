# dsh-computer-use

DSH 的电脑操作插件：给模型一个 `computer` 工具，用 Windows UI Automation 读取原生应用的界面结构，并按元素索引点击、写值、按键，必要时用窗口截图确认像素结果。

网页里的操作用 `browser` 工具，两者边界清晰：这里只做原生桌面应用与系统外壳。

## 组成

```
index.js            插件入口：config、模型公告、生命周期
lib/tool.js         `computer` 工具定义与 action 分发
lib/state.js        最近一次观察的快照、元素索引、坐标换算
lib/render.js       元素树 → 模型可读文本（折叠 / 裁剪 / 注记）
lib/helper-build.js 按源码哈希编译 helper 并缓存产物
lib/helper-client.js helper 子进程的 RPC 客户端
lib/peers.js        解析宿主提供的 @deepseek-ai/dsh-tools
helper/*.cs         UI Automation / 输入合成 / 截图（.NET Framework 4.8）
```

真正的界面操作在独立子进程里：UIA 是跨进程同步调用，目标应用无响应时只废掉一个请求，不会冻住会话。

## 工具

一个工具 `computer`，`action` 判别：

| action | 主要参数 | 说明 |
|---|---|---|
| `apps` | — | 有顶层窗口的应用（pid / 名称 / exe / AUMID / 是否前台） |
| `windows` | `app` 或 `pid` | 该应用的窗口（window_id、标题、是否主窗、最小化） |
| `observe` | `app`/`pid`、`window_id?`、`screenshot?`、`full?` | 读元素树；`screenshot:true` 附带窗口截图 |
| `click` | `element_index` 或 `x`+`y` | 元素优先，坐标兜底 |
| `drag` | 起点 + `to_x`/`to_y` 或 `to_element_index` | |
| `scroll` | `element_index` 或 `x`+`y`、`direction`、`pages` | |
| `hover` | `element_index` 或 `x`+`y` | 只移动指针，让 tooltip / 悬浮菜单显形 |
| `set_value` | `element_index`、`value` | `ValuePattern`，后台写值，不抢焦点 |
| `type` | `text` | UNICODE 键流，绕过输入法 |
| `key` | `key`（如 `ctrl+s`）、`repeat`、`hold_ms` | |
| `paste` | `text` | 剪贴板 + Ctrl+V，给不认 UNICODE 键流的应用 |
| `stop` | `resume?`、`reason?` | 停止全部动作；`resume:true` 恢复 |

## 关键语义

- **元素索引**绑「该窗口最近一次观察」。元素消失由 helper 侧探活后报 `ELEMENT_UNAVAILABLE`，客户端不提前否决。
- **状态可见**：`observe` 带出 `checked`（勾选）与 `selected`（选中），滑块/数值框带出 `value`，
  模型靠这些判断操作到底生效没有——只看名字和 `actions` 是不够的。
- **动作收据**写在正文第一行。`dispatch=possibly_sent` 是错误：动作可能已下发，先重新观察，不要盲目重试非幂等动作。
- **元素动作不抢焦点**（UIA 直接调用）；坐标点击、拖拽、键盘、粘贴要先让目标窗口能接住输入。
  抢不到前台时：坐标动作把窗口临时置顶，事件仍能落上去；键盘没有位置可兜底，直接报 `FOREGROUND_DENIED` 且一个字节都不发。
- **坐标只对那张截图成立**：没有 `observe{screenshot:true}` 就报 `NO_RASTER`。换算基准是 `DWMWA_EXTENDED_FRAME_BOUNDS`（不是含阴影的 `GetWindowRect`）。窗口在截图之后被移动或改过大小，坐标动作报 `STALE_STATE`。
- **仅主 agent**：子 agent 调用会被拒绝（`SUBAGENT_DENIED`）。
- 应用名必须精确匹配；歧义报 `AMBIGUOUS_APP` 并列出 pid，模型改用 pid。

## 编译与运行

helper 源码在 `helper/`，首次使用时会用系统自带的 `csc.exe`（.NET Framework 4.8）编译到
`$DSH_HOME/plugin-data/dsh-computer-use/helper/<源码哈希>/`，之后复用；源码变了自动重编译。
不需要安装 .NET SDK、VS 或任何第三方运行时。

## 配置

| 键 | 默认 | 说明 |
|---|---|---|
| `enabled` | `true` | 总开关 |
| `announceToAgent` | `true` | 是否注入系统提示公告 |
| `maxNodes` | `300` | 单次观察的元素上限 |

## 性能参考

测于 11 个应用 / 3200×1904 屏幕 / 200% DPI：

| 操作 | 耗时 |
|---|---|
| `apps`（11 个应用） | 97ms |
| `observe` 小窗口（10 元素） | 39ms |
| `observe` 大窗口（300 元素上限） | 376ms |
| `set_value` / 元素 `click` | 2ms |
| 截图 1252×706 | 73ms |
| 截图 3200×1904 | 485ms |

`observe` 的开销几乎全在跨进程读 UIA 树：每个容器一次 `FindAll`。元素动作用 UIA pattern 直接调用，与树大小无关。
抢前台失败后有 5 秒冷却，冷却期内坐标动作直接走临时置顶，不再重复尝试。

## 测试

`test/fixture/FixtureWindow.cs` 是一个受控的 WinForms 窗口，覆盖本插件需要处理的各类控件：
菜单栏、按钮、复选框、单选按钮、下拉框、数值框、滑块、列表（20 项，可滚动）、树、选项卡、
自绘区域（无无障碍语义，只能按坐标点）、拖拽源与目标、右键菜单、Ctrl+S 快捷键。

关键在于每个动作都会把结果写进顶部的状态标签——脚本读这个标签，就能区分
「动作真的生效了」和「事件只是发出去了」。

```bash
node test/smoke.mjs   # 27 项：读取、元素动作、坐标、拖拽、键盘、悬停、错误路径
node test/tool.mjs    # 10 项：工具层注册、action 分发、错误码、渲染
```

两个脚本都自己启动并关闭受控窗口，不碰用户数据；过程中会有真实键鼠输入。
需要窗口在前台才能生效的用例（坐标、键盘、拖拽），在系统前台锁定期间会记为 SKIP 而不是假装通过。

## 已知边界

- 只支持 Windows。
- **前台锁定**：Windows 在用户最近一次输入后的 `ForegroundLockTimeout`（默认 200 秒）内，拒绝后台进程抢前台，
  `AttachThreadInput` 返回 `ACCESS_DENIED`，`SetForegroundWindow` / `SwitchToThisWindow` 都会被忽略。
  后果是**坐标点击在后台窗口上可能只完成激活、目标控件不响应**，**键盘输入完全不可用**。
  元素动作（`click` 元素、`set_value`、元素级 `scroll`）走 UIA pattern，不受影响——所以元素永远优先。
- 截图用 `PrintWindow`；全 GPU 直出的窗口可能是纯色，此时报 `SCREENSHOT_BLANK`（改用元素索引）。
- DPI 不感知的老窗口按系统缩放倍数放大截图，与肉眼所见一致。
- Chrome/Electron/UWP 的无障碍树可能很浅：网页内容请用 `browser`，必要时给浏览器加 `--force-renderer-accessibility`。
- 管理员进程受 UIPI 限制，读不到也点不动（`ELEVATED_TARGET`）。
- **WinForms 数值框不可靠**：`NumericUpDown` 对外报 0–0 的假 `RangeValue` 范围，直接设值会被控件自己拒绝。
  这种情况返回 `NOT_SETTABLE` 并说明改用 `type` 或点增减按钮，不会静默把值清零。
- **MSAA 桥接控件的事件可能不触发**：对 WinForms 列表项调用 `SelectionItemPattern.Select()` 会正确选中
  （界面上真是高亮的），但控件自己的 `SelectedIndexChanged` 不一定跑。选中状态本身可以从 `observe` 的
  `selected` 字段确认。
