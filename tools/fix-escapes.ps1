# 修正译文里的"裸引号/裸反斜杠"：这两者在 C# 字面量里非法，会让 Strings.cs 编译失败。
# 规则：`"` 若前面不是反斜杠则补成 `\"`；`\x` 若不是合法转义则补成 `\\x`。
param(
    [string]$Tools = 'E:\deepseek\AE插件开发\tools'
)

foreach ($name in 'g1-ui.en.txt','g2-rules.en.txt','g3-core.en.txt') {
    $path = Join-Path $Tools $name
    if (-not (Test-Path $path)) { continue }
    $lines = [IO.File]::ReadAllLines($path, (New-Object Text.UTF8Encoding($false)))
    $fixedCount = 0
    $out = New-Object System.Collections.Generic.List[string]

    foreach ($line in $lines) {
        $sb = New-Object System.Text.StringBuilder
        $i = 0
        $changed = $false
        while ($i -lt $line.Length) {
            $c = $line[$i]
            if ($c -eq '\') {
                $nxt = if ($i + 1 -lt $line.Length) { $line[$i + 1] } else { '' }
                if ($nxt -ne '' -and 'ntr"\\0abfvux'.IndexOf($nxt) -ge 0) {
                    [void]$sb.Append($c).Append($nxt); $i += 2; continue   # 合法转义
                }
                [void]$sb.Append('\\'); $i++; $changed = $true; continue    # 裸反斜杠
            }
            if ($c -eq '"') {
                [void]$sb.Append('\"'); $i++; $changed = $true; continue     # 裸引号
            }
            [void]$sb.Append($c); $i++
        }
        $newLine = $sb.ToString()
        if ($changed) { $fixedCount++ }
        $out.Add($newLine)
    }

    [IO.File]::WriteAllLines($path, $out, (New-Object Text.UTF8Encoding($false)))
    Write-Output ("{0,-18} 修正 {1} 行" -f $name, $fixedCount)
}
