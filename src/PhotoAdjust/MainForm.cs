using System.Diagnostics;

namespace PhotoAdjust;

public class MainForm : Form
{
    private enum FileState { Pending, Success, Skipped }

    private sealed class FileItem
    {
        public required string Path { get; init; }
        public string Name => System.IO.Path.GetFileName(Path);
        public string OutputName => System.IO.Path.GetFileNameWithoutExtension(Path) + ".jpg";
        public bool Success { get; set; }
        // 入队时即因输出名冲突被跳过，不参与处理
        public bool PreSkipped { get; set; }
        // 是否已走过处理流程（用于区分"待处理"与"处理失败"）
        public bool Processed { get; set; }
        public string ResultText { get; set; } = "";
        public string OutputPath { get; set; } = "";

        public FileState State => PreSkipped
            ? FileState.Skipped
            : Processed ? (Success ? FileState.Success : FileState.Skipped)
                        : FileState.Pending;
    }

    private const string DefaultSummary = "目标要求: 480×640 像素 │ 20~40 KB │ 300 DPI\n支持将照片或文件夹拖拽到窗口任意位置";

    private readonly List<FileItem> items = new();
    // O(1) 去重索引，与 items 同步维护（扫描在后台线程构建批次，仅回 UI 线程后合并）
    private readonly HashSet<string> pathIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileItem> outputNameIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListView lvFiles;
    private readonly PictureBox picOriginal;
    private readonly PictureBox picResult;
    private readonly Label lblResultCaption;
    private readonly Label lblInfo;
    private readonly Label lblSummary;
    private readonly TextBox txtDir;
    private readonly Button btnBrowse;
    private readonly Button btnPick;
    private readonly Button btnAddFolder;
    private readonly Button btnClearQueue;
    private readonly Button btnProcess;
    private readonly Button btnOpenFolder;
    private readonly ToolTip fileTooltip = new();
    private readonly Stopwatch processStopwatch = new();
    private bool isProcessing;
    private bool scanning;
    private CancellationTokenSource? processCts;
    private System.Windows.Forms.Timer? progressTimer;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp" };

