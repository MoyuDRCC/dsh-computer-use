// Windows UI Automation 层：应用与窗口解析、元素树遍历、元素级动作。
//
// 性能要点：
// - 走 ControlViewWalker（Raw View 全是 Pane，模型读不出结构，元素还多 5-10 倍）；
// - 所有属性走 CacheRequest，一次跨进程批量取回；不缓存的话每个属性都是一次 IPC；
// - 广度优先，达上限即停，不是抓完再裁。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Automation;

namespace DshComputerUse
{
    internal sealed class AppInfo
    {
        public int Pid;
        public string Name;
        public string Exe;
        public string Aumid;
        public bool Elevated;
        public bool Active;
        public int WindowCount;
    }

    internal sealed class WindowInfo
    {
        public IntPtr Handle;
        public int Pid;
        /// <summary>UWP 窗口归并后的真实应用进程。</summary>
        public int EffectivePid;
        public string Title;
        public bool Main;
        public bool Focused;
        public bool Minimized;
        public RECT Frame;
    }

    internal sealed class NodeInfo
    {
        public int Index;
        public int Parent;
        public int Depth;
        public string Kind;
        public string AutomationId;
        public string Name;
        public string Value;
        /// <summary>SelectionItem 的选中状态；控件不支持时为 null。</summary>
        public bool? Selected;
        /// <summary>Toggle 的勾选状态；控件不支持时为 null。</summary>
        public bool? Checked;
        public List<string> Actions = new List<string>();
        public bool Focused;
        public bool Enabled;
        public bool Editable;
        public bool Offscreen;
        public bool Secret;
        public int[] Rect;
        public int HiddenChildren;
    }

    internal sealed class Snapshot
    {
        public string StateId;
        public int Pid;
        public IntPtr Handle;
        public long WindowId;
        public string AppName;
        public string AppExe;
        public string AppAumid;
        public string WindowTitle;
        public int[] Frame;
        public bool Truncated;
        public int Total;
        public List<NodeInfo> Nodes = new List<NodeInfo>();
        public List<AutomationElement> Elements = new List<AutomationElement>();
        public bool Captured;
        public string CapturePath;
        public int CaptureWidth;
        public int CaptureHeight;
    }

    internal sealed class ActionOutcome
    {
        public string Method;
        public string Activated = "no";
    }

    internal static class Uia
    {
        private static readonly HashSet<string> SkipClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "Windows.UI.Core.CoreWindow", "ForegroundStaging", "IME",
            "MSCTFIME UI", "Default IME", "InputSiteWindowClass",
        };

        private static int stateCounter;
        private static Snapshot current;

        // ── 应用与窗口 ─────────────────────────────────────────

        public static List<WindowInfo> RawWindows()
        {
            var result = new List<WindowInfo>();
            int selfPid = Process.GetCurrentProcess().Id;
            IntPtr foreground = Native.GetForegroundWindow();
            // 进程名查询要打开进程句柄，单次枚举里复用，避免同一 pid 反复问
            var names = new Dictionary<int, string>();

            Native.EnumWindowsProc callback = delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!Native.IsWindowVisible(hWnd)) return true;
                string className = Native.WindowClass(hWnd);
                if (SkipClasses.Contains(className)) return true;

                int pid = Native.WindowProcessId(hWnd);
                if (pid == 0 || pid == selfPid) return true;

                string title = Native.WindowTitle(hWnd);
                if (title.Length == 0 && Native.IsToolWindow(hWnd)) return true;

                RECT frame = Native.FrameRect(hWnd);
                if (frame.Width <= 0 || frame.Height <= 0) return true;

