# 从 C# 源码中提取"含中文的字符串字面量"，按文件分组去重，供本地化字典使用。
# 跳过普通注释与 XML 注释中的文本，只取真正的字符串字面量。
param(
    [string]$SrcDir = 'E:\deepseek\AE插件开发\src',
    [string]$OutFile = 'E:\deepseek\AE插件开发\tools\zh-literals.txt'
)

$files = Get-ChildItem (Join-Path $SrcDir '*.cs') | Sort-Object Name
$all = New-Object System.Collections.Generic.HashSet[string]
$report = New-Object System.Collections.Generic.List[string]
$total = 0

foreach ($f in $files) {
    $text = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
    # 去掉块注释与行注释，避免把注释里的中文当成字符串
    $text = [regex]::Replace($text, '/\*.*?\*/', '', 'Singleline')
    $text = [regex]::Replace($text, '(?m)^\s*///.*$', '')
    $text = [regex]::Replace($text, '(?m)//.*$', '')

    $set = New-Object System.Collections.Generic.HashSet[string]
    foreach ($m in [regex]::Matches($text, '"((?:[^"\\]|\\.)*)"')) {
        $v = $m.Groups[1].Value
        if ($v -notmatch '[\u4e00-\u9fff]') { continue }
        if ($set.Add($v)) { $null = $all.Add($v) }
    }
    if ($set.Count -eq 0) { continue }
    $total += $set.Count
    $report.Add("### $($f.Name)  ($($set.Count))")
    foreach ($v in ($set | Sort-Object)) { $report.Add($v) }
    $report.Add('')
}

$report.Add("### 合计（含重复跨文件）: $total ；唯一: $($all.Count)")
[IO.File]::WriteAllLines($OutFile, $report, (New-Object Text.UTF8Encoding($false)))
Write-Output "唯一字面量: $($all.Count)"
Write-Output "已写出: $OutFile"
