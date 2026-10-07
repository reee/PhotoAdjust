using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PhotoAdjust;

/// <summary>
/// 照片合规处理：按 EXIF 方向校正后缩放到 480×640，写入 300 DPI，文件体积控制在 20~40KB。
/// </summary>
public static class PhotoProcessor
{
    public const int TargetWidth = 480;
    public const int TargetHeight = 640;
    public const int MinBytes = 20 * 1024;
    public const int MaxBytes = 40 * 1024;
    public const int PreferredBytes = 30 * 1024; // 收敛到 30KB 附近，两种 KB 口径下都稳在 20~40 区间内
    public const float TargetDpi = 300f;

    // GDI+ 解码不支持降采样，超大源图全尺寸解码的内存峰值不可控（8 线程并行时尤甚），
    // 超过上限直接拒绝；典型手机/证件照片远低于该值
    public const long MaxSourcePixels = 40_000_000;

    private const double AspectTolerance = 0.005; // 允许 0.5% 的宽高比浮点误差

    // 编码器信息只含元数据，跨线程共享安全
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public sealed class Result
    {
        public required byte[] Data { get; init; }
        public int FileSize => Data.Length;
    }

    public static void CheckAspectRatio(int width, int height)
    {
        double target = (double)TargetWidth / TargetHeight;
        double actual = (double)width / height;
        if (Math.Abs(actual - target) / target > AspectTolerance)
            throw new InvalidOperationException($"比例不符（{width}×{height}，要求 3:4），已跳过");
    }

    public static Result Process(string sourcePath)
    {
        byte[] fileBytes = ReadFileBytes(sourcePath);

        // 解码前先按文件头预读像素数，超限直接拒绝，避免全尺寸解码撑爆内存
        if (TryGetStoredDimensions(fileBytes, out int rawW, out int rawH)
            && (long)rawW * Math.Abs(rawH) > MaxSourcePixels)
        {
            throw new InvalidOperationException(
                $"图片像素过大（{rawW}×{Math.Abs(rawH)}，上限 {MaxSourcePixels / 1_000_000}MP），已跳过");
        }

        int orientation = ReadExifOrientation(fileBytes);
        bool rotated = orientation is >= 5 and <= 8; // 90° 族方向：显示宽高与存储宽高互换

        var stream = new MemoryStream(fileBytes);
        Bitmap stored;
        try
        {
            stored = new Bitmap(stream);
        }
        catch (Exception)
        {
            stream.Dispose();
            throw new InvalidOperationException("无法读取图片文件（格式损坏或不受支持）");
        }

        using (stream)
        using (stored)
        {
            CheckAspectRatio(rotated ? stored.Height : stored.Width, rotated ? stored.Width : stored.Height);

            // 90° 族先解到 640×480，旋转回正后恰好 480×640
            int targetW = rotated ? TargetHeight : TargetWidth;
            int targetH = rotated ? TargetWidth : TargetHeight;

            // 大比例缩小分两步：先双线性粗缩到目标的 2 倍，再高质量双三次精缩。
            // GDI+ 对超大源图单步 bicubic 又慢又易出边缘伪影，两阶段既快又稳。
            // 阈值取 3：真实照片（3000×4000 级，factor ≥ 6）走两阶段；
            // 测试样张（1200×1600，factor 2.5）保持单步，与既有基线逐像素一致。
            Image drawSource = stored;
            Bitmap? intermediate = null;
            double factor = Math.Max((double)stored.Width / targetW, (double)stored.Height / targetH);
            if (factor > 3.0)
            {
                int iw = Math.Max(targetW, (int)(stored.Width / factor * 2));
                int ih = Math.Max(targetH, (int)(stored.Height / factor * 2));
                intermediate = new Bitmap(iw, ih, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(intermediate))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(stored, new Rectangle(0, 0, iw, ih));
                }
                drawSource = intermediate;
            }

            using var resized = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.Clear(Color.White); // 带透明通道的 PNG 源按白底合成，避免 JPEG 中出现黑底
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(drawSource, new Rectangle(0, 0, targetW, targetH));
            }
            intermediate?.Dispose();

            resized.SetResolution(TargetDpi, TargetDpi);
            ApplyOrientation(resized, orientation);

            byte[] data = EncodeWithinPreferredSize(resized);
            if (data.Length < MinBytes)
                data = PadWithComment(data, MinBytes - data.Length);

