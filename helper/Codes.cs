// 错误码的唯一定义处。Node 侧不做映射表（两侧各写一份必然漂移），只透传这里的字符串，
// 另外维护一个「不可盲目重试」的集合。

namespace DshComputerUse
{
    internal static class Codes
    {
        public const string AppNotFound = "APP_NOT_FOUND";
        public const string AmbiguousApp = "AMBIGUOUS_APP";
        public const string WindowNotFound = "WINDOW_NOT_FOUND";
        public const string ElementUnavailable = "ELEMENT_UNAVAILABLE";
        public const string StaleState = "STALE_STATE";
        public const string NotSettable = "NOT_SETTABLE";
        public const string Disabled = "DISABLED";
        public const string NoRaster = "NO_RASTER";
        public const string ForegroundDenied = "FOREGROUND_DENIED";
        public const string ElevatedTarget = "ELEVATED_TARGET";
        public const string ScreenshotUnavailable = "SCREENSHOT_UNAVAILABLE";
        public const string ScreenshotBlank = "SCREENSHOT_BLANK";
        public const string InvalidArgument = "INVALID_ARGUMENT";
        public const string Timeout = "TIMEOUT";
        public const string HelperBusy = "HELPER_BUSY";
        public const string Internal = "INTERNAL";
    }

    /// <summary>
    /// 协议层异常。Code 会被原样送到 Node，再由 Node 原样交给模型。
    /// ActionSent 只对动作类请求有意义：它决定模型能不能安全重试。
    /// </summary>
    internal sealed class ProtocolException : System.Exception
    {
        public string Code { get; private set; }
        public bool ActionSent { get; private set; }

        public ProtocolException(string code, string message, bool actionSent)
            : base(message)
        {
            Code = code;
            ActionSent = actionSent;
        }

        public ProtocolException(string code, string message)
            : this(code, message, false)
        {
        }
    }
}
