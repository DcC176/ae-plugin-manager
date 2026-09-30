# 从已包裹的源码里收集"需要翻译的中文文案"，输出成待翻译清单（去掉 L.T 包裹，保留原转义）。
param(
    [string]$SrcDir = 'E:\deepseek\AE插件开发\src',
    [string]$OutFile = 'E:\deepseek\AE插件开发\tools\to-translate.txt',
    [string[]]$Files = @('MainForm.cs','Ui.cs','Conflicts.cs','Installer.cs','Executor.cs','Scanner.cs','Archive.cs','Program.cs','Model.cs')
)

$pattern = 'L\.T\("((?:[^"\\]|\\.)*)"\)'
$all = New-Object System.Collections.Generic.List[string]
$seen = New-Object System.Collections.Generic.HashSet[string]

foreach ($name in $Files) {
    $path = Join-Path $SrcDir $name
    if (-not (Test-Path $path)) { continue }
    $text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($text, $pattern)) {
        $lit = $m.Groups[1].Value
        if ($seen.Add($lit)) { $list.Add($lit) }
    }
    if ($list.Count -gt 0) {
        $all.Add("### $name ($($list.Count))")
        foreach ($v in ($list | Sort-Object)) { $all.Add($v) }
        $all.Add('')
    }
}
$all.Add("### 合计唯一: $($seen.Count)")
[IO.File]::WriteAllLines($OutFile, $all, (New-Object Text.UTF8Encoding($false)))
Write-Output "唯一文案: $($seen.Count)  已写出: $OutFile"
