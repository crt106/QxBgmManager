# QxBgmManager 开发环境一键准备
# 用法: powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Write-Host "==> 仓库根目录: $root"

# 1. 便携版 .NET 10 SDK（不装系统级运行时）
$dotnetDir = Join-Path $root "vendor\dotnet"
$dotnetExe = Join-Path $dotnetDir "dotnet.exe"
if (-not (Test-Path $dotnetExe)) {
    Write-Host "==> 下载便携 .NET SDK 10.0.100 ..."
    $zip = Join-Path $env:TEMP "dotnet-sdk.zip"
    Invoke-WebRequest -Uri "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.100/dotnet-sdk-10.0.100-win-x64.zip" -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $dotnetDir -Force
    Remove-Item $zip
}
& $dotnetExe --version | Out-Null
Write-Host "==> dotnet OK"

# 2. Python 编码依赖（numpy 装到 vendor/python-deps，不污染系统 Python）
$deps = Join-Path $root "vendor\python-deps"
if (-not (Test-Path (Join-Path $deps "numpy"))) {
    Write-Host "==> 安装 numpy 到 vendor\python-deps ..."
    python -m pip install --target="$deps" numpy
}
python -c "import sys; sys.path.insert(0, r'$deps'); import numpy; print('numpy', numpy.__version__)"

# 3. ffmpeg 检查
$ff = Get-Command ffmpeg -ErrorAction SilentlyContinue
if ($ff) { Write-Host "==> ffmpeg OK: $($ff.Source)" }
else { Write-Host "!! 未找到 ffmpeg：请在首次启动 App 后的环境检查页/设置页配置 ffmpeg 路径" }

Write-Host ""
Write-Host "完成。构建: vendor\dotnet\dotnet.exe build app\QxBgmManager.sln"
