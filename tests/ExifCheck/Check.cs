using System.Drawing;
using System.Reflection;
using PhotoAdjust;

namespace ExifCheck;

/// <summary>
/// PhotoProcessor 手写解析层的单元测试：EXIF Orientation 解析、COM 填充结构、
/// 文件头尺寸预读、比例校验。不依赖真实照片，全部用构造字节流 + tests/ 合成样张。
/// </summary>
static class Program
{
    static readonly Type T = typeof(PhotoProcessor);
    static int failures;

    static object? Call(string name, params object?[] args) =>
        T.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args.Cast<object?>().ToArray());

    static void Check(bool cond, string name)
    {
        Console.WriteLine($"  {name,-56} {(cond ? "OK" : "!! FAIL")}");
        if (!cond) failures++;
    }

    /// <summary>从程序所在目录逐级向上定位 tests 目录，不依赖绝对路径。</summary>
    static string FindTestsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "tests");
            if (File.Exists(Path.Combine(candidate, "sample.jpg")))
                return candidate;
            dir = dir.Parent!;
        }
        throw new FileNotFoundException("未找到包含 sample.jpg 的 tests 目录");
    }

    // ── 构造带 EXIF Orientation 的最小 JPEG ──
    static byte[] JpegWithExifOrientation(int ori, bool bigEndian = false, bool xmpFirst = false,
        bool overlongApp1 = false, int? ifdOffsetOverride = null)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0xFF, 0xD8 });

        if (xmpFirst)
        {
            var xmp = System.Text.Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta/>");
            int xlen = xmp.Length + 2;
            ms.Write(new byte[] { 0xFF, 0xE1, (byte)(xlen >> 8), (byte)(xlen & 0xFF) });
            ms.Write(xmp);
        }

        // TIFF: 头(8) + IFD 计数(2) + 1 条目(12) + 下一 IFD(4)
        byte[] tiff = bigEndian
            ? new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
                           0x00, 0x01, 0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, (byte)ori, 0x00, 0x00, 0x00,
                           0x00, 0x00, 0x00, 0x00 }
            : new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
                           0x01, 0x00, 0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)ori, 0x00, 0x00, 0x00,
                           0x00, 0x00, 0x00, 0x00 };
        if (ifdOffsetOverride.HasValue)
        {
            int off = ifdOffsetOverride.Value;
            tiff[4] = (byte)off;
            tiff[5] = (byte)(off >> 8);
            tiff[6] = (byte)(off >> 16);
            tiff[7] = (byte)(off >> 24);
        }

        var exifHeader = new byte[] { 0x45, 0x78, 0x69, 0x66, 0x00, 0x00 }; // "Exif\0\0"
        int segLen = 2 + exifHeader.Length + tiff.Length;
        if (overlongApp1)
            segLen += 100; // 声明长度大于实际内容 → 解析器必须安全退出
        ms.Write(new byte[] { 0xFF, 0xE1, (byte)(segLen >> 8), (byte)(segLen & 0xFF) });
        ms.Write(exifHeader);
        ms.Write(tiff);
        ms.Write(new byte[] { 0xFF, 0xD9 });
        return ms.ToArray();
    }

    static int ReadOrientation(byte[] jpeg) => (int)Call("ReadExifOrientation", jpeg)!;

    // ── 构造带 SOFn 的最小 JPEG（仅头，无扫描数据）──
    static byte[] CraftedSofJpeg(int w, int h) => new byte[]
    {
        0xFF, 0xD8,
        0xFF, 0xC0, 0x00, 0x11, 0x08,
        (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w,
        0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        0xFF, 0xD9,
    };

    static (bool ok, int w, int h) StoredDims(byte[] b)
    {
        object?[] args = { b, 0, 0 };
        bool ok = (bool)T.GetMethod("TryGetStoredDimensions", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args)!;
        return (ok, (int)args[1]!, (int)args[2]!);
    }

    static void Main()
    {
        Console.WriteLine("== EXIF Orientation 解析 ==");
        for (int ori = 1; ori <= 8; ori++)
            Check(ReadOrientation(JpegWithExifOrientation(ori)) == ori, $"orientation {ori} 读回");
        Check(ReadOrientation(JpegWithExifOrientation(6, bigEndian: true)) == 6, "大端(MM) orientation 6");
        Check(ReadOrientation(JpegWithExifOrientation(7, xmpFirst: true)) == 7, "XMP APP1 在前仍读到 Exif APP1");
        Check(ReadOrientation(JpegWithExifOrientation(6, overlongApp1: true)) == 1, "APP1 声明长度越界 → 1");
        Check(ReadOrientation(JpegWithExifOrientation(6, ifdOffsetOverride: unchecked((int)0x7FFFFFFF))) == 1, "IFD 偏移越界 → 1");
        Check(ReadOrientation(JpegWithExifOrientation(6, ifdOffsetOverride: unchecked((int)0xFFFFFFFF))) == 1, "IFD 偏移为负 → 1");
        Check(ReadOrientation(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) == 1, "非 JPEG → 1");
        Check(ReadOrientation(Array.Empty<byte>()) == 1, "空文件 → 1");

        Console.WriteLine("== COM 填充结构 ==");
        // SOI + APP0(len 16) + EOI：第一个带长度段结束于偏移 20
        var minimal = new byte[]
        {
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10,
            (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x02, 0x00, 0x01, 0x00, 0x01, 0x00, 0x18, 0x01,
            0xFF, 0xD9,
        };
        Check((int)Call("FirstSegmentEnd", minimal)! == 20, "FirstSegmentEnd 定位 APP0 末尾");
        var padded = (byte[])Call("PadWithComment", minimal, 100)!;
        Check(padded.Length == minimal.Length + 100, "填充后总长 +100");
        Check(padded[20] == 0xFF && padded[21] == 0xFE, "COM 段插在 APP0 之后");
        Check(((padded[22] << 8) | padded[23]) == 100 - 4 + 2, "COM 长度字段正确（含自身 2 字节）");
        Check(padded[^2] == 0xFF && padded[^1] == 0xD9, "EOI 仍在末尾");
        Check(ReadOrientation(padded) == 1, "填充后 EXIF 扫描正确跳过 COM");
        // FF 填充字节：FFD8 FF FF E0 … 应跳过单个填充 FF 后继续定位真正的段
        var ffFill = new byte[22];
        ffFill[0] = 0xFF; ffFill[1] = 0xD8; ffFill[2] = 0xFF; ffFill[3] = 0xFF;
        ffFill[4] = 0xE0; ffFill[5] = 0x00; ffFill[6] = 0x10;
        ffFill[^2] = 0xFF; ffFill[^1] = 0xD9;
        Check((int)Call("FirstSegmentEnd", ffFill)! == 3 + 2 + 16, "FirstSegmentEnd 跳过 FF 填充字节");

        Console.WriteLine("== 文件头尺寸预读 ==");
        var (ok1, w1, h1) = StoredDims(CraftedSofJpeg(9000, 6000));
        Check(ok1 && w1 == 9000 && h1 == 6000, "JPEG SOF0 9000×6000");
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D }
            .CopyTo(png, 0);
        new byte[] { (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 100, 0, 0, 0, 200 }
            .CopyTo(png, 12);
        var (ok2, w2, h2) = StoredDims(png);
        Check(ok2 && w2 == 100 && h2 == 200, "PNG IHDR 100×200");
        var bmp = new byte[30];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        bmp[18] = 44; bmp[19] = 0; bmp[20] = 0; bmp[21] = 0;   // 44
        bmp[22] = 0x88; bmp[23] = 0xFF; bmp[24] = 0xFF; bmp[25] = 0xFF; // -120（top-down）
        var (ok3, w3, h3) = StoredDims(bmp);
        Check(ok3 && w3 == 44 && h3 == -120, "BMP 44×-120（top-down 负高）");
        Check(!StoredDims(new byte[] { 1, 2, 3, 4 }).ok, "无法识别 → false");

        Console.WriteLine("== 管线行为（合成样张）==");
        string tests = FindTestsDir();
        var result = PhotoProcessor.Process(Path.Combine(tests, "sample.jpg"));
        Check(result.Data.Length is >= 20 * 1024 and <= 40 * 1024, "sample.jpg 输出体积 20~40KB");
        using (var bmp2 = new Bitmap(new MemoryStream(result.Data)))
            Check(bmp2.Width == 480 && bmp2.Height == 640, "sample.jpg 输出 480×640");

        var paddedReal = (byte[])Call("PadWithComment", result.Data, 500)!;
        using (var bmp3 = new Bitmap(new MemoryStream(paddedReal)))
            Check(bmp3.Width == 480 && bmp3.Height == 640 && paddedReal.Length == result.Data.Length + 500,
                "真实 JPEG 填充 500B 后仍可解码");

        // 两阶段缩放路径：源图远大于目标（factor > 2）
        var big = PhotoProcessor.Process(Path.Combine(tests, "big3to4.jpg"));
        using (var bmp4 = new Bitmap(new MemoryStream(big.Data)))
            Check(bmp4.Width == 480 && bmp4.Height == 640, "big3to4.jpg 两阶段缩放后 480×640");

        // 像素护栏：构造 9000×6000 头，Process 必须在解码前拒绝
        string huge = Path.Combine(Path.GetTempPath(), "pa_huge_" + Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(huge, CraftedSofJpeg(9000, 6000));
        try
        {
            PhotoProcessor.Process(huge);
            Check(false, "54MP 源图被拒绝");
        }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains("像素过大"), "54MP 源图被拒绝: " + ex.Message);
        }
        finally
        {
            File.Delete(huge);
        }

        Console.WriteLine("== 比例校验 ==");
        try { PhotoProcessor.CheckAspectRatio(480, 640); Check(true, "480×640 通过"); }
        catch { Check(false, "480×640 通过"); }
        try { PhotoProcessor.CheckAspectRatio(3000, 4000); Check(true, "3000×4000 通过"); }
        catch { Check(false, "3000×4000 通过"); }
        try { PhotoProcessor.CheckAspectRatio(480, 638); Check(true, "480×638（0.4% 偏差）通过"); }
        catch { Check(false, "480×638（0.4% 偏差）通过"); }
        try { PhotoProcessor.CheckAspectRatio(640, 480); Check(false, "640×480 被拒"); }
        catch { Check(true, "640×480 被拒"); }

        Console.WriteLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL（{failures} 项）");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
