# 逐行比对当前源码与备份（把当前源码里的 L.T 包裹去掉后应完全相同）。
# 输出写到文件，避免控制台编码问题。
param(
    [string]$SrcDir = 'E:\deepseek\AE插件开发\src',
    [string]$BakDir = 'E:\deepseek\AE插件开发\.src-backup',
    [string]$OutFile = 'E:\deepseek\AE插件开发\tools\verify-report.txt'
)

$report = New-Object System.Collections.Generic.List[string]
$files = Get-ChildItem (Join-Path $SrcDir '*.cs') | Sort-Object Name
$totalDiff = 0

foreach ($f in $files) {
    $srcPath = $f.FullName
    $bakPath = Join-Path $BakDir $f.Name
    if (-not (Test-Path $bakPath)) { $report.Add("$($f.Name): 备份不存在"); continue }

    $cur = [IO.File]::ReadAllText($srcPath, [Text.Encoding]::UTF8)
    $bak = [IO.File]::ReadAllText($bakPath, [Text.Encoding]::UTF8)
    $unwrapped = [regex]::Replace($cur, 'L\.T\(("(?:[^"\\]|\\.)*")\)', '${1}')
    if ($unwrapped -eq $bak) { $report.Add("$($f.Name): 一致（仅新增 L.T 包裹）"); continue }

    $curLines = [IO.File]::ReadAllLines($srcPath, [Text.Encoding]::UTF8)
    $bakLines = [IO.File]::ReadAllLines($bakPath, [Text.Encoding]::UTF8)
    $report.Add("$($f.Name): 行数 当前 $($curLines.Count) / 备份 $($bakLines.Count)")
    $diffs = 0
    for ($i = 0; $i -lt [Math]::Max($curLines.Count, $bakLines.Count); $i++) {
        $c = if ($i -lt $curLines.Count) { [regex]::Replace($curLines[$i], 'L\.T\(("(?:[^"\\]|\\.)*")\)', '${1}') } else { '<missing>' }
        $b = if ($i -lt $bakLines.Count) { $bakLines[$i] } else { '<missing>' }
        if ($c -ne $b) {
            $diffs++
            if ($diffs -le 12) {
                $report.Add("  行 $($i + 1)")
                $report.Add("    备份: $b")
                $report.Add("    当前: $c")
            }
        }
    }
    $report.Add("  差异行数: $diffs")
    $totalDiff += $diffs
}
$report.Add("")
$report.Add("总差异行数: $totalDiff")
[IO.File]::WriteAllLines($OutFile, $report, (New-Object Text.UTF8Encoding($false)))
Write-Output "报告已写出: $OutFile  总差异 $totalDiff 行"
