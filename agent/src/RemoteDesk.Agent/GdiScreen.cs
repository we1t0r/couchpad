using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent;

/// <summary>
/// Снимок экрана через GDI (BitBlt) с курсором. Просто и работает везде; игры в исключительном
/// полноэкранном режиме могут показываться чёрными — их возьмёт захват через видеокарту позже.
/// </summary>
sealed class GdiScreen : IScreenSource
{
    public IReadOnlyList<MonitorInfo> Monitors() =>
        Screen.AllScreens
            .OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.X)
            .Select((s, i) => new MonitorInfo(s.DeviceName, $"Монитор {i + 1}{(s.Primary ? " (основной)" : "")}",
                s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, s.Primary))
            .ToList();

    public IScreenCapture Open(string? monitorId)
    {
        var monitors = Monitors();
        return new GdiCapture(monitors.FirstOrDefault(m => m.Id == monitorId) ?? monitors.First(m => m.Primary));
    }

    sealed class GdiCapture(MonitorInfo monitor) : IScreenCapture
    {
        static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        readonly Bitmap full = new(monitor.Width, monitor.Height, PixelFormat.Format32bppRgb);
        byte[] current = new byte[monitor.Width * monitor.Height * 4];
        byte[] previous = new byte[monitor.Width * monitor.Height * 4];
        bool hasPrevious;

        public MonitorInfo Monitor => monitor;

        public ScreenFrame? Capture(int maxWidth, int quality, bool force)
        {
            using (var g = Graphics.FromImage(full))
            {
                // На экране блокировки и в окне UAC Windows не даёт снимать — тут будет исключение, поток подождёт.
                g.CopyFromScreen(monitor.X, monitor.Y, 0, 0, full.Size, CopyPixelOperation.SourceCopy);
                DrawCursor(g);
            }

            // Ничего не поменялось — кадр не шлём: экономим сеть и батарею телефона.
            var bits = full.LockBits(new Rectangle(Point.Empty, full.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                Marshal.Copy(bits.Scan0, current, 0, current.Length);
            }
            finally
            {
                full.UnlockBits(bits);
            }
            var same = hasPrevious && current.AsSpan().SequenceEqual(previous);
            (current, previous) = (previous, current);
            hasPrevious = true;
            if (same && !force)
                return null;

            var scale = Math.Min(1.0, (double)maxWidth / monitor.Width);
            var width = Math.Max(1, (int)(monitor.Width * scale));
            var height = Math.Max(1, (int)(monitor.Height * scale));
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            using var output = new MemoryStream();
            if (scale >= 1)
            {
                full.Save(output, Jpeg, parameters);
            }
            else
            {
                using var small = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    g.DrawImage(full, 0, 0, width, height);
                }
                small.Save(output, Jpeg, parameters);
            }
            return new ScreenFrame(output.ToArray(), width, height);
        }

        void DrawCursor(Graphics g)
        {
            var info = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref info) || (info.flags & CursorShowing) == 0 || info.hCursor == 0)
                return;
            if (!GetIconInfo(info.hCursor, out var icon))
                return;
            try
            {
                var hdc = g.GetHdc();
                try
                {
                    DrawIconEx(hdc, info.ptScreenPos.X - icon.xHotspot - monitor.X, info.ptScreenPos.Y - icon.yHotspot - monitor.Y,
                        info.hCursor, 0, 0, 0, 0, DiNormal);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
            finally
            {
                if (icon.hbmMask != 0) DeleteObject(icon.hbmMask);
                if (icon.hbmColor != 0) DeleteObject(icon.hbmColor);
            }
        }

        public void Dispose() => full.Dispose();
    }

    const int CursorShowing = 1, DiNormal = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct CursorInfo
    {
        public int cbSize, flags;
        public nint hCursor;
        public Point ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IconInfo
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public nint hbmMask, hbmColor;
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    static extern bool GetIconInfo(nint icon, out IconInfo info);

    [DllImport("user32.dll")]
    static extern bool DrawIconEx(nint hdc, int x, int y, nint icon, int w, int h, int step, nint brush, int flags);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(nint obj);
}
