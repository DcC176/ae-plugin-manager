# 编译 AE 插件管理器：使用系统自带 csc.exe（.NET Framework 4.x），产物单文件、零依赖。
# 用法: powershell -ExecutionPolicy Bypass -File build.ps1 [-Lang zh|en]
# 说明：界面语言是运行期特性（菜单「界面语言」可切换），因此中英文共用同一个 exe；
#       -Lang 只影响产物文件名，方便发布时按受众取名。
param(
    [ValidateSet('en','zh')]
    [string]$Lang = 'en'
)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$out  = Join-Path $root 'dist'
$name = if ($Lang -eq 'zh') { 'AE插件管理器.exe' } else { 'AEPluginManager.exe' }
$exe  = Join-Path $out $name

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 csc.exe: $csc" }

New-Item -ItemType Directory -Force -Path $out | Out-Null

$refs = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.IO.Compression.dll',
    'System.IO.Compression.FileSystem.dll'
) | ForEach-Object { "/r:$_" }

$files = Get-ChildItem (Join-Path $src '*.cs') | Sort-Object Name | Select-Object -ExpandProperty FullName
if (-not $files) { throw "src 目录下没有 .cs 文件" }

$icon = Join-Path $root 'app.ico'
if (Test-Path $icon) { $refs += "/win32icon:$icon" }

$argumentList = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warn:4', '/codepage:65001', "/out:$exe") + $refs + $files
& $csc @argumentList

if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

$size = [math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host "编译成功: $exe ($size KB)" -ForegroundColor Green
