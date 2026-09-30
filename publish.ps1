param(
    # 自包含模式：目标机器无需安装 .NET 运行时，但 exe 较大（约 70MB）
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\PhotoAdjust\PhotoAdjust.csproj'
$outDir = Join-Path $PSScriptRoot 'dist'

# 每次发布前清空输出目录，避免残留上一轮的产物
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

$extraArgs = @('-r', 'win-x64')
if ($SelfContained) {
    # 自包含：把 .NET 桌面运行时打进 exe，任何机器可直接运行
    $extraArgs += @('--self-contained', 'true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true')
}
else {
    # 依赖框架：目标机器需装有 .NET 8 桌面运行时，exe 极小
    $extraArgs += @('--self-contained', 'false')
}

Write-Host ('publish args: ' + ($extraArgs -join ' '))
& dotnet publish $project -c Release @extraArgs -p:PublishSingleFile=true -p:DebugType=none -o $outDir
if ($LASTEXITCODE -ne 0) { throw "发布失败（dotnet publish 退出码 $LASTEXITCODE）" }

Write-Host ''
Get-ChildItem $outDir | ForEach-Object { '{0}  {1:N0} bytes' -f $_.Name, $_.Length }
