using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using PhotoAdjust;

namespace PerfCheck;

static class Program
{
    const int SpeedFileCount = 2000;
    static readonly string TestsDir = @"D:\Projects\PhotoAdjust\tests";
    static readonly BindingFlags NF = BindingFlags.NonPublic | BindingFlags.Instance;

    enum Phase { Enqueue, Process, WaitB, Clear, ReEnqueue, Cancel, WaitC, Done }

    [STAThread]
    static int Main(string[] args)
    {
        bool pass = true;
        bool skipA = args.Contains("--skipA");
        if (!skipA)
            pass &= RunRegressionTest();
        pass &= RunSpeedAndCancelTest();
        Console.WriteLine(pass ? "RESULT: PASS" : "RESULT: FAIL");
        return pass ? 0 : 1;
    }

    // ── 阶段 A：输出回归（与回滚前原版产品的基线输出逐像素比对）──
    static bool RunRegressionTest()
    {
        Console.WriteLine("== 阶段 A：输出回归（对照原版基线输出）==");
        bool pass = true;

        foreach (var src in Directory.EnumerateFiles(TestsDir, "*.jpg").OrderBy(x => x))
        {
            string name = Path.GetFileName(src);
            try
            {
                if (name == "garbage.jpg")
                {
                    try
                    {
                        PhotoProcessor.Process(src);
                        Console.WriteLine($"  {name,-22} !! 预期失败却成功");
                        pass = false;
                    }
                    catch (Exception ex)
                    {
                        bool ok = ex.Message == "无法读取图片文件（格式损坏或不受支持）";
                        if (!ok) pass = false;
                        Console.WriteLine($"  {name,-22} 正确拒绝: {ex.Message}  {(ok ? "OK" : "!! 消息不符")}");
                    }
                    continue;
                }

                if (name == "corrupt.jpg")
                {
                    // 截断 JPEG：旧版可部分解码、新版拒绝属于可接受的行为变化，仅提示
                    try
                    {
                        var r = PhotoProcessor.Process(src);
                        Console.WriteLine($"  {name,-22} 成功处理（{r.Data.Length}B，行为变化，可接受）");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  {name,-22} 拒绝截断 JPEG（行为变化，可接受）: {ex.Message}");
                    }
                    continue;
                }

                if (name == "wrongratio.jpg")
                {
                    try
                    {
                        PhotoProcessor.Process(src);
                        Console.WriteLine($"  {name,-22} !! 预期失败却成功");
                        pass = false;
                    }
                    catch (Exception ex)
                    {
                        bool ok = ex.Message.StartsWith("比例不符");
                        if (!ok) pass = false;
                        Console.WriteLine($"  {name,-22} 正确拒绝: {ex.Message}  {(ok ? "OK" : "!! 消息不符")}");
                    }
                    continue;
                }

                // 与原版基线（回滚前产品输出）逐像素比对：回滚后行为应与原版一致
                string basePath = Path.Combine(TestsDir, "baseline", name);
                if (!File.Exists(basePath))
                {
                    Console.WriteLine($"  {name,-22} !! 基线缺失，无法比对");
                    pass = false;
                    continue;
                }

                var result = PhotoProcessor.Process(src);
                using var nw = new Bitmap(new MemoryStream(result.Data));
                using var old = new Bitmap(basePath);
                double mae = PixelMae(old, nw);
                bool dimsOk = nw.Width == 480 && nw.Height == 640;
                bool dpiOk = Math.Abs(nw.HorizontalResolution - 300) < 1 && Math.Abs(nw.VerticalResolution - 300) < 1;
                bool sizeOk = result.Data.Length >= 20 * 1024 && result.Data.Length <= 40 * 1024;
                bool verdict = dimsOk && dpiOk && sizeOk && mae < 3;
                if (!verdict) pass = false;
                Console.WriteLine($"  {name,-22} {result.Data.Length,6}B  {nw.Width}x{nw.Height}  dpi={nw.HorizontalResolution:F0}  mae={mae,5:F2}  {(verdict ? "OK" : "!! FAIL")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name,-22} !! 新管线失败: {ex.Message}");
                pass = false;
            }
        }
        return pass;
    }