                result.Add(new WindowInfo
                {
                    Handle = hWnd,
                    Pid = pid,
                    EffectivePid = EffectiveProcessId(hWnd, pid, names),
                    Title = title,
                    Minimized = Native.IsIconic(hWnd),
                    Focused = hWnd == foreground,
                    Main = false,
                    Frame = frame,
                });
                return true;
            };

            Native.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);

            // 标记每个进程的主窗口（UIA 与激活都以它为准）
            var mains = new Dictionary<int, IntPtr>();
            foreach (WindowInfo window in result)
            {
                if (window.Minimized || window.Title.Length == 0) continue;
                if (!mains.ContainsKey(window.EffectivePid)) mains[window.EffectivePid] = window.Handle;
            }
            foreach (WindowInfo window in result)
            {
                IntPtr main;
                window.Main = mains.TryGetValue(window.EffectivePid, out main) && main == window.Handle;
            }
            return result;
        }

        public static List<AppInfo> EnumerateApps()
        {
            var apps = new Dictionary<int, AppInfo>();
            var foregroundPid = Native.WindowProcessId(Native.GetForegroundWindow());

            foreach (WindowInfo window in RawWindows())
            {
                AppInfo app;
                if (!apps.TryGetValue(window.EffectivePid, out app))
                {
                    app = DescribeProcess(window.EffectivePid);
                    apps[window.EffectivePid] = app;
                }
                app.WindowCount++;
                if (window.EffectivePid == foregroundPid || window.Focused) app.Active = true;
            }

            var list = new List<AppInfo>(apps.Values);
            list.Sort(delegate(AppInfo a, AppInfo b)
            {
                if (a.Active != b.Active) return a.Active ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        public static List<WindowInfo> EnumerateWindows(int pid)
        {
            var result = new List<WindowInfo>();
            foreach (WindowInfo window in RawWindows())
            {
                if (window.EffectivePid == pid) result.Add(window);
            }
            return result;
        }

        public static IntPtr MainWindowOf(int pid)
        {
            // 只枚举一次：MainWindowOf 在每次坐标/键盘动作里都会调用
            List<WindowInfo> windows = RawWindows();
            foreach (WindowInfo window in windows)
            {
                if (window.EffectivePid == pid && window.Main && !window.Minimized) return window.Handle;
            }
            foreach (WindowInfo window in windows)
            {
                if (window.EffectivePid == pid && !window.Minimized) return window.Handle;
            }
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    return process.MainWindowHandle;
                }
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>UWP 的窗口属于 ApplicationFrameHost，真正的应用在 CoreWindow 子窗口里。</summary>
        private static int EffectiveProcessId(IntPtr hWnd, int ownerPid, Dictionary<int, string> names)
        {
            if (!string.Equals(ProcessNameOf(ownerPid, names), "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                return ownerPid;
            }
            IntPtr core = IntPtr.Zero;
            Native.EnumWindowsProc callback = delegate(IntPtr child, IntPtr lParam)
            {
                string cls = Native.WindowClass(child);
                if (cls.StartsWith("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
                {
                    core = child;
                    return false;
                }
                return true;
            };
            Native.EnumChildWindows(hWnd, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return core == IntPtr.Zero ? ownerPid : Native.WindowProcessId(core);
        }

        private static string ProcessNameOf(int pid)
        {
            return ProcessNameOf(pid, null);
        }

        private static string ProcessNameOf(int pid, Dictionary<int, string> names)
        {
            string cached;
            if (names != null && names.TryGetValue(pid, out cached)) return cached;
            string name;
            try
            {
                using (Process process = Process.GetProcessById(pid)) name = process.ProcessName;
            }
            catch (Exception)
            {
                name = string.Empty;
            }
            if (names != null) names[pid] = name;
            return name;
        }

        private static AppInfo DescribeProcess(int pid)
        {
            var info = new AppInfo { Pid = pid, Name = "pid " + pid };
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    string exe = null;
                    string description = null;
                    try
                    {
                        ProcessModule module = process.MainModule;
                        if (module != null)
                        {
                            exe = module.FileName;
                            if (module.FileVersionInfo != null) description = module.FileVersionInfo.FileDescription;
                        }
                    }
                    catch (Exception)
                    {
                        // 提权进程读 MainModule 会失败：保留 null，由调用方标注 elevated
                        info.Elevated = true;
                    }

                    if (string.IsNullOrWhiteSpace(description)) description = process.ProcessName;
                    info.Name = description;
                    info.Exe = exe;

                    try { info.Aumid = Native.ApplicationUserModelId(process.Handle); }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
                // 进程可能在枚举与查询之间退出
            }

            if (!string.IsNullOrEmpty(info.Aumid) && !string.IsNullOrWhiteSpace(info.Aumid))
            {
                string friendly = FriendlyNameFromAumid(info.Aumid);
                if (friendly != null) info.Name = friendly;
            }
            return info;
        }

        /// <summary>AUMID 形如 Microsoft.WindowsCalculator_8wekyb3d8bbwe!App，去掉发布者哈希当显示名。</summary>
        private static string FriendlyNameFromAumid(string aumid)
        {
            int bang = aumid.IndexOf('!');
            string package = bang > 0 ? aumid.Substring(0, bang) : aumid;
            int underscore = package.IndexOf('_');
            if (underscore > 0) package = package.Substring(0, underscore);
            int dot = package.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < package.Length) package = package.Substring(dot + 1);
            return package.Length == 0 ? null : package;
        }

        public static AppInfo DescribeApp(int pid)
        {
            return DescribeProcess(pid);
        }

        // ── 观察 ───────────────────────────────────────────────

        public static Snapshot Observe(int pid, long? windowId, int maxNodes, int maxDepth, int maxChildren)
        {
            IntPtr handle;
            if (windowId.HasValue)
            {
                handle = new IntPtr(windowId.Value);
                if (!Native.IsWindow(handle))
                {
                    throw new ProtocolException(Codes.WindowNotFound, "窗口 " + windowId.Value + " 已不存在，请重新调用 windows");
                }
                int owner = Native.WindowProcessId(handle);
                if (owner != pid && EffectiveProcessId(handle, owner, new Dictionary<int, string>()) != pid)
                {
                    throw new ProtocolException(Codes.WindowNotFound, "窗口 " + windowId.Value + " 不属于 pid " + pid);
                }
            }
            else
            {
                handle = MainWindowOf(pid);
                if (handle == IntPtr.Zero)
                {
                    throw new ProtocolException(Codes.WindowNotFound, "pid " + pid + " 没有可观察的窗口");
                }
            }

            AutomationElement root;
            Snapshot snapshot = new Snapshot
            {
                Pid = pid,
                Handle = handle,
                WindowId = handle.ToInt64(),
                WindowTitle = Native.WindowTitle(handle),
                StateId = "s" + (++stateCounter),
                Frame = new[] { 0, 0, 0, 0 },
            };

            RECT frame = Native.FrameRect(handle);
            snapshot.Frame = new[] { frame.Left, frame.Top, frame.Width, frame.Height };

            AppInfo app = DescribeProcess(pid);
            snapshot.AppName = app.Name;
            snapshot.AppExe = app.Exe;
            snapshot.AppAumid = app.Aumid;

            // 根元素必须在 CacheRequest 激活期间取，否则它的属性也不是缓存的。
            CacheRequest cache = BuildCacheRequest();
            try
            {
                using (cache.Activate())
                {
                    root = AutomationElement.FromHandle(handle);
                    if (root == null)
                    {
                        throw new ProtocolException(Codes.StaleState, "该窗口没有可用的无障碍根节点（可能尚未绘制完成）");
                    }

                    try { focusedElement = AutomationElement.FocusedElement; }
                    catch (Exception) { focusedElement = null; }

                    Walk(root, snapshot, maxNodes, maxDepth, maxChildren);
                }
            }
            catch (ProtocolException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ProtocolException(Codes.StaleState, "读取无障碍树失败：" + error.Message);
            }

            current = snapshot;
            return snapshot;
        }

        private static CacheRequest BuildCacheRequest()
        {
            var cache = new CacheRequest();
            cache.TreeFilter = Automation.ControlViewCondition;
            cache.AutomationElementMode = AutomationElementMode.Full;
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.AutomationIdProperty);
            cache.Add(AutomationElement.ClassNameProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.IsEnabledProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.HasKeyboardFocusProperty);
            cache.Add(AutomationElement.IsKeyboardFocusableProperty);
            cache.Add(AutomationElement.IsPasswordProperty);
            cache.Add(InvokePattern.Pattern);
            cache.Add(ValuePattern.Pattern);
            cache.Add(ExpandCollapsePattern.Pattern);
            cache.Add(TogglePattern.Pattern);
            cache.Add(SelectionItemPattern.Pattern);
            cache.Add(ScrollPattern.Pattern);
            cache.Add(RangeValuePattern.Pattern);
            return cache;
        }

        private sealed class Pending
        {
            public AutomationElement Element;
            public int Parent;
            public int Depth;
        }

        private static void Walk(AutomationElement root, Snapshot snapshot, int maxNodes, int maxDepth, int maxChildren)
        {
            var queue = new Queue<Pending>();
            queue.Enqueue(new Pending { Element = root, Parent = -1, Depth = 0 });

            while (queue.Count > 0)
            {
                if (snapshot.Nodes.Count >= maxNodes)
                {
                    snapshot.Truncated = true;
                    snapshot.Total = snapshot.Nodes.Count + queue.Count;
                    return;
                }

                Pending item = queue.Dequeue();
                NodeInfo node = BuildNode(item, snapshot.Nodes.Count);
                snapshot.Nodes.Add(node);
                snapshot.Elements.Add(item.Element);

                if (item.Depth >= maxDepth)
                {
                    node.HiddenChildren = CountChildren(item.Element);
                    if (node.HiddenChildren > 0) snapshot.Truncated = true;
                    continue;
                }

                // 用 FindAll 而不是 TreeWalker：只有 FindAll 会把当前 CacheRequest 应用到返回的元素上，
                // TreeWalker 拿到的元素没有缓存，逐个属性读都会重新 IPC。
                AutomationElementCollection children;
                try
                {
                    children = item.Element.FindAll(TreeScope.Children, Automation.ControlViewCondition);
                }
                catch (Exception)
                {
                    continue;
                }

                int produced = 0;
                foreach (AutomationElement child in children)
                {
                    if (produced >= maxChildren)
                    {
                        node.HiddenChildren = children.Count - maxChildren;
                        snapshot.Truncated = true;
                        break;
                    }
                    queue.Enqueue(new Pending { Element = child, Parent = node.Index, Depth = item.Depth + 1 });
                    produced++;
                }
            }
        }

        private static int CountChildren(AutomationElement element)
        {
            try
            {
                AutomationElementCollection children = element.FindAll(TreeScope.Children, Automation.ControlViewCondition);
                return children.Count;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static NodeInfo BuildNode(Pending item, int index)
        {
            AutomationElement element = item.Element;
            var node = new NodeInfo { Index = index, Parent = item.Parent, Depth = item.Depth };

            try
            {
                AutomationElement.AutomationElementInformation cached = element.Cached;
                node.Name = cached.Name;
                node.Kind = KindOf(cached.ControlType);
                node.AutomationId = cached.AutomationId;
                node.Enabled = cached.IsEnabled;
                node.Offscreen = cached.IsOffscreen;
                // MSAA 桥接的列表项会把 HasKeyboardFocus 全报成 true，只信它会把 20 个条目
                // 全部标成 focused。先用它筛出候选，再和系统当前焦点元素比对确认。
                node.Focused = cached.HasKeyboardFocus && IsFocusedElement(element);
                node.Secret = cached.IsPassword;

                System.Windows.Rect bounds = cached.BoundingRectangle;
                if (!bounds.IsEmpty)
                {
                    node.Rect = new[] { (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height };
                }
                else
                {
                    node.Rect = new[] { 0, 0, 0, 0 };
                }
            }
            catch (Exception error)
            {
                node.Kind = "ERR:" + error.GetType().Name;
                node.Name = error.Message;
                node.Enabled = false;
                node.Rect = new[] { 0, 0, 0, 0 };
                return node;
            }

            ReadValue(element, node);
            ReadState(element, node);
            ReadActions(element, node);
            return node;
        }

        /// <summary>
        /// 读「选中 / 勾选」这类开关状态。
        ///
        /// 模型判断「这步操作到底生效了没有」全靠这个：只看名字和 actions 无法区分
        /// 一个复选框现在是勾上还是没勾。和值一样，缓存优先，MSAA 桥接的控件再补一次 Current。
        /// </summary>
        private static void ReadState(AutomationElement element, NodeInfo node)
        {
            try
            {
                var pattern = (SelectionItemPattern)element.GetCachedPattern(SelectionItemPattern.Pattern);
                node.Selected = pattern.Cached.IsSelected;
            }
            catch (Exception) { }

            try
            {
                var pattern = (TogglePattern)element.GetCachedPattern(TogglePattern.Pattern);
                node.Checked = pattern.Cached.ToggleState == ToggleState.On;
            }
            catch (Exception) { }

            if (node.Selected.HasValue && node.Checked.HasValue) return;
            if (!NeedsPatternProbe(node.Kind)) return;

            if (!node.Selected.HasValue)
            {
                try
                {
                    var pattern = (SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern);
                    node.Selected = pattern.Current.IsSelected;
                }
                catch (Exception) { }
            }
            if (!node.Checked.HasValue)
            {
                try
                {
                    var pattern = (TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern);
                    node.Checked = pattern.Current.ToggleState == ToggleState.On;
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// 值读取。原生 UIA 提供者的元素走缓存；由 MSAA 桥接出来的控件（WinForms、老 Win32）
        /// 往往不返回缓存 pattern，这类节点额外做一次 Current 查询。
        /// 滑块、数值框、进度条没有 ValuePattern，只有 RangeValuePattern，值要单独取。
        /// </summary>
        private static void ReadValue(AutomationElement element, NodeInfo node)
        {
            if (node.Secret) return;
            try
            {
                var valuePattern = (ValuePattern)element.GetCachedPattern(ValuePattern.Pattern);
                node.Value = valuePattern.Cached.Value;
                node.Editable = !valuePattern.Cached.IsReadOnly;
                return;
            }
            catch (Exception) { }

            if (!NeedsPatternProbe(node.Kind)) return;
            try
            {
                var valuePattern = (ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern);
                node.Value = valuePattern.Current.Value;
                node.Editable = !valuePattern.Current.IsReadOnly;
                return;
            }
            catch (Exception) { }

            try
            {
                var range = (RangeValuePattern)element.GetCurrentPattern(RangeValuePattern.Pattern);
                node.Value = range.Current.Value.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception) { }
        }

        private static void ReadActions(AutomationElement element, NodeInfo node)
        {
            bool cachedWorked = TryCachedActions(element, node);
            if (cachedWorked || !NeedsPatternProbe(node.Kind)) return;

            // 桥接控件：一次 GetSupportedPatterns 拿到全部模式，比逐个 TryGetCurrentPattern 省 IPC
            try
            {
                AutomationPattern[] patterns = element.GetSupportedPatterns();
                foreach (AutomationPattern pattern in patterns)
                {
                    string name = pattern.ProgrammaticName ?? string.Empty;
                    if (name.IndexOf("InvokePattern", StringComparison.Ordinal) >= 0) AddUnique(node, "invoke");
                    else if (name.IndexOf("TogglePattern", StringComparison.Ordinal) >= 0) AddUnique(node, "toggle");
                    else if (name.IndexOf("SelectionItemPattern", StringComparison.Ordinal) >= 0) AddUnique(node, "select");
                    else if (name.IndexOf("ScrollPattern", StringComparison.Ordinal) >= 0) AddUnique(node, "scroll");
                    else if (name.IndexOf("ExpandCollapsePattern", StringComparison.Ordinal) >= 0) AddUnique(node, "expand");
                    else if (name.IndexOf("ValuePattern", StringComparison.Ordinal) >= 0 && node.Editable) AddUnique(node, "set-value");
                    else if (name.IndexOf("RangeValuePattern", StringComparison.Ordinal) >= 0) AddUnique(node, "set-range");
                }
            }
            catch (Exception) { }
        }

        private static bool TryCachedActions(AutomationElement element, NodeInfo node)
        {
            bool any = false;
            any |= TryAddAction(element, node, InvokePattern.Pattern, "invoke");
            any |= TryAddAction(element, node, TogglePattern.Pattern, "toggle");
            any |= TryAddAction(element, node, SelectionItemPattern.Pattern, "select");
            any |= TryAddAction(element, node, ScrollPattern.Pattern, "scroll");

            try
            {
                var expand = (ExpandCollapsePattern)element.GetCachedPattern(ExpandCollapsePattern.Pattern);
                ExpandCollapseState state = expand.Cached.ExpandCollapseState;
                AddUnique(node, state == ExpandCollapseState.Expanded ? "collapse" : "expand");
                any = true;
            }
            catch (Exception) { }

            if (node.Editable) AddUnique(node, "set-value");

            try
            {
                var range = (RangeValuePattern)element.GetCachedPattern(RangeValuePattern.Pattern);
                if (!range.Cached.IsReadOnly) AddUnique(node, "set-range");
                any = true;
            }
            catch (Exception) { }

            return any;
        }

        private static void AddUnique(NodeInfo node, string action)
        {
            if (!node.Actions.Contains(action)) node.Actions.Add(action);
        }

        private static bool TryAddAction(AutomationElement element, NodeInfo node, AutomationPattern pattern, string name)
        {
            try
            {
                object cached = element.GetCachedPattern(pattern);
                if (cached == null) return false;
                AddUnique(node, name);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>当前拥有键盘焦点的元素，一次观察里只取一次。</summary>
        private static AutomationElement focusedElement;

        /// <summary>和系统焦点元素比对；只在 HasKeyboardFocus 为 true 的候选上调用，开销可忽略。</summary>
        private static bool IsFocusedElement(AutomationElement element)
        {
            if (focusedElement == null) return false;
            try
            {
                return Automation.Compare(element, focusedElement);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>可能承载值或可操作性的控件类型；只有这些才值得为缓存失败再付一次 IPC。</summary>
        private static bool NeedsPatternProbe(string kind)
        {
            switch (kind)
            {
                case "Edit":
                case "Document":
                case "ComboBox":
                case "Button":
                case "CheckBox":
                case "RadioButton":
                case "MenuItem":
                case "TreeItem":
                case "ListItem":
                case "DataItem":
                case "TabItem":
                case "Hyperlink":
                case "Slider":
                case "Spinner":
                case "SplitButton":
                    return true;
                default:
                    return false;
            }
        }

        private static string KindOf(ControlType type)
        {
            if (type == null) return "Unknown";
            return type.ProgrammaticName.Replace("ControlType.", string.Empty);
        }

        // ── 元素动作 ───────────────────────────────────────────

        public static Snapshot Current { get { return current; } }

        public static AutomationElement RequireElement(int index)
        {
            if (current == null)
            {
                throw new ProtocolException(Codes.StaleState, "还没有观察过任何窗口，请先调用 observe");
            }
            if (index < 0 || index >= current.Elements.Count)
            {
                throw new ProtocolException(Codes.ElementUnavailable,
                    "元素索引 " + index + " 不在最近一次观察里（共 " + current.Elements.Count + " 个）");
            }
            AutomationElement element = current.Elements[index];
            try
            {
                ControlType probe = element.Current.ControlType;
                if (probe == null) throw new ElementNotAvailableException();
            }
            catch (Exception)
            {
                throw new ProtocolException(Codes.ElementUnavailable, "元素 " + index + " 已从界面消失，请重新 observe");
            }
            return element;
        }

        public static void AssertSnapshotMatches(int pid, long? windowId)
        {
            if (current == null)
            {
                throw new ProtocolException(Codes.StaleState, "还没有观察过任何窗口，请先调用 observe");
            }
            if (current.Pid != pid)
            {
                throw new ProtocolException(Codes.StaleState,
                    "元素索引属于 pid " + current.Pid + " 的那次观察，与请求的 pid " + pid + " 不一致，请重新 observe");
            }
            if (windowId.HasValue && current.WindowId != windowId.Value)
            {
                throw new ProtocolException(Codes.StaleState,
                    "元素索引属于窗口 " + current.WindowId + " 的那次观察，请先 observe 该窗口");
            }
        }

        public static ActionOutcome ClickElement(int index, string button, int clickCount, string modifiers)
        {
            AutomationElement element = RequireElement(index);
            string normalized = string.IsNullOrEmpty(button) ? "left" : button.Trim().ToLowerInvariant();
            bool plainLeft = (normalized == "left" || normalized == "l") && Math.Max(1, clickCount) == 1;

            if (plainLeft)
            {
                if (TryInvoke(element)) return new ActionOutcome { Method = "invoke" };
                if (TryToggle(element)) return new ActionOutcome { Method = "toggle" };
                if (TryExpandCollapse(element)) return new ActionOutcome { Method = "expand-collapse" };
                if (TrySelect(element)) return new ActionOutcome { Method = "select" };
            }

            Point center = CenterOf(element);
            return PointerClick(center.X, center.Y, normalized, clickCount, modifiers);
        }

        /// <summary>真实指针点击：先确保这次事件能落到目标窗口，再发键。</summary>
        private static ActionOutcome PointerClick(int screenX, int screenY, string button, int clickCount, string modifiers)
        {
            IntPtr target = Input.TargetWindow(current.Pid, new IntPtr(current.WindowId));
            string activated;
            if (!Input.EnsureInteractive(target, false, out activated))
            {
                throw new ProtocolException(Codes.ForegroundDenied,
                    "无法让目标窗口接住这次点击，未发送任何输入；请让用户先切到该窗口");
            }
            try
            {
                Input.MouseClick(screenX, screenY, button, clickCount, modifiers);
            }
            finally
            {
                if (activated == "topmost") Input.ReleaseTopmost(target);
            }
            return new ActionOutcome { Method = "pointer", Activated = activated };
        }

        /// <summary>
        /// 纯坐标动作的前提：既有截图，窗口从截图之后也没动过。
        /// 窗口被拖动或改了大小，那张图上的像素就不再对应屏幕上的位置，点下去会打偏。
        /// 元素动作不走这里——元素的矩形是实时读的。
        /// </summary>
        private static void AssertRasterFresh()
        {
            if (current == null || !current.Captured)
            {
                throw new ProtocolException(Codes.NoRaster,
                    "坐标动作需要先 observe{screenshot:true}，并按那张图的像素给坐标");
            }
            RECT now = Native.FrameRect(current.Handle);
            int[] frame = current.Frame;
            if (now.Left != frame[0] || now.Top != frame[1] || now.Width != frame[2] || now.Height != frame[3])
            {
                throw new ProtocolException(Codes.StaleState,
                    "窗口在截图之后移动或改变了大小，那张图的坐标已失效：请重新 observe{screenshot:true}");
            }
        }

        public static ActionOutcome ClickPoint(int screenX, int screenY, string button, int clickCount, string modifiers)
        {
            AssertRasterFresh();
            return PointerClick(screenX, screenY, button, clickCount, modifiers);
        }

        /// <summary>元素到元素的拖拽：两端坐标都是实时读的，不依赖任何截图。</summary>
        public static ActionOutcome DragElement(int fromIndex, int toX, int toY, string modifiers)
        {
            AutomationElement element = RequireElement(fromIndex);
            Point from = CenterOf(element);
            return DoDrag(from.X, from.Y, toX, toY, modifiers);
        }

        public static ActionOutcome DragPoint(int fromX, int fromY, int toX, int toY, string modifiers)
        {
            AssertRasterFresh();
            return DoDrag(fromX, fromY, toX, toY, modifiers);
        }

        private static ActionOutcome DoDrag(int fromX, int fromY, int toX, int toY, string modifiers)
        {
            IntPtr target = Input.TargetWindow(current.Pid, new IntPtr(current.WindowId));
            string activated;
            if (!Input.EnsureInteractive(target, false, out activated))
            {
                throw new ProtocolException(Codes.ForegroundDenied, "无法让目标窗口接住这次拖拽，未发送任何输入");
            }
            try
            {
                Input.MouseDrag(fromX, fromY, toX, toY, modifiers);
            }
            finally
            {
                if (activated == "topmost") Input.ReleaseTopmost(target);
            }
            return new ActionOutcome { Method = "pointer-drag", Activated = activated };
        }

        /// <summary>
        /// 悬停：只移动指针。元素矩形是实时读的，所以不要求有截图；
        /// 不激活窗口，因为悬停本身不发送任何指令，模型要激活会先点一下。
        /// </summary>
        public static ActionOutcome HoverElement(int index)
        {
            AutomationElement element = RequireElement(index);
            Point center = CenterOf(element);
            Input.MouseMove(center.X, center.Y);
            return new ActionOutcome { Method = "pointer-move" };
        }

        public static ActionOutcome HoverPoint(int screenX, int screenY)
        {
            AssertRasterFresh();
            Input.MouseMove(screenX, screenY);
            return new ActionOutcome { Method = "pointer-move" };
        }

        public static ActionOutcome ScrollElement(int index, string direction, int pages)
        {
            AutomationElement element = RequireElement(index);
            try
            {
                var scroll = (ScrollPattern)element.GetCurrentPattern(ScrollPattern.Pattern);
                int times = Math.Max(1, Math.Min(100, pages));
                for (int i = 0; i < times; i++) ScrollOnce(scroll, direction);
                return new ActionOutcome { Method = "scroll-pattern" };
            }
            catch (InvalidOperationException)
            {
                // 元素本身不能滚，退化成在它中间滚轮
            }
            catch (ElementNotAvailableException)
            {
                throw new ProtocolException(Codes.ElementUnavailable, "元素 " + index + " 已从界面消失，请重新 observe");
            }

            Point center = CenterOf(element);
            return ScrollPoint(center.X, center.Y, direction, pages);
        }

        private static void ScrollOnce(ScrollPattern scroll, string direction)
        {
            string normalized = (direction ?? "down").Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "up":
                case "u":
                    scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeDecrement);
                    break;
                case "down":
                case "d":
                    scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                    break;
                case "left":
                case "l":
                    scroll.Scroll(ScrollAmount.LargeDecrement, ScrollAmount.NoAmount);
                    break;
                case "right":
                case "r":
                    scroll.Scroll(ScrollAmount.LargeIncrement, ScrollAmount.NoAmount);
                    break;
                default:
                    throw new ProtocolException(Codes.InvalidArgument, "scroll direction 只能是 up/down/left/right");
            }
        }

        public static ActionOutcome ScrollPoint(int screenX, int screenY, string direction, int pages)
        {
            AssertRasterFresh();
            IntPtr target = Input.TargetWindow(current.Pid, new IntPtr(current.WindowId));
            string activated;
            if (!Input.EnsureInteractive(target, false, out activated))
            {
                throw new ProtocolException(Codes.ForegroundDenied, "无法让目标窗口接住这次滚轮，未发送任何输入");
            }
            try
            {
                Input.MouseScroll(screenX, screenY, direction, pages);
            }
            finally
            {
                if (activated == "topmost") Input.ReleaseTopmost(target);
            }
            return new ActionOutcome { Method = "wheel", Activated = activated };
        }

        /// <summary>
        /// 写值。文本控件走 ValuePattern；滑块、数值框、进度类只有 RangeValuePattern，
        /// 按数值设置并夹到控件自己的区间里。
        /// </summary>
        public static void SetElementValue(int index, string value)
        {
            AutomationElement element = RequireElement(index);
            string text = value ?? string.Empty;

            ValuePattern pattern;
            try
            {
                pattern = (ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern);
            }
            catch (InvalidOperationException)
            {
                SetRangeValue(element, index, text);
                return;
            }
            if (pattern.Current.IsReadOnly)
            {
                throw new ProtocolException(Codes.Disabled, "元素 " + index + " 是只读的");
            }
            try
            {
                pattern.SetValue(text);
            }
            catch (ElementNotEnabledException)
            {
                throw new ProtocolException(Codes.Disabled, "元素 " + index + " 已被禁用");
            }
        }

        private static void SetRangeValue(AutomationElement element, int index, string text)
        {
            RangeValuePattern range;
            try
            {
                range = (RangeValuePattern)element.GetCurrentPattern(RangeValuePattern.Pattern);
            }
            catch (InvalidOperationException)
            {
                throw new ProtocolException(Codes.NotSettable,
                    "元素 " + index + " 不支持直接写值；可以先用 click 聚焦，再用 type 输入");
            }
            double number;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                throw new ProtocolException(Codes.InvalidArgument, "元素 " + index + " 只接受数值，收到 " + text);
            }
            if (range.Current.IsReadOnly)
            {
                throw new ProtocolException(Codes.Disabled, "元素 " + index + " 是只读的");
            }
            // MSAA 桥接的数值控件常常报 0-0 的空范围，照那个夹取会把所有值清零；
            // 范围有效时先夹取，控件仍然拒绝就说明它对外报的范围不准，如实告诉模型换个办法。
            double minimum = range.Current.Minimum;
            double maximum = range.Current.Maximum;
            if (maximum > minimum)
            {
                number = Math.Max(minimum, Math.Min(maximum, number));
            }
            try
            {
                range.SetValue(number);
            }
            catch (ElementNotEnabledException)
            {
                throw new ProtocolException(Codes.Disabled, "元素 " + index + " 已被禁用");
            }
            catch (ArgumentException)
            {
                throw new ProtocolException(Codes.NotSettable,
                    "元素 " + index + " 拒绝被设为 " + number + "（它对外报告的范围是 "
                    + minimum.ToString(CultureInfo.InvariantCulture) + "–" + maximum.ToString(CultureInfo.InvariantCulture)
                    + "，不可信）；可以改用 type 输入数值，或点击它的增减按钮");
            }
        }

        // ── 模式辅助 ───────────────────────────────────────────

        private static bool TryInvoke(AutomationElement element)
        {
            try
            {
                var pattern = (InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern);
                pattern.Invoke();
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool TryToggle(AutomationElement element)
        {
            try
            {
                var pattern = (TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern);
                pattern.Toggle();
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool TryExpandCollapse(AutomationElement element)
        {
            try
            {
                var pattern = (ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Expanded) pattern.Collapse();
                else pattern.Expand();
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool TrySelect(AutomationElement element)
        {
            try
            {
                var pattern = (SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern);
                pattern.Select();
                return true;
            }
            catch (Exception) { return false; }
        }

        private static Point CenterOf(AutomationElement element)
        {
            System.Windows.Rect bounds;
            try { bounds = element.Current.BoundingRectangle; }
            catch (Exception)
            {
                throw new ProtocolException(Codes.ElementUnavailable, "取不到元素位置（可能已消失），请重新 observe");
            }
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                throw new ProtocolException(Codes.ElementUnavailable, "元素没有可见矩形，无法点击；请改用键盘或父级元素");
            }
            return new Point((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2));
        }

        public static void Reset()
        {
            current = null;
        }
    }
}