    public MainForm(string[]? initialFiles = null)
    {
        Text = "照片合规调整工具";
        ClientSize = new Size(990, 610);
        MinimumSize = new Size(1000, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        // ── 左侧：队列操作 ──
        btnPick = new Button
        {
            Location = new Point(12, 12),
            Size = new Size(140, 28),
            Text = "选择文件…",
        };
        btnPick.Click += async (_, _) => await PickFiles();
        Controls.Add(btnPick);

        btnAddFolder = new Button
        {
            Location = new Point(158, 12),
            Size = new Size(140, 28),
            Text = "添加文件夹…",
        };
        btnAddFolder.Click += async (_, _) => await AddFolder();
        Controls.Add(btnAddFolder);

        btnClearQueue = new Button
        {
            Location = new Point(304, 12),
            Size = new Size(140, 28),
            Text = "清空队列",
        };
        btnClearQueue.Click += (_, _) => ClearQueue();
        Controls.Add(btnClearQueue);

        // 虚拟模式：只按需生成可见行，万级文件不拖慢布局与滚动
        lvFiles = new ListView
        {
            Location = new Point(12, 48),
            Size = new Size(440, 372),
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            ShowItemToolTips = true,
            VirtualMode = true,
        };
        lvFiles.Columns.Add("文件", 170);
        lvFiles.Columns.Add("结果", 200);
        lvFiles.Columns.Add("状态", 58);
        lvFiles.RetrieveVirtualItem += OnRetrieveVirtualItem;
        lvFiles.SelectedIndexChanged += (_, _) => ShowSelectedItem();
        lvFiles.MouseMove += OnFilesMouseMove;
        lvFiles.MouseLeave += (_, _) => fileTooltip.Hide(lvFiles);
        Controls.Add(lvFiles);

        var lblOutput = new Label
        {
            Location = new Point(12, 428),
            Size = new Size(440, 18),
            Text = "输出到指定文件夹（文件名与源文件相同，同名原件会被覆盖）:",
        };
        Controls.Add(lblOutput);

        txtDir = new TextBox
        {
            Location = new Point(12, 450),
            Size = new Size(356, 24),
        };
        Controls.Add(txtDir);

        btnBrowse = new Button
        {
            Location = new Point(374, 449),
            Size = new Size(64, 26),
            Text = "浏览…",
        };
        btnBrowse.Click += (_, _) => BrowseFolder();
        Controls.Add(btnBrowse);

        lblSummary = new Label
        {
            Location = new Point(12, 484),
            Size = new Size(440, 40),
            Text = DefaultSummary,
            ForeColor = Color.DimGray,
        };
        Controls.Add(lblSummary);

        // ── 右侧：前后预览 ──
        var lblOriginalCaption = new Label
        {
            Location = new Point(470, 12),
            Size = new Size(240, 20),
            Text = "原图",
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Controls.Add(lblOriginalCaption);

        picOriginal = new PictureBox
        {
            Location = new Point(470, 36),
            Size = new Size(240, 320),
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
        };
        Controls.Add(picOriginal);

        var lblArrow = new Label
        {
            Location = new Point(710, 182),
            Size = new Size(32, 28),
            Text = "→",
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Controls.Add(lblArrow);

        lblResultCaption = new Label
        {
            Location = new Point(742, 12),
            Size = new Size(240, 20),
            Text = "结果",
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Controls.Add(lblResultCaption);

        picResult = new PictureBox
        {
            Location = new Point(742, 36),
            Size = new Size(240, 320),
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
        };
        Controls.Add(picResult);

        lblInfo = new Label
        {
            Location = new Point(470, 364),
            Size = new Size(512, 160),
            Text = "尚未选择文件",
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.TopLeft,
        };
        Controls.Add(lblInfo);

        // ── 底部操作区 ──
        var separator = new Panel
        {
            Location = new Point(0, 534),
            Size = new Size(ClientSize.Width, 1),
            BackColor = Color.FromArgb(216, 216, 216),
        };
        Controls.Add(separator);

        btnProcess = new Button
        {
            Location = new Point(12, 548),
            Size = new Size(160, 40),
            Text = "开始处理",
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
        };
        btnProcess.Click += async (_, _) =>
        {
            if (isProcessing)
            {
                processCts?.Cancel();
                return;
            }
            await ProcessAllAsync();
        };
        Controls.Add(btnProcess);

        btnOpenFolder = new Button
        {
            Location = new Point(182, 548),
            Size = new Size(180, 40),
            Text = "打开输出文件夹",
            Enabled = false,
        };
        btnOpenFolder.Click += (_, _) => OpenOutputFolder();
        Controls.Add(btnOpenFolder);

        AcceptButton = btnProcess;

        if (initialFiles is { Length: > 0 })
            _ = AddPathsAsync(initialFiles);
    }

    private async Task PickFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择照片",
            Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await AddPathsAsync(dialog.FileNames);
    }

    private async Task AddFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择文件夹（将包含其所有子文件夹中的图片）",
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await AddPathsAsync([dialog.SelectedPath]);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (scanning)
        {
            e.Effect = DragDropEffects.None;
            return;
        }
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            _ = AddPathsAsync(paths);
    }

    /// <summary>加入文件或文件夹（文件夹含子文件夹）。目录枚举与去重在后台线程完成，
    /// 期间界面保持响应并显示扫描进度，完成后一次性提交到虚拟列表。</summary>
    private async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (scanning) return;
        SetScanning(true);
        string summaryBefore = lblSummary.Text;
        lblSummary.Text = "正在扫描…";
        try
        {
            var (newItems, firstNew, error) = await Task.Run(
                () => BuildNewItems(paths, pathIndex, outputNameIndex, ReportScanProgress));
            if (newItems.Count > 0)
            {
                int baseIndex = items.Count;
                foreach (var item in newItems)
                {
                    items.Add(item);
                    pathIndex.Add(item.Path);
                    if (!item.PreSkipped)
                        outputNameIndex[item.OutputName] = item;
                }
                lvFiles.BeginUpdate();
                lvFiles.VirtualListSize = items.Count;
                lvFiles.EndUpdate();
                if (firstNew >= 0)
                {
                    // 先用索引器物化该行（触发 RetrieveVirtualItem 并登记到内部集合），
                    // 再选中，避免选中通知到达时该行还不存在
                    var first = lvFiles.Items[baseIndex + firstNew];
                    lvFiles.SelectedIndices.Clear();
                    first.Selected = true;
                    ShowSelectedItem();
                }
            }
            if (error != null)
                MessageBox.Show(this, $"扫描文件夹时出错（部分文件可能未加入）：\n{error.Message}", "错误");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"添加文件失败：{ex.Message}", "错误");
        }
        finally
        {
            SetScanning(false);
            lblSummary.Text = summaryBefore;
        }
    }

