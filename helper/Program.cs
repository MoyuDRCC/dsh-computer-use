// 进程主体：stdin 读请求、串行 worker 执行 UIA、stdout 写响应。
//
// 三条硬约束：
// 1. stdout 只走协议行。Console.Out 在启动时被重定向到 stderr，任何意外输出都不会污染协议。
// 2. UIA 调用全部在单个 MTA worker 线程里串行执行；某个应用无响应时只废掉一个请求。
// 3. 结果由主线程写回。worker 只填结果不写流，所以超时之后不会冒出过期响应。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace DshComputerUse
{
    internal static class Program
    {
        private sealed class Job
        {
            public Dictionary<string, object> Request;
            public Dictionary<string, object> Response;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static readonly HashSet<string> ActionMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "click", "drag", "scroll", "set_value", "type", "key", "paste",
        };

        private static StreamWriter protocol;

        private static int Main(string[] args)
        {
            Native.DeclarePerMonitorDpiAware();

            protocol = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 8192);
            protocol.AutoFlush = true;
            Console.SetOut(Console.Error);
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch (Exception) { }

            int parentPid = ReadParentPid(args);
            if (parentPid > 0) StartWatchdog(parentPid);

            var queue = new BlockingCollection<Job>();
            var worker = new Thread(delegate() { WorkerLoop(queue); });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();

            Job running = null;
            string line;
            var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;

                Dictionary<string, object> request;
                try
                {
                    request = Json.Object(Json.Parse(line));
                }
                catch (Exception error)
                {
                    WriteError(0, Codes.InvalidArgument, "请求不是合法 JSON：" + error.Message, false);
                    continue;
                }
                if (request == null)
                {
                    WriteError(0, Codes.InvalidArgument, "请求必须是 JSON 对象", false);
                    continue;
                }

                int id = Json.Int(request, "id", 0);
                string method = Json.String(request, "method");
                if (string.IsNullOrEmpty(method))
                {
                    WriteError(id, Codes.InvalidArgument, "请求缺少 method", false);
                    continue;
                }

                if (method == "shutdown")
                {
                    WriteOk(id, new Dictionary<string, object> { { "stopped", true } });
                    break;
                }

                if (running != null && running.Done.IsSet) running = null;
                if (running != null)
                {
                    WriteError(id, Codes.HelperBusy, "上一个请求仍在执行（目标应用可能无响应），请稍后重试", false);
                    continue;
                }

                var job = new Job { Request = request };
                running = job;
                queue.Add(job);

                Dictionary<string, object> parameters = Json.Object(request.ContainsKey("params") ? request["params"] : null);
                int timeout = Json.Int(parameters, "timeoutMs", 8000);
                if (!job.Done.Wait(timeout))
                {
                    bool action = ActionMethods.Contains(method);
                    WriteError(id, Codes.Timeout,
                        "请求超过 " + timeout + " 毫秒未完成，目标应用可能无响应" +
                        (action ? "；该动作可能已经下发" : ""),
                        action);
                    continue;
                }
                WriteRaw(job.Response);
            }

            try { queue.CompleteAdding(); }
            catch (Exception) { }
            Uia.Reset();
            return 0;
        }

        private static void WorkerLoop(BlockingCollection<Job> queue)
        {
            foreach (Job job in queue.GetConsumingEnumerable())
            {
                try
                {
                    job.Response = Dispatch(job.Request);
                }
                catch (ProtocolException error)
                {
                    job.Response = ErrorEnvelope(Json.Int(job.Request, "id", 0), error.Code, error.Message, error.ActionSent);
                }
                catch (Exception error)
                {
                    job.Response = ErrorEnvelope(Json.Int(job.Request, "id", 0), Codes.Internal,
                        error.GetType().Name + ": " + error.Message, ActionMethods.Contains(Json.String(job.Request, "method")));
                }
                job.Done.Set();
            }
        }

        // ── 分发 ───────────────────────────────────────────────

        private static Dictionary<string, object> Dispatch(Dictionary<string, object> request)
        {
            int id = Json.Int(request, "id", 0);
            string method = Json.String(request, "method");
            Dictionary<string, object> parameters = Json.Object(request.ContainsKey("params") ? request["params"] : null)
                ?? new Dictionary<string, object>();

            switch (method)
            {
                case "ping":
                    return OkEnvelope(id, new Dictionary<string, object> { { "ready", true }, { "pid", Process.GetCurrentProcess().Id } });
                case "apps":
                    return OkEnvelope(id, AppsResult());
                case "windows":
                    return OkEnvelope(id, WindowsResult(parameters));
                case "observe":
                    return OkEnvelope(id, ObserveResult(parameters));
                case "click":
                    return OkEnvelope(id, ClickResult(parameters));
                case "drag":
                    return OkEnvelope(id, DragResult(parameters));
                case "hover":
                    return OkEnvelope(id, HoverResult(parameters));
                case "scroll":
                    return OkEnvelope(id, ScrollResult(parameters));
                case "set_value":
                    return OkEnvelope(id, SetValueResult(parameters));
                case "type":
                    return OkEnvelope(id, TypeResult(parameters));
                case "key":
                    return OkEnvelope(id, KeyResult(parameters));
                case "paste":
                    return OkEnvelope(id, PasteResult(parameters));
                default:
                    throw new ProtocolException(Codes.InvalidArgument, "未知方法：" + method);
            }
        }

        private static Dictionary<string, object> AppsResult()
        {
            var list = new List<object>();
            foreach (AppInfo app in Uia.EnumerateApps())
            {
                list.Add(new Dictionary<string, object>
                {
                    { "pid", app.Pid },
                    { "name", app.Name },
                    { "exe", app.Exe },
                    { "aumid", app.Aumid },
                    { "elevated", app.Elevated },
                    { "active", app.Active },
                    { "windows", app.WindowCount },
                });
            }
            return new Dictionary<string, object> { { "apps", list } };
        }

        private static Dictionary<string, object> WindowsResult(Dictionary<string, object> parameters)
        {
            int pid = RequiredPid(parameters);
            var list = new List<object>();
            foreach (WindowInfo window in Uia.EnumerateWindows(pid))
            {
                list.Add(new Dictionary<string, object>
                {
                    { "windowId", window.Handle.ToInt64() },
                    { "title", window.Title },
                    { "main", window.Main },
                    { "focused", window.Focused },
                    { "minimized", window.Minimized },
                    { "rect", new[] { window.Frame.Left, window.Frame.Top, window.Frame.Width, window.Frame.Height } },
                });
            }
            AppInfo app = Uia.DescribeApp(pid);
            return new Dictionary<string, object>
            {
                { "app", AppPayload(app) },
                { "windows", list },
            };
        }

        private static Dictionary<string, object> ObserveResult(Dictionary<string, object> parameters)
        {
            int pid = RequiredPid(parameters);
            long? windowId = Json.NullableLong(parameters, "windowId");
            int maxNodes = Json.Int(parameters, "maxNodes", 300);
            int maxDepth = Json.Int(parameters, "maxDepth", 16);
            int maxChildren = Json.Int(parameters, "maxChildren", 60);
            maxNodes = Math.Max(20, Math.Min(5000, maxNodes));
            maxDepth = Math.Max(2, Math.Min(40, maxDepth));
            maxChildren = Math.Max(1, Math.Min(500, maxChildren));

            Snapshot snapshot = Uia.Observe(pid, windowId, maxNodes, maxDepth, maxChildren);

            string capturePath = Json.String(parameters, "capturePath");
            if (Json.Bool(parameters, "capture", false) && !string.IsNullOrEmpty(capturePath))
            {
                BitmapRect box = Capture.CaptureWindow(new IntPtr(snapshot.WindowId), capturePath);
                snapshot.Captured = true;
                snapshot.CapturePath = capturePath;
                snapshot.CaptureWidth = box.Width;
                snapshot.CaptureHeight = box.Height;
            }

            return SnapshotPayload(snapshot);
        }

        private static Dictionary<string, object> ClickResult(Dictionary<string, object> parameters)
        {
            int pid = RequiredPid(parameters);
            long? windowId = Json.NullableLong(parameters, "windowId");
            int? index = Json.NullableInt(parameters, "elementIndex");
            string button = Json.String(parameters, "button") ?? "left";
            int clickCount = Json.Int(parameters, "clickCount", 1);
            string modifiers = Json.String(parameters, "modifiers");

            ActionOutcome outcome;
            if (index.HasValue)
            {
                Uia.AssertSnapshotMatches(pid, windowId);
                outcome = Uia.ClickElement(index.Value, button, clickCount, modifiers);
            }
            else
            {
                int x = RequiredInt(parameters, "screenX");
                int y = RequiredInt(parameters, "screenY");
                outcome = Uia.ClickPoint(x, y, button, clickCount, modifiers);
            }
            return ActionPayload(outcome);
        }

        private static Dictionary<string, object> DragResult(Dictionary<string, object> parameters)
        {
            int pid = RequiredPid(parameters);
            long? windowId = Json.NullableLong(parameters, "windowId");
            int? fromIndex = Json.NullableInt(parameters, "fromElementIndex");
            string modifiers = Json.String(parameters, "modifiers");
            int toX = RequiredInt(parameters, "screenToX");
            int toY = RequiredInt(parameters, "screenToY");

            ActionOutcome outcome;
            if (fromIndex.HasValue)
            {
                Uia.AssertSnapshotMatches(pid, windowId);
                outcome = Uia.DragElement(fromIndex.Value, toX, toY, modifiers);
            }
            else
            {
                int fromX = RequiredInt(parameters, "screenFromX");
                int fromY = RequiredInt(parameters, "screenFromY");
                outcome = Uia.DragPoint(fromX, fromY, toX, toY, modifiers);
            }
            return ActionPayload(outcome);
        }

        private static Dictionary<string, object> HoverResult(Dictionary<string, object> parameters)
        {
            int pid = RequiredPid(parameters);
            long? windowId = Json.NullableLong(parameters, "windowId");
            int? index = Json.NullableInt(parameters, "elementIndex");

            ActionOutcome outcome;
            if (index.HasValue)
            {
                Uia.AssertSnapshotMatches(pid, windowId);
                outcome = Uia.HoverElement(index.Value);
            }
            else
            {
                int x = RequiredInt(parameters, "screenX");
                int y = RequiredInt(parameters, "screenY");
                outcome = Uia.HoverPoint(x, y);
            }
            return ActionPayload(outcome);
        }

        private static Dictionary<string, object> ScrollResult(Dictionary<string, object> parameters)
        {
            int pid = RequiredPid(parameters);
            long? windowId = Json.NullableLong(parameters, "windowId");
            int? index = Json.NullableInt(parameters, "elementIndex");
            string direction = Json.String(parameters, "direction") ?? "down";
            int pages = Json.Int(parameters, "pages", 1);

            ActionOutcome outcome;
            if (index.HasValue)
            {
                Uia.AssertSnapshotMatches(pid, windowId);
                outcome = Uia.ScrollElement(index.Value, direction, pages);
            }
            else
            {
                int x = RequiredInt(parameters, "screenX");
                int y = RequiredInt(parameters, "screenY");
                outcome = Uia.ScrollPoint(x, y, direction, pages);
            }
            return ActionPayload(outcome);
        }

        private static Dictionary<string, object> SetValueResult(Dictionary<string, object> parameters)
        {
            int pid = RequiredPid(parameters);
            long? windowId = Json.NullableLong(parameters, "windowId");
            int index = RequiredInt(parameters, "elementIndex");
            string value = Json.String(parameters, "value") ?? string.Empty;
            Uia.AssertSnapshotMatches(pid, windowId);
            Uia.SetElementValue(index, value);
            return ActionPayload(new ActionOutcome { Method = "set-value" });
        }

        private static Dictionary<string, object> TypeResult(Dictionary<string, object> parameters)
        {
            int pid = RequiredPid(parameters);
            long? windowId = Json.NullableLong(parameters, "windowId");
            string text = Json.String(parameters, "text");
            if (string.IsNullOrEmpty(text))
            {
                throw new ProtocolException(Codes.InvalidArgument, "type 需要非空 text");
            }
            string activated = Focus(pid, windowId);
            Input.TypeText(text);
            return ActionPayload(new ActionOutcome { Method = "type", Activated = activated });
        }

        private static Dictionary<string, object> KeyResult(Dictionary<string, object> parameters)
        {
            int pid = RequiredPid(parameters);
            long? windowId = Json.NullableLong(parameters, "windowId");
            string text = Json.String(parameters, "text");
            int repeat = Json.Int(parameters, "repeat", 1);
            int holdMs = Json.Int(parameters, "holdMs", 0);
            string activated = Focus(pid, windowId);
            Input.PressChord(text, repeat, holdMs);
            return ActionPayload(new ActionOutcome { Method = "key", Activated = activated });
        }

        private static Dictionary<string, object> PasteResult(Dictionary<string, object> parameters)
        {
            int pid = RequiredPid(parameters);
            long? windowId = Json.NullableLong(parameters, "windowId");
            string text = Json.String(parameters, "text") ?? string.Empty;
            string activated = Focus(pid, windowId);
            Clipboard.SetText(text);
            Thread.Sleep(60); // 让目标进程拿到剪贴板所有权后再按键
            Input.PressChord("ctrl+v", 1, 40);
            return ActionPayload(new ActionOutcome { Method = "paste", Activated = activated });
        }

        /// <summary>
        /// 键盘动作必须真的拿到前台：按键没有「位置」可以兜底，发出去就会进当前前台窗口。
        /// 切不到前台直接抛错，绝不把用户正在输入的内容打乱。
        /// </summary>
        private static string Focus(int pid, long? windowId)
        {
            IntPtr window = windowId.HasValue ? new IntPtr(windowId.Value) : IntPtr.Zero;
            IntPtr target = Input.TargetWindow(pid, window);
            string activated;
            if (!Input.EnsureInteractive(target, true, out activated))
            {
                throw new ProtocolException(Codes.ForegroundDenied,
                    "无法把 pid " + pid + " 的窗口切到前台，未发送任何按键。系统在前台锁定期间会拒绝后台进程抢焦点，"
                    + "请先对该窗口执行一次 click（它会连点击一起把窗口带到前台），或请用户切换过去");
            }
            return activated;
        }

        // ── 响应组装 ───────────────────────────────────────────

        private static Dictionary<string, object> SnapshotPayload(Snapshot snapshot)
        {
            var nodes = new List<object>();
            foreach (NodeInfo node in snapshot.Nodes)
            {
                nodes.Add(new Dictionary<string, object>
                {
                    { "index", node.Index },
                    { "parent", node.Parent },
                    { "depth", node.Depth },
                    { "kind", node.Kind },
                    { "automationId", node.AutomationId },
                    { "name", node.Name },
                    { "value", node.Value },
                    { "selected", node.Selected },
                    { "checked", node.Checked },
                    { "actions", node.Actions },
                    { "focused", node.Focused },
                    { "enabled", node.Enabled },
                    { "editable", node.Editable },
                    { "secret", node.Secret },
                    { "offscreen", node.Offscreen },
                    { "rect", node.Rect },
                    { "hiddenChildren", node.HiddenChildren },
                });
            }

            var payload = new Dictionary<string, object>
            {
                { "stateId", snapshot.StateId },
                { "app", new Dictionary<string, object>
                    {
                        { "pid", snapshot.Pid },
                        { "name", snapshot.AppName },
                        { "exe", snapshot.AppExe },
                        { "aumid", snapshot.AppAumid },
                    } },
                { "window", new Dictionary<string, object>
                    {
                        { "windowId", snapshot.WindowId },
                        { "title", snapshot.WindowTitle },
                        { "frame", snapshot.Frame },
                    } },
                { "nodes", nodes },
                { "truncated", snapshot.Truncated },
                { "total", snapshot.Total },
            };

            if (snapshot.Captured)
            {
                payload["capture"] = new Dictionary<string, object>
                {
                    { "path", snapshot.CapturePath },
                    { "width", snapshot.CaptureWidth },
                    { "height", snapshot.CaptureHeight },
                    { "frame", snapshot.Frame },
                };
            }
            return payload;
        }

        private static Dictionary<string, object> ActionPayload(ActionOutcome outcome)
        {
            return new Dictionary<string, object>
            {
                { "dispatch", "accepted" },
                { "actionSent", true },
                { "method", outcome.Method },
                { "activated", outcome.Activated },
            };
        }

        private static Dictionary<string, object> AppPayload(AppInfo app)
        {
            return new Dictionary<string, object>
            {
                { "pid", app.Pid },
                { "name", app.Name },
                { "exe", app.Exe },
                { "aumid", app.Aumid },
                { "elevated", app.Elevated },
            };
        }

        private static Dictionary<string, object> OkEnvelope(int id, Dictionary<string, object> result)
        {
            return new Dictionary<string, object> { { "id", (long)id }, { "ok", true }, { "result", result } };
        }

        private static Dictionary<string, object> ErrorEnvelope(int id, string code, string message, bool actionSent)
        {
            return new Dictionary<string, object>
            {
                { "id", (long)id },
                { "ok", false },
                { "error", new Dictionary<string, object>
                    {
                        { "code", code },
                        { "message", message },
                        { "actionSent", actionSent },
                    } },
            };
        }

        private static void WriteOk(int id, Dictionary<string, object> result)
        {
            WriteRaw(OkEnvelope(id, result));
        }

        private static void WriteError(int id, string code, string message, bool actionSent)
        {
            WriteRaw(ErrorEnvelope(id, code, message, actionSent));
        }

        private static void WriteRaw(Dictionary<string, object> payload)
        {
            if (payload == null) return;
            protocol.WriteLine(Json.Write(payload));
        }

        // ── 参数与看门狗 ───────────────────────────────────────

        private static int RequiredPid(Dictionary<string, object> parameters)
        {
            int? pid = Json.NullableInt(parameters, "pid");
            if (!pid.HasValue || pid.Value <= 0)
            {
                throw new ProtocolException(Codes.InvalidArgument, "需要 pid（先调用 apps 拿到）");
            }
            return pid.Value;
        }

        private static int RequiredInt(Dictionary<string, object> parameters, string key)
        {
            int? value = Json.NullableInt(parameters, key);
            if (!value.HasValue)
            {
                throw new ProtocolException(Codes.InvalidArgument, "缺少参数 " + key);
            }
            return value.Value;
        }

        private static int ReadParentPid(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--parent")
                {
                    int pid;
                    if (int.TryParse(args[i + 1], out pid)) return pid;
                }
            }
            return 0;
        }

        /// <summary>父进程消失就自尽：worker 卡在 UIA 里也不会留下孤儿进程占着桌面输入。</summary>
        private static void StartWatchdog(int parentPid)
        {
            var thread = new Thread(delegate()
            {
                while (true)
                {
                    Thread.Sleep(2000);
                    try
                    {
                        Process parent = Process.GetProcessById(parentPid);
                        if (parent.HasExited) Environment.Exit(0);
                    }
                    catch (Exception)
                    {
                        Environment.Exit(0);
                    }
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }
    }
}