    /// <summary>GDI+ 独立真值：全解码 → 按 GDI+ EXIF 方向旋转 → 双三次缩放到 480×640。无法解码或比例不符返回 null。</summary>
    static Bitmap? GdiGroundTruth(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var img = Image.FromStream(new MemoryStream(bytes));
            int ori = 1;
            if (img.PropertyIdList.Contains(0x0112))
            {
                var p = img.GetPropertyItem(0x0112);
                if (p.Value.Length > 0 && p.Value[0] is >= 1 and <= 8)
                    ori = p.Value[0];
            }

            using var stored = new Bitmap(img);
            bool rot = ori is >= 5 and <= 8;
            int dispW = rot ? img.Height : img.Width;
            int dispH = rot ? img.Width : img.Height;
            double target = (double)PhotoProcessor.TargetWidth / PhotoProcessor.TargetHeight;
            if (Math.Abs((double)dispW / dispH - target) / target > 0.005)
                return null;

            int tw = rot ? 640 : 480, th = rot ? 480 : 640;
            var resized = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(stored, new Rectangle(0, 0, tw, th));
            }
            var tf = ori switch
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
            if (tf != RotateFlipType.RotateNoneFlipNone)
                resized.RotateFlip(tf);
            return resized;
        }
        catch
        {
            return null;
        }
    }

    // ── 阶段 B：2000 张并行处理速度 + 进度展示；阶段 C：取消 ──
    static bool RunSpeedAndCancelTest()
    {
        Console.WriteLine($"== 阶段 B：{SpeedFileCount} 张并行处理 ==");
        string imgDir = Path.Combine(Path.GetTempPath(), "pa_speed_" + Guid.NewGuid().ToString("N"));
        string outDir = Path.Combine(Path.GetTempPath(), "pa_speedout_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(imgDir);
        try
        {
            var sw = Stopwatch.StartNew();
            string variantDir = Path.Combine(Path.GetTempPath(), "pa_variants");
            Directory.CreateDirectory(variantDir);
            for (int v = 0; v < 20; v++)
                GenerateVariant(Path.Combine(variantDir, $"v{v}.jpg"), v);
            for (int i = 0; i < SpeedFileCount; i++)
                File.Copy(Path.Combine(variantDir, $"v{i % 20}.jpg"), Path.Combine(imgDir, $"img{i:D4}.jpg"), false);
            Console.WriteLine($"准备 {SpeedFileCount} 张 1200x1600 测试图: {sw.ElapsedMilliseconds:N0} ms");
            bool pass = true;
            Phase phase = Phase.Enqueue;
            bool progressSeen = false;
            long maxGap = 0;
            DateTime? lastTick = null;
            DateTime phaseStart = DateTime.Now;
            int cancelProcessed = -1;

            var form = new MainForm(new[] { imgDir });
            var timer = new System.Windows.Forms.Timer { Interval = 100 };
            timer.Tick += (_, _) =>
            {
                try
                {
                    var now = DateTime.Now;
                    if (lastTick != null)
                    {
                        long gap = (long)(now - lastTick.Value).TotalMilliseconds;
                        if (gap > maxGap)
                            maxGap = gap;
                        if (gap > 1500)
                            Console.WriteLine($"[gap] {gap} ms @ 阶段{phase} +{(now - phaseStart).TotalSeconds:F1}s");
                    }
                    lastTick = now;

                    var txtDir = (TextBox)GetField(form, "txtDir")!;
                    var btn = (Button)GetField(form, "btnProcess")!;
                    var summary = ((Label)GetField(form, "lblSummary")!).Text;

                    switch (phase)
                    {
                        case Phase.Enqueue when GetVirtualSize(form) == SpeedFileCount:
                            phaseStart = now;
                            txtDir.Text = outDir;
                            btn.PerformClick();
                            phase = Phase.Process;
                            break;

                        case Phase.Process:
                            if (summary.StartsWith("正在处理")) progressSeen = true;
                            if (summary.StartsWith("处理完成") || summary.StartsWith("已取消"))
                            {
                                Console.WriteLine($"[B] {summary}");
                                Console.WriteLine($"[B] {SpeedFileCount} 张处理耗时: {(now - phaseStart).TotalSeconds:F1} s，UI 最大间隔 {maxGap} ms，进度展示: {progressSeen}");
                                maxGap = 0; // 重置，阶段 C 单独统计
                                var vsw = Stopwatch.StartNew();
                                pass &= VerifyOutputs(outDir, form);
                                Console.WriteLine($"[B] VerifyOutputs 耗时: {vsw.ElapsedMilliseconds} ms（不计入 UI 间隔）");
                                lastTick = DateTime.Now; // 校验为 harness 自身耗时，不算 UI 卡顿
                                ((Button)GetField(form, "btnClearQueue")!).PerformClick();
                                phase = Phase.Clear;
                            }
                            else if ((now - phaseStart) > TimeSpan.FromMinutes(5))
                            {
                                Console.WriteLine("!! 阶段 B 超时（5 分钟）");
                                pass = false;
                                phase = Phase.Done;
                            }
                            break;

                        case Phase.Clear when GetVirtualSize(form) == 0:
                            var m = form.GetType().GetMethod("AddPathsAsync", NF)!;
                            _ = (Task)m.Invoke(form, new object?[] { (IReadOnlyList<string>)(object)new[] { imgDir } })!;
                            phase = Phase.ReEnqueue;
                            break;

                        case Phase.ReEnqueue when GetVirtualSize(form) == SpeedFileCount:
                            txtDir.Text = outDir;
                            btn.PerformClick();
                            phaseStart = now;
                            phase = Phase.Cancel;
                            break;

                        case Phase.Cancel:
                            // 等处理实际跑起来（有进度文本）2.5 秒后点击取消
                            if (progressSeen2(summary) && (now - phaseStart) > TimeSpan.FromSeconds(2.5))
                            {
                                btn.PerformClick(); // 第二次点击 = 取消
                                phase = Phase.WaitC;
                            }
                            else if ((now - phaseStart) > TimeSpan.FromSeconds(60))
                            {
                                Console.WriteLine("!! 阶段 C 启动超时");
                                pass = false;
                                phase = Phase.Done;
                            }
                            break;

                        case Phase.WaitC:
                            if (summary.StartsWith("已取消"))
                            {
                                Console.WriteLine($"[C] {summary}");
                                int p = ExtractBetween(summary, "处理了 ", "/");
                                cancelProcessed = p;
                                Console.WriteLine($"[C] 取消时已完成 {p}/{SpeedFileCount}，UI 最大间隔 {maxGap} ms");
                                pass &= p is > 0 and < SpeedFileCount && maxGap < 1000;
                                phase = Phase.Done;
                            }
                            else if ((now - phaseStart) > TimeSpan.FromSeconds(30))
                            {
                                Console.WriteLine("!! 阶段 C 取消未生效");
                                pass = false;
                                phase = Phase.Done;
                            }
                            break;

                        case Phase.Done:
                            timer.Stop();
                            form.Close();
                            break;

                        default:
                            if ((now - phaseStart) > TimeSpan.FromMinutes(5))
                            {
                                Console.WriteLine($"!! 阶段 {phase} 超时");
                                pass = false;
                                phase = Phase.Done;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("!! tick 内部异常:\n" + ex);
                    pass = false;
                    timer.Stop();
                    form.Close();
                }
            };
            bool progressSeen2(string s) => s.StartsWith("正在处理");

            try
            {
                timer.Start();
                Application.Run(form);
            }
            catch (Exception ex)
            {
                Console.WriteLine("!! Application.Run 抛出异常:\n" + ex);
                timer.Stop();
                pass = false;
            }
            form.Dispose();
            return pass;
        }
        finally
        {
            try { Directory.Delete(imgDir, true); } catch { }
            try
            {
                var v = Path.Combine(Path.GetTempPath(), "pa_variants");
                if (Directory.Exists(v)) Directory.Delete(v, true);
            }
            catch { }
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); } catch { }
        }
    }

    static bool VerifyOutputs(string outDir, Form form)
    {
        int files = Directory.EnumerateFiles(outDir).Count();
        int ok = 0;
        foreach (var it in (System.Collections.IList)GetField(form, "items")!)
            if ((bool)it.GetType().GetProperty("Success")!.GetValue(it)!) ok++;

        // 抽查第一张输出的尺寸/DPI/体积
        string info = "无输出";
        var first = Directory.EnumerateFiles(outDir).FirstOrDefault();
        if (first != null)
        {
            using var bmp = new Bitmap(first);
            info = $"{Path.GetFileName(first)}: {bmp.Width}x{bmp.Height} dpi={bmp.HorizontalResolution:F0} {new FileInfo(first).Length}B";
        }
        Console.WriteLine($"[B] 输出 {files} 个文件，成功项 {ok}，抽查 {info}");

        bool sizeOk = false;
        if (first != null)
            sizeOk = new FileInfo(first).Length is >= 20 * 1024 and <= 40 * 1024;
        bool pass = files == SpeedFileCount && ok == SpeedFileCount && sizeOk;
        if (!pass) Console.WriteLine("!! 阶段 B 输出校验不符");
        return pass;
    }

    // 生成 3:4（1200x1600）平滑内容测试图：底色 + 随机圆/矩形，JPEG 可正常压缩
    static void GenerateVariant(string path, int seed)
    {
        if (File.Exists(path))
            return; // 本机 GDI+ 编码互操作偶发故障，已生成的直接复用
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                GenerateVariantCore(path, seed);
                return;
            }
            catch (Exception) when (attempt < 5)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(300);
            }
        }
    }

    static void GenerateVariantCore(string path, int seed)
    {
        var rnd = new Random(seed);
        using var bmp = new Bitmap(1200, 1600, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)));
        for (int i = 0; i < 40; i++)
        {
            using var br = new SolidBrush(Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)));
            int x = rnd.Next(0, 1000), y = rnd.Next(0, 1400);
            int w = rnd.Next(50, 400), h = rnd.Next(50, 400);
            if (rnd.Next(2) == 0) g.FillEllipse(br, x, y, w, h);
            else g.FillRectangle(br, x, y, w, h);
        }
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, 85L); // 必须 long 重载；int 重载在本机 .NET 8 下抛 "Parameter is not valid"
        // 本机 Save(path, codec, ep) 文件路径重载不稳定，统一走流
        using var ms = new MemoryStream();
        bmp.Save(ms, codec, ep);
        File.WriteAllBytes(path, ms.ToArray());
    }

    static double PixelMae(Bitmap a, Bitmap b)
    {
        var ra = new Rectangle(0, 0, a.Width, a.Height);
        var da = a.LockBits(ra, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var db = b.LockBits(ra, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pa = new byte[Math.Abs(da.Stride) * a.Height];
            var pb = new byte[Math.Abs(db.Stride) * b.Height];
            Marshal.Copy(da.Scan0, pa, 0, pa.Length);
            Marshal.Copy(db.Scan0, pb, 0, pb.Length);
            long diff = 0;
            int n = a.Width * a.Height;
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                diff += Math.Abs(pa[o] - pb[o]) + Math.Abs(pa[o + 1] - pb[o + 1]) + Math.Abs(pa[o + 2] - pb[o + 2]);
            }
            return diff / (double)(n * 3);
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
        }
    }

    static int ExtractBetween(string s, string start, string end)
    {
        int i1 = s.IndexOf(start, StringComparison.Ordinal);
        int i2 = s.IndexOf(end, i1 + start.Length, StringComparison.Ordinal);
        if (i1 < 0 || i2 < 0) return -1;
        return int.Parse(s[(i1 + start.Length)..i2].Trim());
    }

    static int GetVirtualSize(Form f) => ((ListView)GetField(f, "lvFiles")!).VirtualListSize;

    static object? GetField(Form f, string name) => f.GetType().GetField(name, NF)!.GetValue(f);
}
