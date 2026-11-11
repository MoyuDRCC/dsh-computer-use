// 窗口截图。
//
// 首选 PrintWindow(PW_RENDERFULLCONTENT)：能拿到被其它窗口遮挡的内容；
// 失败或全黑时回退 BitBlt（System.Drawing 的 CopyFromScreen），要求窗口可见。
// 输出按 EXTENDED_FRAME_BOUNDS 裁剪，保证图片像素与坐标基准是同一个矩形。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace DshComputerUse
{
    internal static class Capture
    {
        /// <summary>把窗口截成 PNG 写到 path，返回图片尺寸与所用矩形。</summary>
        public static BitmapRect CaptureWindow(IntPtr hWnd, string path)
        {
            if (Native.IsIconic(hWnd))
            {
                throw new ProtocolException(Codes.ScreenshotUnavailable, "窗口已最小化，截不到像素；元素树仍然可用，请改用元素索引");
            }

            RECT frame = Native.FrameRect(hWnd);
            RECT windowRect = Native.WindowRect(hWnd);
            if (frame.Width <= 0 || frame.Height <= 0)
            {
                throw new ProtocolException(Codes.ScreenshotUnavailable, "窗口没有可用的可见矩形");
            }

            Bitmap bitmap = null;
            try
            {
                bitmap = CaptureWithPrintWindow(hWnd, windowRect);
                if (bitmap == null || IsBlank(bitmap))
                {
                    if (bitmap != null) bitmap.Dispose();
                    bitmap = CaptureWithScreen(frame);
                }
                if (bitmap == null)
                {
                    throw new ProtocolException(Codes.ScreenshotUnavailable, "PrintWindow 与屏幕截图都失败；请改用元素索引");
                }

                Bitmap result = Crop(bitmap, frame, windowRect);
                if (IsBlank(result))
                {
                    result.Dispose();
                    throw new ProtocolException(Codes.ScreenshotBlank,
                        "截到的图像是纯色（该窗口可能由 GPU 直接合成）；请改用元素索引");
                }

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                result.Save(path, ImageFormat.Png);
                var box = new BitmapRect
                {
                    Width = result.Width,
                    Height = result.Height,
                    Left = frame.Left,
                    Top = frame.Top,
                };
                result.Dispose();
                return box;
            }
            finally
            {
                if (bitmap != null) bitmap.Dispose();
            }
        }

        /// <summary>
        /// PrintWindow 画到「屏幕兼容 DC」而不是 GDI+ 位图的 HDC。
        ///
        /// 两类窗口要分开处理：
        /// - DPI 感知窗口（Electron/Chrome/资源管理器）按物理像素绘制，一比一；
        /// - DPI 不感知的老窗口，PrintWindow 只按逻辑像素（96 DPI）绘制，在 200% 缩放下
        ///   内容只占左上角 1/4，其余是黑的。这类窗口按系统缩放倍数放大回物理尺寸，
        ///   与用户肉眼看到的一致，坐标基准也才自洽。
        /// </summary>
        private static Bitmap CaptureWithPrintWindow(IntPtr hWnd, RECT windowRect)
        {
            if (windowRect.Width <= 0 || windowRect.Height <= 0) return null;

            double scale = DpiScaleFor(hWnd);
            int captureWidth = Math.Max(1, (int)Math.Round(windowRect.Width / scale));
            int captureHeight = Math.Max(1, (int)Math.Round(windowRect.Height / scale));

            Bitmap raw = PrintWindowToBitmap(hWnd, captureWidth, captureHeight);
            if (raw == null) return null;

            if (scale <= 1.01) return raw;

            // 放大回物理尺寸：不放大模型看不清小字，坐标基准也会与实际窗口对不上
            var scaled = new Bitmap(windowRect.Width, windowRect.Height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(raw, new Rectangle(0, 0, scaled.Width, scaled.Height));
            }
            raw.Dispose();
            return scaled;
        }

        /// <summary>DPI 不感知的窗口按系统缩放倍数返回；其余一律 1。</summary>
        private static double DpiScaleFor(IntPtr hWnd)
        {
            try
            {
                IntPtr context = Native.GetWindowDpiAwarenessContext(hWnd);
                // 0 = DPI_AWARENESS_UNAWARE
                if (Native.GetAwarenessFromDpiAwarenessContext(context) != 0) return 1.0;

                IntPtr screen = Native.GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) return 1.0;
                int systemDpi;
                try { systemDpi = Native.GetDeviceCaps(screen, Native.LOGPIXELSX); }
                finally { Native.ReleaseDC(IntPtr.Zero, screen); }

                if (systemDpi <= 96) return 1.0;
                return systemDpi / 96.0;
            }
            catch (Exception)
            {
                return 1.0;
            }
        }

        private static Bitmap PrintWindowToBitmap(IntPtr hWnd, int width, int height)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;

            IntPtr memoryDc = Native.CreateCompatibleDC(screenDc);
            IntPtr dib = Native.CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || dib == IntPtr.Zero)
            {
                if (memoryDc != IntPtr.Zero) Native.DeleteDC(memoryDc);
                if (dib != IntPtr.Zero) Native.DeleteObject(dib);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
                return null;
            }

            IntPtr previous = Native.SelectObject(memoryDc, dib);
            bool printed;
            try
            {
                printed = Native.PrintWindow(hWnd, memoryDc, Native.PW_RENDERFULLCONTENT);
            }
            finally
            {
                Native.SelectObject(memoryDc, previous);
            }

            Bitmap bitmap = null;
            if (printed)
            {
                using (Bitmap fromDib = Image.FromHbitmap(dib))
                {
                    bitmap = fromDib.Clone(new Rectangle(0, 0, fromDib.Width, fromDib.Height), PixelFormat.Format24bppRgb);
                }
            }

            Native.DeleteObject(dib);
            Native.DeleteDC(memoryDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
            return bitmap;
        }

        private static Bitmap CaptureWithScreen(RECT frame)
        {
            try
            {
                var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(frame.Left, frame.Top, 0, 0, new Size(frame.Width, frame.Height));
                }
                return bitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>PrintWindow 画的是整个窗口矩形；裁掉阴影边距，得到与视觉矩形一致的图。</summary>
        private static Bitmap Crop(Bitmap source, RECT frame, RECT windowRect)
        {
            int offsetX = frame.Left - windowRect.Left;
            int offsetY = frame.Top - windowRect.Top;
            bool sameSize = source.Width == frame.Width && source.Height == frame.Height && offsetX == 0 && offsetY == 0;
            if (sameSize) return source;

            var rect = new Rectangle(offsetX, offsetY, frame.Width, frame.Height);
            rect.Intersect(new Rectangle(0, 0, source.Width, source.Height));
            if (rect.Width <= 0 || rect.Height <= 0) return source;

            Bitmap cropped = source.Clone(rect, source.PixelFormat);
            source.Dispose();
            return cropped;
        }

        /// <summary>采样判断是否纯色（全黑/全白的截图对模型没有信息量）。</summary>
        private static bool IsBlank(Bitmap bitmap)
        {
            try
            {
                int stepX = Math.Max(1, bitmap.Width / 24);
                int stepY = Math.Max(1, bitmap.Height / 24);
                int reference = bitmap.GetPixel(0, 0).ToArgb();
                for (int y = 0; y < bitmap.Height; y += stepY)
                {
                    for (int x = 0; x < bitmap.Width; x += stepX)
                    {
                        if (bitmap.GetPixel(x, y).ToArgb() != reference) return false;
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    internal sealed class BitmapRect
    {
        public int Width;
        public int Height;
        public int Left;
        public int Top;
    }
}