    /// <summary>后台线程执行：枚举文件、过滤扩展名、按路径与输出名去重，产出待入队批次。
    /// 全局索引只读、批次内冲突用局部索引判断，保证不触碰 UI 线程状态。</summary>
    private static (List<FileItem> NewItems, int FirstNew, Exception? Error) BuildNewItems(
        IReadOnlyList<string> paths,
        HashSet<string> pathIndex,
        Dictionary<string, FileItem> outputNameIndex,
        Action<int> onProgress)
    {
        var batchPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batchOutputs = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
        var newItems = new List<FileItem>();
        Exception? error = null;
        int firstNew = -1;

        foreach (var path in paths)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.Exists(path)
                    ? Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                    : new[] { path };
            }
            catch (Exception ex)
            {
                error ??= ex;
                continue;
            }

            int photos = 0;
            try
            {
                foreach (var file in files)
                {
                    if (!SupportedExtensions.Contains(System.IO.Path.GetExtension(file)))
                        continue;
                    photos++;
                    if (photos % 500 == 0)
                        onProgress(photos);

                    string fullPath = System.IO.Path.GetFullPath(file);
                    if (pathIndex.Contains(fullPath) || batchPaths.Contains(fullPath))
                        continue;

                    // 输出统一到一个文件夹，输出文件名相同的文件标记为跳过（显式入队并写明原因），避免处理时互相覆盖
                    string outputName = System.IO.Path.GetFileNameWithoutExtension(file) + ".jpg";
                    FileItem? conflict = null;
                    if (outputNameIndex.TryGetValue(outputName, out var globalHit))
                        conflict = globalHit;
                    else if (batchOutputs.TryGetValue(outputName, out var batchHit))
                        conflict = batchHit;

                    var item = new FileItem { Path = fullPath };
                    if (conflict != null)
                    {
                        item.PreSkipped = true;
                        item.ResultText = $"输出名与队列中 {conflict.Name} 冲突，已跳过";
                    }
                    else
                    {
                        batchOutputs[outputName] = item;
                    }
                    batchPaths.Add(fullPath);
                    if (firstNew < 0)
                        firstNew = newItems.Count;
                    newItems.Add(item);
                }
            }
            catch (Exception ex)
            {
                // 某个根目录中途出错（如无权限子目录）不影响其余路径
                error ??= ex;
            }
        }

        return (newItems, firstNew, error);
    }

    /// <summary>后台线程回调，限流刷新扫描进度（每 500 张照片一次）。</summary>
    private void ReportScanProgress(int count)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() => lblSummary.Text = $"正在扫描… 已找到 {count} 张照片");
        }
        catch
        {
            // 窗口已关闭，忽略
        }
    }

    private void SetScanning(bool value)
    {
        scanning = value;
        // 扫描期间锁定会改动队列/启动处理的入口，避免索引与列表不同步
        btnPick.Enabled = !value;
        btnAddFolder.Enabled = !value;
        btnClearQueue.Enabled = !value;
        btnProcess.Enabled = !value;
    }

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        // 必须始终为 e.Item 赋值：虚拟模式下选中/滚动通知到达时若该行尚未生成，
        // ListView 内部按显示索引取行会拿到 null 并抛 NullReferenceException
        var item = (e.ItemIndex >= 0 && e.ItemIndex < items.Count) ? items[e.ItemIndex] : null;
        var lvi = new ListViewItem(item?.Name ?? "") { Tag = item };
        lvi.SubItems.Add(item is { ResultText.Length: > 0 } ? item.ResultText : "—");
        if (item != null)
        {
            lvi.SubItems.Add(item.State switch
            {
                FileState.Success => "✔ 成功",
                FileState.Skipped => "✘ 跳过",
                _ => "待处理",
            });
            lvi.SubItems[2].ForeColor = item.State switch
            {
                FileState.Success => Color.Green,
                FileState.Skipped => Color.Red,
                _ => SystemColors.WindowText,
            };
        }
        e.Item = lvi;
    }

    private void ClearQueue()
    {
        items.Clear();
        pathIndex.Clear();
        outputNameIndex.Clear();
        lvFiles.BeginUpdate();
        lvFiles.SelectedIndices.Clear();
        lvFiles.VirtualListSize = 0;
        lvFiles.EndUpdate();
        lblResultCaption.Text = "结果";
        lblInfo.Text = "尚未选择文件";
        lblSummary.Text = DefaultSummary;
        btnOpenFolder.Enabled = false;
        ShowImage(picOriginal, () => null);
        ShowImage(picResult, () => null);
    }

    private async Task ProcessAllAsync()
    {
        if (isProcessing) return;
        if (items.Count == 0)
        {
            MessageBox.Show(this, "请先添加照片。", "提示");
            return;
        }
        var pending = items.Where(i => !i.Success && !i.PreSkipped).ToList();
        if (pending.Count == 0)
        {
            MessageBox.Show(this, "没有待处理的文件。", "提示");
            return;
        }

        if (string.IsNullOrWhiteSpace(txtDir.Text))
        {
            MessageBox.Show(this, "请先指定输出文件夹。", "提示");
            return;
        }
        string outputDir = txtDir.Text.Trim();
        try
        {
            Directory.CreateDirectory(outputDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法创建输出文件夹：{ex.Message}", "错误");
            return;
        }

        isProcessing = true;
        processCts = new CancellationTokenSource();
        int total = pending.Count;
        int done = 0, ok = 0, fail = 0;

        // 处理期间锁定队列入口，避免中途增删条目
        btnPick.Enabled = false;
        btnAddFolder.Enabled = false;
        btnClearQueue.Enabled = false;
        btnProcess.Text = "取消处理";
        processStopwatch.Restart();
        progressTimer = new System.Windows.Forms.Timer { Interval = 200 };
        progressTimer.Tick += (_, _) =>
        {
            if (!isProcessing) return;
            int d = Volatile.Read(ref done);
            double elapsed = processStopwatch.Elapsed.TotalSeconds;
            string eta = d > 0 ? $" │ 预计剩余 {FormatEta(elapsed / d * (total - d))}" : "";
            lblSummary.Text = $"正在处理 {d}/{total}（{d * 100 / total}%）│ 成功 {Volatile.Read(ref ok)} │ 失败 {Volatile.Read(ref fail)}{eta}";
            lvFiles.Invalidate();
        };
        progressTimer.Start();

        bool canceled = false;
        try
        {
            await Parallel.ForEachAsync(pending, new ParallelOptions
            {
                // 单张处理峰值内存约 60MB，并发上限 8 控制总量在 0.5GB 左右
                MaxDegreeOfParallelism = Math.Max(2, Math.Min(Environment.ProcessorCount, 8)),
                CancellationToken = processCts.Token,
            }, async (item, ct) =>
            {
                var (success, resultText, outputPath) = await Task.Run(() => ProcessOne(item.Path, outputDir, item.OutputName), ct);
                item.Success = success;
                item.Processed = true;
                item.ResultText = resultText;
                item.OutputPath = outputPath;
                if (success) Interlocked.Increment(ref ok);
                else Interlocked.Increment(ref fail);
                Interlocked.Increment(ref done);
            });
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        finally
        {
            progressTimer.Stop();
            progressTimer.Dispose();
            progressTimer = null;
            processStopwatch.Stop();
            isProcessing = false;
            btnProcess.Text = "开始处理";
            btnPick.Enabled = true;
            btnAddFolder.Enabled = true;
            btnClearQueue.Enabled = true;
            int d = Volatile.Read(ref done);
            // 汇总反映队列整体状态（含入队时即跳过的同名文件）
            lblSummary.Text = canceled
                ? $"已取消: 处理了 {d}/{total}（成功 {Volatile.Read(ref ok)}，失败 {Volatile.Read(ref fail)}），未处理项保留在队列中"
                : $"处理完成: 成功 {items.Count(i => i.Success)} 张, 跳过 {items.Count(i => !i.Success)} 张";
            btnOpenFolder.Enabled = items.Any(i => i.Success);
            lvFiles.Invalidate();
            ShowSelectedItem();
            processCts.Dispose();
            processCts = null;
        }
    }

    private static string FormatEta(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours} 小时 {ts.Minutes} 分"
            : $"{ts.Minutes} 分 {ts.Seconds} 秒";
    }

    private static (bool Success, string ResultText, string OutputPath) ProcessOne(string path, string outputDir, string outputName)
    {
        try
        {
            var result = PhotoProcessor.Process(path);
            string outputPath = System.IO.Path.Combine(outputDir, outputName);
            File.WriteAllBytes(outputPath, result.Data);
            return (true, $"480×640 │ {result.FileSize / 1024.0:F1}KB │ 300DPI", outputPath);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, "");
        }
    }

    /// <summary>结果列文字被截断时，悬停显示完整内容。</summary>
    private void OnFilesMouseMove(object? sender, MouseEventArgs e)
    {
        var hit = lvFiles.HitTest(e.Location);
        if (hit.Item != null)
        {
            int column = ColumnAtX(e.X);
            if (column >= 0)
            {
                string text = column == 0 ? hit.Item.Text! : hit.Item.SubItems[column].Text!;
                if (text.Length > 0 && TextRenderer.MeasureText(text, lvFiles.Font).Width > ColumnWidth(column) - 8)
                {
                    fileTooltip.Show(text, lvFiles, e.Location.X + 4, e.Location.Y + 14, 4000);
                    return;
                }
            }
        }
        fileTooltip.Hide(lvFiles);
    }

    private int ColumnAtX(int x)
    {
        int acc = 0;
        for (int i = 0; i < lvFiles.Columns.Count; i++)
        {
            int w = i == lvFiles.Columns.Count - 1
                ? lvFiles.ClientSize.Width - acc
                : lvFiles.Columns[i].Width;
            if (x < acc + w)
                return i;
            acc += w;
        }
        return lvFiles.Columns.Count - 1;
    }

    private int ColumnWidth(int index)
    {
        // 最后一列占满剩余宽度
        if (index == lvFiles.Columns.Count - 1)
        {
            int others = 0;
            for (int i = 0; i < index; i++)
                others += lvFiles.Columns[i].Width;
            return Math.Max(lvFiles.ClientSize.Width - others, 0);
        }
        return lvFiles.Columns[index].Width;
    }

    private void ShowSelectedItem()
    {
        // 虚拟模式下 SelectedItems 集合不可枚举，改用选中索引直接取数据
        if (lvFiles.SelectedIndices.Count == 0) return;
        int index = lvFiles.SelectedIndices[0];
        if (index < 0 || index >= items.Count) return;
        var item = items[index];

        ShowImage(picOriginal, () => LoadPreview(item.Path));
        ShowImage(picResult, item.Success
            ? () => LoadPreview(item.OutputPath)
            : () => null);
        lblResultCaption.Text = item.Success ? "结果" : "结果（未生成）";
        lblInfo.Text = item.Path + "\n" + (item.ResultText.Length > 0 ? item.ResultText : "待处理");
    }

    private static void ShowImage(PictureBox box, Func<Image?> loader)
    {
        var old = box.Image;
        box.Image = loader();
        old?.Dispose();
    }

    private static Image? LoadPreview(string path)
    {
        try
        {
            var stream = new MemoryStream(File.ReadAllBytes(path));
            return Image.FromStream(stream); // 流需在图像存活期内保持可用，随图像一起由 GC 回收
        }
        catch
        {
            return null;
        }
    }

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择输出文件夹",
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            txtDir.Text = dialog.SelectedPath;
    }

    private void OpenOutputFolder()
    {
        var lastSuccess = items.LastOrDefault(i => i.Success);
        if (lastSuccess == null) return;
        string dir = System.IO.Path.GetDirectoryName(lastSuccess.OutputPath)!;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        picOriginal.Image?.Dispose();
        picResult.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
