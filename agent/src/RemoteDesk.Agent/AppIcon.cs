using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RemoteDesk.Agent;

/// <summary>Значок, нарисованный кодом: синий скруглённый квадрат с курсором. Couchpad.ico сделан из него же (--export-icon).</summary>
static class AppIcon
{
    public static readonly Color Accent = Color.FromArgb(55, 138, 221);

    public static Icon Create(int size = 32)
    {
        using var bmp = Draw(size);
        var handle = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    /// <summary>Файл .ico с картинками PNG нужных размеров — для значка программы, ярлыков и установщика.</summary>
    public static void WriteIco(string path)
    {
        int[] sizes = [16, 24, 32, 48, 64, 256];
        var images = sizes.Select(s =>
        {
            using var bmp = Draw(s);
            using var png = new MemoryStream();
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            return png.ToArray();
        }).ToList();
        using var file = new BinaryWriter(File.Create(path));
        file.Write((short)0);
        file.Write((short)1);
        file.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            file.Write((byte)(sizes[i] % 256)); // 256 записывается как 0
            file.Write((byte)(sizes[i] % 256));
            file.Write((byte)0);
            file.Write((byte)0);
            file.Write((short)1);
            file.Write((short)32);
            file.Write(images[i].Length);
            file.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images)
            file.Write(image);
    }

    static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var r = size * 0.22f;
            using var path = RoundedRect(new RectangleF(1, 1, size - 2, size - 2), r);
            using var fill = new SolidBrush(Accent);
            g.FillPath(fill, path);

            // Стрелка-курсор.
            var s = size / 32f;
            PointF[] arrow =
            [
                new(10 * s, 7 * s), new(10 * s, 24 * s), new(14.5f * s, 19.5f * s),
                new(18 * s, 26 * s), new(21 * s, 24.5f * s), new(17.5f * s, 18 * s), new(23.5f * s, 18 * s),
            ];
            g.FillPolygon(Brushes.White, arrow);
        }
        return bmp;
    }

    static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
