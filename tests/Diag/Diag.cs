using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

static class Diag
{
    static ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    static void T(string name, Action a)
    {
        try { a(); Console.WriteLine($"  {name,-44} OK"); }
        catch (Exception ex) { Console.WriteLine($"  {name,-44} 抛出: {ex.Message}"); }
    }

    static Bitmap Fill32(int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, w, h);
        var d = b.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var row = new byte[w * 4];
        for (int x = 0; x < w; x++)
        {
            row[x * 4] = (byte)(x % 251); row[x * 4 + 1] = (byte)(x / 7 % 253);
            row[x * 4 + 2] = (byte)(x * 3 % 256); row[x * 4 + 3] = 255;
        }
        int stride = Math.Abs(d.Stride);
        for (int y = 0; y < h; y++) Marshal.Copy(row, 0, d.Scan0 + y * stride, w * 4);
        b.UnlockBits(d);
        return b;
    }

    static void SaveStream(Bitmap b)
    {
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, 50);
        using var os = new MemoryStream();
        b.Save(os, Jpeg, ep);
        Console.WriteLine($"      ({os.Length}B)");
    }

    /// <summary>从程序所在目录逐级向上定位 tests\sample.jpg，不依赖绝对路径。</summary>
    static string FindSample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "sample.jpg");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent!;
        }
        throw new FileNotFoundException("未找到 tests\\sample.jpg");
    }

    static void Main()
    {
        T("T8 LockBits 32bpp + Save(stream)", () => { var b = Fill32(480, 640); SaveStream(b); b.Dispose(); });
        T("T9 LockBits 32bpp + SetRes + Save", () => { var b = Fill32(480, 640); b.SetResolution(300f, 300f); SaveStream(b); b.Dispose(); });
        T("T10 LockBits 32bpp + RotateFlip270 + SetRes + Save", () =>
        {
            var b = Fill32(640, 480);
            b.RotateFlip(RotateFlipType.Rotate270FlipNone);
            if (b.Width != 480 || b.Height != 640) throw new Exception($"尺寸未变 {b.Width}x{b.Height}");
            b.SetResolution(300f, 300f);
            SaveStream(b);
            b.Dispose();
        });
        T("T11 文件解码 24bpp + Save(stream)", () =>
        {
            using var f = new Bitmap(FindSample());
            SaveStream(f);
        });
        T("T12 LockBits 24bpp + Save", () =>
        {
            var b = new Bitmap(480, 640, PixelFormat.Format24bppRgb);
            var rect = new Rectangle(0, 0, 480, 640);
            var d = b.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            int stride = Math.Abs(d.Stride);
            for (int y = 0; y < 640; y++)
                for (int x = 0; x < 480 * 3; x++)
                    Marshal.WriteByte(d.Scan0 + y * stride + x, (byte)(x % 251));
            b.UnlockBits(d);
            SaveStream(b);
            b.Dispose();
        });
        T("T13 DrawImage 24bpp 目标 + Save", () =>
        {
            var src = Fill32(480, 640);
            var t = new Bitmap(480, 640, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(t)) g.DrawImage(src, new Rectangle(0, 0, 480, 640));
            SaveStream(t);
            t.Dispose(); src.Dispose();
        });
    }
}
