# 包裹脚本 v2：把源码里"含中文的字符串字面量"包成 L.T("...")，供英文模式查表翻译。
# v1 的缺陷：正则把逐字字符串 @"...\" 结尾的反斜杠当成转义引号，导致跨行误匹配、注释里也被改写。
# v2 改为字符级扫描（跳过注释、正确识别 @"..." 与 "" 转义），并强制逐行校验：
#   去包裹后必须与原文件逐字节一致，否则该文件不落盘。
param(
    [string]$SrcDir = 'E:\deepseek\AE插件开发\src',
    [string[]]$Files = @('MainForm.cs','Ui.cs','Conflicts.cs','Installer.cs','Executor.cs','Scanner.cs','Archive.cs','Program.cs','Model.cs'),
    [switch]$DryRun,
    [string]$ListFile
)

function Test-Wrappable([string]$lit) {
    if ($lit.Length -eq 0) { return $false }
    if ($lit -notmatch '[\u4e00-\u9fff]') { return $false }
    if ($lit -match '[*|]') { return $false }              # 文件对话框过滤器等"值"
    if ($lit -match '\\[dDsSwWbBzZ]') { return $false }    # 正则模式
    return $true
}

$allWrapped = New-Object System.Collections.Generic.List[string]
$totalWrapped = 0

foreach ($name in $Files) {
    $path = Join-Path $SrcDir $name
    if (-not (Test-Path $path)) { Write-Output "跳过（不存在）: $name"; continue }

    $text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    $out = New-Object System.Text.StringBuilder
    $wrapped = 0
    $i = 0
    $n = $text.Length

    while ($i -lt $n) {
        $c = $text[$i]

        # ---- 行注释 / 块注释：原样跳过 ----
        if ($c -eq '/' -and $i + 1 -lt $n -and $text[$i+1] -eq '/') {
            $j = $text.IndexOf("`n", $i); if ($j -lt 0) { $j = $n }
            [void]$out.Append($text.Substring($i, $j - $i)); $i = $j; continue
        }
        if ($c -eq '/' -and $i + 1 -lt $n -and $text[$i+1] -eq '*') {
            $j = $text.IndexOf('*/', $i + 2); if ($j -lt 0) { $j = $n } else { $j += 2 }
            [void]$out.Append($text.Substring($i, $j - $i)); $i = $j; continue
        }

        if ($c -eq '"') {
            # 已包裹：整体复制
            if ($i -ge 4 -and $text.Substring($i - 4, 4) -eq 'L.T(') {
                $j = $i + 1; $end = -1
                while ($j -lt $n) {
                    if ($text[$j] -eq '\') { $j += 2; continue }
                    if ($text[$j] -eq '"') { $end = $j; break }
                    $j++
                }
                if ($end -lt 0) { [void]$out.Append($c); $i++; continue }
                [void]$out.Append($text.Substring($i - 4, $end - $i + 1 + 2)); $i = $end + 3; continue
            }

            # 判断 @ 前缀（逐字字符串）
            $verbatim = $false
            $p = $i - 1
            while ($p -ge 0 -and ($text[$p] -eq '@' -or $text[$p] -eq '$')) {
                if ($text[$p] -eq '@') { $verbatim = $true }
                $p--
            }

            $start = $i
            $j = $i + 1
            $end = -1
            while ($j -lt $n) {
                if (-not $verbatim -and $text[$j] -eq '\') { $j += 2; continue }
                if ($text[$j] -eq '"') {
                    if ($verbatim -and $j + 1 -lt $n -and $text[$j+1] -eq '"') { $j += 2; continue }
                    $end = $j; break
                }
                $j++
            }
            if ($end -lt 0) { [void]$out.Append($c); $i++; continue }

            $raw = $text.Substring($start, $end - $start + 1)
            $lit = $text.Substring($start + 1, $end - $start - 1)
            if ((Test-Wrappable $lit) -and -not $verbatim) {
                [void]$out.Append('L.T(').Append($raw).Append(')')
                $wrapped++
                $allWrapped.Add($lit)
            } else {
                [void]$out.Append($raw)
            }
            $i = $end + 1
            continue
        }

        [void]$out.Append($c); $i++
    }

    $newText = $out.ToString()

    # ---------- 校验：去包裹后必须与原文逐字节一致 ----------
    # 两个坑：
    #   1) 替换串必须写 '${1}'，否则 $1 后面的中文会被当成变量名的一部分；
    #   2) 字符类必须排除换行（[^"\\\r\n]），否则正则会把跨行的两个引号当成一个字面量，
    #      校验会"假通过"——Archive.cs 之前就是这样漏过去的。
    $stripped = [regex]::Replace($newText, 'L\.T\(("(?:[^"\\\r\n]|\\.)*")\)', '${1}')
    $ok = ($stripped -eq $text)
    if (-not $ok) { Write-Output ("  [校验失败] {0}: 去包裹后与原文不一致，未落盘" -f $name) }

    if ($ok -and -not $DryRun -and $wrapped -gt 0) {
        [IO.File]::WriteAllText($path, $newText, (New-Object Text.UTF8Encoding($true)))
    }
    $totalWrapped += $wrapped
    Write-Output ("{0,-16} 包裹 {1,4} 处  {2}" -f $name, $wrapped, $(if ($ok) { "校验通过" } else { "未落盘" }))
}

Write-Output "合计: $totalWrapped"
if ($ListFile -and -not $DryRun) {
    $uniq = New-Object System.Collections.Generic.HashSet[string]
    foreach ($v in $allWrapped) { [void]$uniq.Add($v) }
    $sorted = New-Object System.Collections.Generic.List[string]
    foreach ($v in ($uniq | Sort-Object)) { $sorted.Add($v) }
    [IO.File]::WriteAllLines($ListFile, $sorted, (New-Object Text.UTF8Encoding($false)))
    Write-Output "唯一文案: $($uniq.Count) -> $ListFile"
}
