# 逐行包裹脚本（用于 wrap-lt.ps1 拒绝处理的文件）：
# 只在该行"非注释区"里查找含中文的字符串字面量并包成 L.T("...")，然后整体去包裹回验。
param(
    [string]$File = 'E:\deepseek\AE插件开发\src\Archive.cs'
)

$lines = [IO.File]::ReadAllLines($File, [Text.Encoding]::UTF8)

function Wrap-Line([string]$line, [ref]$count) {
    # 找出行注释起点（字符串内的 // 需要跳过）
    $commentAt = -1
    $i = 0
    $inStr = $false
    while ($i -lt $line.Length) {
        $c = $line[$i]
        if ($inStr) {
            if ($c -eq '\') { $i += 2; continue }
            if ($c -eq '"') { $inStr = $false }
        } else {
            if ($c -eq '"') { $inStr = $true }
            elseif ($c -eq '/' -and $i + 1 -lt $line.Length -and $line[$i+1] -eq '/') { $commentAt = $i; break }
            elseif ($c -eq '@' -and $i + 1 -lt $line.Length -and $line[$i+1] -eq '"') { return $line }   # 逐字字符串整行跳过
        }
        $i++
    }
    $codeEnd = if ($commentAt -ge 0) { $commentAt } else { $line.Length }
    $code = $line.Substring(0, $codeEnd)
    $tail = $line.Substring($codeEnd)

    if ($code -notmatch '[\u4e00-\u9fff]') { return $line }

    $out = New-Object System.Text.StringBuilder
    $j = 0
    while ($j -lt $code.Length) {
        $c = $code[$j]
        if ($c -eq '"') {
            $start = $j
            $k = $j + 1
            $end = -1
            while ($k -lt $code.Length) {
                if ($code[$k] -eq '\') { $k += 2; continue }
                if ($code[$k] -eq '"') { $end = $k; break }
                $k++
            }
            if ($end -lt 0) { [void]$out.Append($code.Substring($j)); break }
            $raw = $code.Substring($start, $end - $start + 1)
            $lit = $code.Substring($start + 1, $end - $start - 1)
            $already = $start -ge 4 -and $code.Substring($start - 4, 4) -eq 'L.T('
            $isRegex = $lit -match '\\[dDsSwWbBzZ]'
            if ((-not $already) -and (-not $isRegex) -and $lit -match '[\u4e00-\u9fff]') {
                [void]$out.Append('L.T(').Append($raw).Append(')')
                $count.Value++
            } else {
                [void]$out.Append($raw)
            }
            $j = $end + 1
            continue
        }
        [void]$out.Append($c); $j++
    }
    return $out.ToString() + $tail
}

$count = 0
$outLines = New-Object System.Collections.Generic.List[string]
foreach ($line in $lines) { $outLines.Add((Wrap-Line $line ([ref]$count))) }

$newText = ($outLines -join "`n")
$origText = [IO.File]::ReadAllText($File, [Text.Encoding]::UTF8)
# 文件末尾若原本有换行，ReadAllLines 会丢掉，这里补回来
if ($origText.EndsWith("`n") -and -not $newText.EndsWith("`n")) { $newText += "`n" }
$stripped = [regex]::Replace($newText, 'L\.T\(("(?:[^"\\\r\n]|\\.)*")\)', '${1}')
if ($stripped -ne $origText) {
    Write-Output ("校验失败：去包裹后与原文不一致，未落盘（原文 {0} 字符 / 结果 {1} 字符）" -f $origText.Length, $stripped.Length)
    exit 1
}
if ($count -eq 0) { Write-Output "没有任何可包裹的字面量"; exit 0 }
[IO.File]::WriteAllText($File, $newText, (New-Object Text.UTF8Encoding($false)))
Write-Output "包裹 $count 处，校验通过：$File"