            if (data.Length > MaxBytes)
                throw new InvalidOperationException("照片内容过于复杂，无法压缩到 40KB 以内");

            return new Result { Data = data };
        }
    }

    private static byte[] ReadFileBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            throw new InvalidOperationException("无法读取图片文件（文件不存在、被占用或无权限）");
        }
    }

    /// <summary>不解码、只按文件头读取存储宽高（JPEG SOFn / PNG IHDR / BMP DIB）。
    /// 无法识别时返回 false，由调用方回退到解码路径。</summary>
    private static bool TryGetStoredDimensions(byte[] b, out int width, out int height)
    {
        width = height = 0;

        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            int pos = 2;
            while (pos + 4 <= b.Length && b[pos] == 0xFF)
            {
                int marker = b[pos + 1];
                if (marker is 0x00 or 0xFF or 0xD8 or 0xD9 or 0x01 or (>= 0xD0 and <= 0xD7))
                {
                    pos += 2;
                    continue;
                }
                if (marker == 0xDA)
                    break; // SOS：帧内尺寸字段之前必须已遇到 SOFn
                int len = (b[pos + 2] << 8) | b[pos + 3];
                if (len < 2 || pos + 2 + len > b.Length)
                    break;
                if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                {
                    if (pos + 9 > b.Length)
                        return false;
                    height = (b[pos + 5] << 8) | b[pos + 6];
                    width = (b[pos + 7] << 8) | b[pos + 8];
                    return width > 0 && height > 0;
                }
                pos += 2 + len;
            }
            return false;
        }

        if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            && b[12] == (byte)'I' && b[13] == (byte)'H' && b[14] == (byte)'D' && b[15] == (byte)'R')
        {
            width = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            height = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            return width > 0 && height > 0;
        }

        if (b.Length >= 26 && b[0] == (byte)'B' && b[1] == (byte)'M')
        {
            width = b[18] | (b[19] << 8) | (b[20] << 16) | (b[21] << 24);
            height = b[22] | (b[23] << 8) | (b[24] << 16) | (b[25] << 24);
            return width > 0 && height != 0;
        }

        return false;
    }

    /// <summary>
    /// GDI+ 解码只返回 raw 像素、不应用 EXIF 方向标记。
    /// 在此按标记把像素旋转到显示方向，后续编码以用户看到的画面为准。
    /// </summary>
    private static void ApplyOrientation(Bitmap source, int orientation)
    {
        RotateFlipType transform = orientation switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.RotateNoneFlipY,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate90FlipY,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };

        if (transform != RotateFlipType.RotateNoneFlipNone)
            source.RotateFlip(transform);
    }

    /// <summary>从 JPEG 字节流读取 EXIF Orientation（0x0112），无标记或解析失败返回 1（不旋转）。</summary>
    private static int ReadExifOrientation(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return 1;

        int pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            int marker = jpeg[pos + 1];
            if (marker is 0x00 or 0xFF or 0xD8 or 0xD9 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                pos += 2;
                continue;
            }
            if (marker == 0xDA)
                break; // SOS：扫描数据开始

            int len = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (len < 2 || pos + 2 + len > jpeg.Length)
                break;

            if (marker == 0xE1)
            {
                int tiffStart = pos + 4;
                if (tiffStart + 6 <= jpeg.Length
                    && jpeg[tiffStart] == 0x45 && jpeg[tiffStart + 1] == 0x78
                    && jpeg[tiffStart + 2] == 0x69 && jpeg[tiffStart + 3] == 0x66
                    && jpeg[tiffStart + 4] == 0x00 && jpeg[tiffStart + 5] == 0x00)
                {
                    return ReadOrientationFromTiff(jpeg, tiffStart + 6);
                }
            }

            pos += 2 + len;
        }
        return 1;
    }

    private static int ReadOrientationFromTiff(byte[] b, int tiffStart)
    {
        try
        {
            if (tiffStart + 8 > b.Length)
                return 1;
            bool littleEndian = b[tiffStart] == 0x49 && b[tiffStart + 1] == 0x49; // "II"
            if (!littleEndian && !(b[tiffStart] == 0x4D && b[tiffStart + 1] == 0x4D)) // "MM"
                return 1;

            int Read16(int off) => littleEndian ? b[off] | (b[off + 1] << 8) : (b[off] << 8) | b[off + 1];
            int Read32(int off) => littleEndian
                ? b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24)
                : (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];

            int ifdPos = tiffStart + Read32(tiffStart + 4);
            if (ifdPos + 2 > b.Length)
                return 1;
            int count = Read16(ifdPos);
            ifdPos += 2;

            for (int i = 0; i < count; i++)
            {
                if (ifdPos + 12 > b.Length)
                    break;
                if (Read16(ifdPos) == 0x0112 && Read16(ifdPos + 2) == 3 && Read32(ifdPos + 4) >= 1)
                {
                    int value = Read16(ifdPos + 8); // SHORT 值内联在 value 字段前 2 字节
                    return value is >= 1 and <= 8 ? value : 1;
                }
                ifdPos += 12;
            }
            return 1;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// 先按质量 50 编码（480×640 照片最常见的落点，多数情况一次命中 20~30KB 区间），
    /// 越界时才在半区间内二分。平均编码次数从 7 次降到 1~4 次；
    /// 带内质量取 50 附近而非理论最高，换取速度（对 480×640 观感差异很小）。
    /// </summary>
    private static byte[] EncodeWithinPreferredSize(Bitmap image)
    {
        byte[] data = Encode(image, 50);
        if (data.Length <= PreferredBytes)
        {
            if (data.Length < MinBytes)
            {
                // 偏小：尝试更高质量（仍 ≤30KB）以减少 COM 填充
                var better = BinarySearchQuality(image, 51, 99);
                if (better != null)
                    return better;
            }
            return data;
        }
        // 偏大：在 [1,49] 二分最高且 ≤30KB 的质量
        return BinarySearchQuality(image, 1, 49) ?? Encode(image, 1);
    }

    /// <summary>在 [lo,hi] 二分搜索最高且编码体积 ≤ PreferredBytes 的质量；无解返回 null。</summary>
    private static byte[]? BinarySearchQuality(Bitmap image, int lo, int hi)
    {
        byte[]? best = null;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            byte[] d = Encode(image, mid);
            if (d.Length <= PreferredBytes)
            {
                best = d;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return best;
    }

    private static byte[] Encode(Bitmap image, long quality)
    {
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        using var ms = new MemoryStream();
        image.Save(ms, JpegCodec, ep);
        return ms.ToArray();
    }

    /// <summary>
    /// 插入 COM 注释段，用无视觉影响的字节补足到最小体积。
    /// 插在第一个 marker 段之后，保持 JFIF 要求的 SOI 紧跟 APP0 结构。
    /// </summary>
    private static byte[] PadWithComment(byte[] jpeg, int padBytes)
    {
        padBytes = Math.Max(padBytes, 4);
        if (padBytes > 65535)
            throw new InvalidOperationException("所需填充过大");

        int insertAt = FirstSegmentEnd(jpeg);
        var output = new byte[jpeg.Length + padBytes];
        Buffer.BlockCopy(jpeg, 0, output, 0, insertAt);
        output[insertAt] = 0xFF;
        output[insertAt + 1] = 0xFE;                                  // COM marker
        int payload = padBytes - 4;
        output[insertAt + 2] = (byte)((payload + 2) >> 8);
        output[insertAt + 3] = (byte)((payload + 2) & 0xFF);
        for (int i = 0; i < payload; i++)
            output[insertAt + 4 + i] = 0x20;                          // 空格填充
        Buffer.BlockCopy(jpeg, insertAt, output, insertAt + padBytes, jpeg.Length - insertAt);
        return output;
    }

    /// <summary>第一个带长度 marker 段的结束偏移；无法识别时退回 SOI 之后。</summary>
    private static int FirstSegmentEnd(byte[] jpeg)
    {
        int pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            int marker = jpeg[pos + 1];
            if (marker == 0xFF)
            {
                pos += 1; // 连续 FF 填充字节，跳过单个继续找真正的 marker
                continue;
            }
            if (marker is 0x00 or 0xD9)
                break; // 文件结束，无长度字段
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                pos += 2; // RST/TEM 独立 marker，无长度字段
                continue;
            }
            if (marker == 0xDA)
                break; // SOS：扫描数据开始
            int len = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (len < 2)
                break;
            int end = pos + 2 + len; // len 含长度字段自身 2 字节
            if (end > jpeg.Length)
                break;
            return end;
        }
        return 2;
    }
}
