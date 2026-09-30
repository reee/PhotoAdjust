# PhotoAdjust — 照片合规调整工具

批量把照片处理成统一规格的 Windows 桌面小工具：按 EXIF 方向校正 → 缩放到 **480×640** → 写入 **300 DPI** → JPEG 体积控制在 **20~40 KB**（向 30 KB 收敛）。

## 功能

- **批量处理**：选择多个文件或整个文件夹（含所有子目录），后台线程扫描与去重，万级文件不卡界面。
- **自动去重**：按路径去重防止重复入队；输出名冲突（如 `a.png` 与 `a.jpg` 都输出 `a.jpg`）的文件显式标记跳过并写明原因，避免互相覆盖。
- **EXIF 方向校正**：独立解析 JPEG EXIF Orientation，横拍/竖拍照片均按用户看到的画面转正，输出统一为 480×640。
- **体积控制**：先按质量 50 编码，越界时二分搜索最高可用质量；仍偏小时插入 COM 注释段（无视觉影响）补足到 20 KB 以上。
- **并行处理**：多线程并行（上限 8），实时进度、预计剩余时间，可随时取消。
- **前后对比预览**：列表选中任一文件即可对照查看原图与结果。
- **虚拟列表**：队列使用虚拟模式 ListView，只渲染可见行。

## 运行要求

- Windows 10 及以上
- 依赖框架发布需安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)；自包含发布则无需安装

## 使用

1. 启动后通过「选择文件…」或「添加文件夹…」加入照片；
2. 指定输出文件夹（输出文件名 = 源文件名 + `.jpg`，同名文件会被覆盖）；
3. 点击「开始处理」。若输出目录中包含本轮待处理的源文件，会先弹出覆盖确认。

> 注意：比例不符 3:4 或损坏的文件会被跳过并在列表中标明原因。

### 命令行模式（用于自动化）

```powershell
PhotoAdjust.exe --process <输出.jpg> <输入.jpg>
# 成功输出: OK <字节数> bytes -> <输出路径>，退出码 0
```

## 构建与发布

```powershell
# 依赖框架（exe 极小，目标机器需 .NET 8 桌面运行时）
./publish.ps1

# 自包含（约 70MB，任何机器可直接运行）
./publish.ps1 -SelfContained
```

产物输出到 `dist/`。

## 测试

`tests/` 下为 Windows 专用测试辅助工程（依赖 GDI+/WinForms，需在 Windows 上运行）：

| 工程 | 用途 |
| --- | --- |
| `PerfCheck` | 阶段 A：与 `tests/baseline` 基线输出逐像素回归比对；阶段 B：2000 张并行处理速度与输出校验；阶段 C：取消响应与 UI 流畅度（最大消息间隔 < 1s） |
| `Diag` | GDI+ 编码互操作的诊断脚本 |

```powershell
dotnet run --project tests/PerfCheck          # 完整回归 + 速度 + 取消
dotnet run --project tests/PerfCheck -- --skipA   # 跳过阶段 A
```

`tests/` 根目录的 `*.jpg` 为各场景测试样张（EXIF 方向、比例不符、损坏、纯色等）。

## 代码结构

```
src/PhotoAdjust/
├── Program.cs          # 入口：--process 命令行模式 / GUI
├── PhotoProcessor.cs   # 核心管线：EXIF 解析 → 缩放 → 质量搜索编码 → 填充
└── MainForm.cs         # 界面：队列、扫描去重、并行处理、预览
```
