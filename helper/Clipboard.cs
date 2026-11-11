// 剪贴板写入：paste 动作只用得到「写」，读不需要，所以不实现读。

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DshComputerUse
{
    internal static class Clipboard
    {
        /// <summary>把文本放进剪贴板；别的进程正占着剪贴板时重试几次。</summary>
        public static void SetText(string text)
        {
            if (text == null) text = string.Empty;

            bool opened = false;
            for (int attempt = 0; attempt < 8 && !opened; attempt++)
            {
                opened = Native.OpenClipboard(IntPtr.Zero);
                if (!opened) Thread.Sleep(30);
            }
            if (!opened)
            {
                throw new ProtocolException(Codes.Internal, "剪贴板被其它程序占用，写入失败");
            }

            IntPtr memory = IntPtr.Zero;
            try
            {
                Native.EmptyClipboard();
                int bytes = (text.Length + 1) * 2;
                memory = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)bytes);
                if (memory == IntPtr.Zero)
                {
                    throw new ProtocolException(Codes.Internal, "剪贴板内存分配失败");
                }
                IntPtr target = Native.GlobalLock(memory);
                if (target == IntPtr.Zero)
                {
                    throw new ProtocolException(Codes.Internal, "剪贴板内存锁定失败");
                }
                try
                {
                    char[] chars = text.ToCharArray();
                    Marshal.Copy(chars, 0, target, chars.Length);
                    Marshal.WriteInt16(target, chars.Length * 2, 0); // 结尾 NUL
                }
                finally
                {
                    Native.GlobalUnlock(memory);
                }

                if (Native.SetClipboardData(Native.CF_UNICODETEXT, memory) == IntPtr.Zero)
                {
                    throw new ProtocolException(Codes.Internal, "剪贴板写入被拒绝");
                }
                // 交付所有权后不能再释放
                memory = IntPtr.Zero;
            }
            finally
            {
                if (memory != IntPtr.Zero) Native.GlobalFree(memory);
                Native.CloseClipboard();
            }
        }
    }
}
