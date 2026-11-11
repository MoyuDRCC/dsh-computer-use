// 输入合成与前台激活。
//
// 两条铁律：
// 1. 全局输入（键鼠）之前必须确认目标窗口真的在前台——Windows 的前台锁定会让
//    SetForegroundWindow 静默失败，不校验就等于把点击打进 DSH 自己的窗口；
// 2. 任何路径失败都要保证鼠标键已抬起，不能留下按住的按钮。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace DshComputerUse
{
    internal static class Input
    {
        private const int WheelDelta = 120;
        private const int WheelPerPage = WheelDelta * 10;

        // ── 键名 → 虚拟键码 ────────────────────────────────────

        private static readonly Dictionary<string, ushort> VirtualKeys = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            { "ctrl", 0xA2 }, { "control", 0xA2 }, { "lctrl", 0xA2 },
            { "shift", 0xA0 }, { "lshift", 0xA0 },
            { "alt", 0xA4 }, { "lalt", 0xA4 }, { "menu", 0xA4 },
            { "win", 0x5B }, { "lwin", 0x5B }, { "super", 0x5B }, { "cmd", 0x5B },
            { "return", 0x0D }, { "enter", 0x0D }, { "kp_enter", 0x0D },
            { "tab", 0x09 }, { "esc", 0x1B }, { "escape", 0x1B },
            { "space", 0x20 }, { "backspace", 0x08 }, { "back", 0x08 },
            { "delete", 0x2E }, { "del", 0x2E }, { "insert", 0x2D },
            { "home", 0x24 }, { "end", 0x23 },
            { "pageup", 0x21 }, { "prior", 0x21 }, { "pagedown", 0x22 }, { "next", 0x22 },
            { "up", 0x26 }, { "down", 0x28 }, { "left", 0x25 }, { "right", 0x27 },
            { "capslock", 0x14 }, { "numlock", 0x90 }, { "scrolllock", 0x91 },
            { "pause", 0x13 }, { "printscreen", 0x2C }, { "snapshot", 0x2C },
            { "apps", 0x5D },
            { "-", 0xBD }, { "minus", 0xBD }, { "=", 0xBB }, { "equal", 0xBB },
            { ",", 0xBC }, { "comma", 0xBC }, { ".", 0xBE }, { "period", 0xBE },
            { "/", 0xBF }, { "slash", 0xBF }, { ";", 0xBA }, { "semicolon", 0xBA },
            { "'", 0xDE }, { "quote", 0xDE }, { "[", 0xDB }, { "]", 0xDD },
            { "\\", 0xDC }, { "backslash", 0xDC }, { "`", 0xC0 }, { "grave", 0xC0 },
        };

        private static readonly Dictionary<string, ushort> ModifierKeys = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            { "ctrl", 0xA2 }, { "control", 0xA2 }, { "shift", 0xA0 }, { "alt", 0xA4 },
            { "win", 0x5B }, { "super", 0x5B }, { "cmd", 0x5B }, { "meta", 0xA4 },
        };

        private static ushort? ResolveVirtualKey(string token)
        {
            if (token == null) return null;
            string trimmed = token.Trim();
            if (trimmed.Length == 0) return null;

            ushort known;
            if (VirtualKeys.TryGetValue(trimmed, out known)) return known;

            // F1..F24
            if (trimmed.Length >= 2 && (trimmed[0] == 'F' || trimmed[0] == 'f'))
            {
                int index;
                if (int.TryParse(trimmed.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
                    && index >= 1 && index <= 24)
                {
                    return (ushort)(0x70 + index - 1);
                }
            }

            // 单字符：字母数字走 VK，其余按键已在上面的表里
            if (trimmed.Length == 1)
            {
                char c = char.ToUpperInvariant(trimmed[0]);
                if (c >= 'A' && c <= 'Z') return (ushort)c;
                if (c >= '0' && c <= '9') return (ushort)c;
            }

            return null;
        }

        // ── SendInput 基础 ─────────────────────────────────────

        private static INPUT MouseInput(uint flags, int dx, int dy, uint data)
        {
            var input = new INPUT();
            input.type = Native.INPUT_MOUSE;
            input.u.mi = new MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = data,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            };
            return input;
        }

        private static INPUT KeyInput(ushort virtualKey, ushort scan, uint flags)
        {
            var input = new INPUT();
            input.type = Native.INPUT_KEYBOARD;
            input.u.ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = scan,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            };
            return input;
        }

        private static void Send(params INPUT[] inputs)
        {
            if (inputs == null || inputs.Length == 0) return;
            uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal2.SizeOfInput());
            if (sent != inputs.Length)
            {
                throw new ProtocolException(Codes.Internal, "SendInput 被拒绝（目标可能以更高权限运行）");
            }
        }

        private static int[] ToAbsolute(int screenX, int screenY)
        {
            int left, top, width, height;
            Native.VirtualDesktop(out left, out top, out width, out height);
            long ax = (long)Math.Round((screenX - left) * 65535.0 / Math.Max(1, width - 1));
            long ay = (long)Math.Round((screenY - top) * 65535.0 / Math.Max(1, height - 1));
            return new[] { (int)ax, (int)ay };
        }

        private static void MoveTo(int screenX, int screenY)
        {
            int[] absolute = ToAbsolute(screenX, screenY);
            Send(MouseInput(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK,
                absolute[0], absolute[1], 0));
        }

        // ── 修饰键 ─────────────────────────────────────────────

        private static List<ushort> ParseModifiers(string modifiers)
        {
            var result = new List<ushort>();
            if (string.IsNullOrWhiteSpace(modifiers)) return result;
            foreach (string part in modifiers.Split('+'))
            {
                string token = part.Trim();
                if (token.Length == 0) continue;
                ushort key;
                if (ModifierKeys.TryGetValue(token, out key)) result.Add(key);
                else
                {
                    ushort? resolved = ResolveVirtualKey(token);
                    if (resolved.HasValue) result.Add(resolved.Value);
                }
            }
            return result;
        }

        private static void PressModifiers(List<ushort> keys)
        {
            foreach (ushort key in keys) Send(KeyInput(key, 0, 0));
        }

        private static void ReleaseModifiers(List<ushort> keys)
        {
            for (int i = keys.Count - 1; i >= 0; i--) Send(KeyInput(keys[i], 0, Native.KEYEVENTF_KEYUP));
        }

        // ── 鼠标 ───────────────────────────────────────────────

        public static void MouseClick(int screenX, int screenY, string button, int clickCount, string modifiers)
        {
            string normalized = (button ?? "left").Trim().ToLowerInvariant();
            uint down;
            uint up;
            switch (normalized)
            {
                case "right":
                case "r":
                    down = Native.MOUSEEVENTF_RIGHTDOWN;
                    up = Native.MOUSEEVENTF_RIGHTUP;
                    break;
                case "middle":
                case "m":
                    down = Native.MOUSEEVENTF_MIDDLEDOWN;
                    up = Native.MOUSEEVENTF_MIDDLEUP;
                    break;
                default:
                    down = Native.MOUSEEVENTF_LEFTDOWN;
                    up = Native.MOUSEEVENTF_LEFTUP;
                    break;
            }

            int times = Math.Max(1, Math.Min(3, clickCount));
            List<ushort> held = ParseModifiers(modifiers);
            MoveTo(screenX, screenY);
            PressModifiers(held);
            try
            {
                for (int i = 0; i < times; i++)
                {
                    Send(MouseInput(down, 0, 0, 0));
                    Thread.Sleep(30);
                    Send(MouseInput(up, 0, 0, 0));
                    if (i + 1 < times) Thread.Sleep(60);
                }
            }
            finally
            {
                ReleaseModifiers(held);
            }
        }

        public static void MouseDrag(int fromX, int fromY, int toX, int toY, string modifiers)
        {
            List<ushort> held = ParseModifiers(modifiers);
            MoveTo(fromX, fromY);
            PressModifiers(held);
            Send(MouseInput(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0));
            try
            {
                // 中间路径分步移动：一步到位的拖拽在多数控件里不会被识别成拖动
                for (int step = 1; step <= 12; step++)
                {
                    int x = fromX + (toX - fromX) * step / 12;
                    int y = fromY + (toY - fromY) * step / 12;
                    MoveTo(x, y);
                    Thread.Sleep(16);
                }
            }
            finally
            {
                Send(MouseInput(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0));
                ReleaseModifiers(held);
            }
        }

        /// <summary>
        /// 只把指针移到目标位置，不按键、不激活窗口。
        /// 用来让 tooltip、悬浮菜单、预览这类「停一下才出现」的元素显形。
        /// </summary>
        public static void MouseMove(int screenX, int screenY)
        {
            MoveTo(screenX, screenY);
        }

        public static void MouseScroll(int screenX, int screenY, string direction, int pages)
        {
            string normalized = (direction ?? "down").Trim().ToLowerInvariant();
            int amount = Math.Max(1, Math.Min(100, pages)) * WheelPerPage;
            MoveTo(screenX, screenY);

            uint flag;
            int delta;
            switch (normalized)
            {
                case "up":
                case "u":
                    flag = Native.MOUSEEVENTF_WHEEL; delta = amount; break;
                case "down":
                case "d":
                    flag = Native.MOUSEEVENTF_WHEEL; delta = -amount; break;
                case "left":
                case "l":
                    flag = Native.MOUSEEVENTF_HWHEEL; delta = -amount; break;
                case "right":
                case "r":
                    flag = Native.MOUSEEVENTF_HWHEEL; delta = amount; break;
                default:
                    throw new ProtocolException(Codes.InvalidArgument, "scroll direction 只能是 up/down/left/right");
            }

            Send(MouseInput(flag, 0, 0, unchecked((uint)delta)));
        }

        // ── 键盘 ───────────────────────────────────────────────

        public static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c == '\n')
                {
                    Send(KeyInput(0x0D, 0, 0), KeyInput(0x0D, 0, Native.KEYEVENTF_KEYUP));
                    continue;
                }
                if (c == '\t')
                {
                    Send(KeyInput(0x09, 0, 0), KeyInput(0x09, 0, Native.KEYEVENTF_KEYUP));
                    continue;
                }
                // KEYEVENTF_UNICODE 直接送 UTF-16 码元，绕过输入法；代理对天然拆成两次发送
                Send(
                    KeyInput(0, c, Native.KEYEVENTF_UNICODE),
                    KeyInput(0, c, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP));
                Thread.Sleep(2);
            }
        }

        public static void PressChord(string chord, int repeat, int holdMs)
        {
            if (string.IsNullOrWhiteSpace(chord))
            {
                throw new ProtocolException(Codes.InvalidArgument, "key 需要按键名或组合键");
            }

            string[] parts = chord.Split('+');
            var modifiers = new List<ushort>();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string token = parts[i].Trim();
                if (token.Length == 0) continue;
                ushort key;
                if (ModifierKeys.TryGetValue(token, out key)) modifiers.Add(key);
                else
                {
                    ushort? resolved = ResolveVirtualKey(token);
                    if (!resolved.HasValue)
                    {
                        throw new ProtocolException(Codes.InvalidArgument, "无法识别的修饰键：" + token);
                    }
                    modifiers.Add(resolved.Value);
                }
            }

            string main = parts[parts.Length - 1].Trim();
            ushort? mainKey = ResolveVirtualKey(main);
            if (!mainKey.HasValue)
            {
                throw new ProtocolException(Codes.InvalidArgument,
                    "无法识别的按键：" + main + "（可用：字母、数字、F1-F24、Return/Tab/Esc/Space/Backspace/Delete/Insert/Home/End/PageUp/PageDown/方向键，组合用 + 连接）");
            }

            int times = Math.Max(1, Math.Min(100, repeat));
            int hold = holdMs > 0 ? Math.Min(holdMs, 10000) : 30;

            for (int round = 0; round < times; round++)
            {
                PressModifiers(modifiers);
                Send(KeyInput(mainKey.Value, 0, 0));
                Thread.Sleep(hold);
                Send(KeyInput(mainKey.Value, 0, Native.KEYEVENTF_KEYUP));
                ReleaseModifiers(modifiers);
                if (round + 1 < times) Thread.Sleep(80);
            }
        }

        // ── 前台激活 ───────────────────────────────────────────

        /// <summary>解析要操作的具体窗口：给了句柄就用它，否则退回该进程的主窗口。</summary>
        public static IntPtr TargetWindow(int processId, IntPtr window)
        {
            if (window != IntPtr.Zero && Native.IsWindow(window)) return window;
            return Uia.MainWindowOf(processId);
        }

        /// <summary>
        /// 让目标窗口能接住这一次输入。
        ///
        /// 前台切换受 Windows 的前台锁定约束：用户最近有操作（ForegroundLockTimeout，默认 200 秒）时，
        /// 后台进程调 SetForegroundWindow 会被拒绝，AttachThreadInput 也返回 ACCESS_DENIED。
        /// 所以分两条路：
        /// - 需要焦点（键盘、粘贴）：必须真的切到前台，切不到就失败，绝不把按键打进别的窗口；
        /// - 只需要点击（鼠标）：切不到前台就把窗口临时置顶——鼠标事件按屏幕位置分发，
        ///   置顶窗口一定能接住；用户点一下之后前台自然就归它了。
        /// </summary>
        /// <summary>
        /// 前台锁定一旦成立就会持续很久（用户不停止操作就一直续期）。
        /// 每次动作都完整试一遍五种抢前台的办法要花两秒，连续操作时代价很直观，
        /// 所以失败后短暂记住结论，冷却期内直接走置顶这条路。
        /// </summary>
        private static int lastActivationFailure;
        private const int ActivationCooldownMs = 5000;

        public static bool EnsureInteractive(IntPtr target, bool requireFocus, out string mode)
        {
            mode = "already";
            if (target == IntPtr.Zero || !Native.IsWindow(target)) return false;
            if (Native.IsIconic(target)) Native.ShowWindow(target, Native.SW_RESTORE);
            if (Native.GetForegroundWindow() == target) return true;

            // AttachThreadInput 要求调用线程自己有输入队列，工作线程默认没有
            Native.MSG probe;
            Native.PeekMessage(out probe, IntPtr.Zero, 0, 0, 0);

            if (Environment.TickCount - lastActivationFailure >= ActivationCooldownMs)
            {
                if (TryActivate(target))
                {
                    mode = "before";
                    return true;
                }
                lastActivationFailure = Environment.TickCount;
            }

            if (requireFocus) return false;

            if (Native.SetWindowPos(target, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE))
            {
                mode = "topmost";
                return true;
            }
            return false;
        }

        /// <summary>置顶只是为了让这次鼠标事件落到正确窗口上，发完立刻恢复原状。</summary>
        public static void ReleaseTopmost(IntPtr target)
        {
            Native.SetWindowPos(target, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        private static bool TryActivate(IntPtr target)
        {
            // ① 直接试。目标已经是最上层、只是没焦点时这一步就够。
            Native.SetForegroundWindow(target);
            if (WaitForForeground(target, 300)) return true;

            // ② 走系统自己的「切换到这个窗口」路径，不受 SetForegroundWindow 的调用者限制。
            Native.SwitchToThisWindow(target, true);
            if (WaitForForeground(target, 400)) return true;

            // ③ 接上当前前台窗口的输入队列。Windows 的前台锁定按「线程输入队列」判断，
            //    接上之后本线程的请求就等同于前台线程发出的。被系统拒绝时直接跳下一步。
            IntPtr current = Native.GetForegroundWindow();
            uint unusedPid;
            uint frontThread = Native.GetWindowThreadProcessId(current, out unusedPid);
            uint thisThread = Native.GetCurrentThreadId();
            if (frontThread != 0 && frontThread != thisThread)
            {
                bool attached = Native.AttachThreadInput(thisThread, frontThread, true);
                if (attached)
                {
                    try
                    {
                        Native.BringWindowToTop(target);
                        Native.SetForegroundWindow(target);
                    }
                    finally
                    {
                        Native.AttachThreadInput(thisThread, frontThread, false);
                    }
                    if (WaitForForeground(target, 400)) return true;
                }
            }

            // ④ 换一条路：接上目标自己的线程。目标线程拥有窗口，它的输入队列更接近窗口本身。
            uint targetThread = Native.GetWindowThreadProcessId(target, out unusedPid);
            if (targetThread != 0 && targetThread != thisThread)
            {
                bool attached = Native.AttachThreadInput(thisThread, targetThread, true);
                if (attached)
                {
                    try
                    {
                        Native.BringWindowToTop(target);
                        Native.SetForegroundWindow(target);
                    }
                    finally
                    {
                        Native.AttachThreadInput(thisThread, targetThread, false);
                    }
                    if (WaitForForeground(target, 400)) return true;
                }
            }

            // ⑤ 最后手段：模拟一次 Alt。系统把「最后收到输入的进程」视为有权切换前台。
            Send(KeyInput(0x12, 0, 0), KeyInput(0x12, 0, Native.KEYEVENTF_KEYUP));
            Thread.Sleep(60);
            Native.SetForegroundWindow(target);
            return WaitForForeground(target, 500);
        }

        private static bool WaitForForeground(IntPtr target, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (Native.GetForegroundWindow() == target) return true;
                Thread.Sleep(30);
                waited += 30;
            }
            return Native.GetForegroundWindow() == target;
        }
    }

    /// <summary>Marshal.SizeOf 的薄包装，避免在热路径里反复反射。</summary>
    internal static class Marshal2
    {
        private static readonly int CachedInputSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT));
        public static int SizeOfInput() { return CachedInputSize; }
    }
}
