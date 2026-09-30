# 把三组"行对行"译文组装成 src\Strings.cs。
# 关键实现细节：中文键来自"已包裹源码"里的字面量转义文本（如 严重\n），
# 因此写进 C# 时必须把反斜杠再转义一次（\\n），否则会被解释成真实换行，键就对不上了。
param(
    [string]$Root = 'E:\deepseek\AE插件开发'
)

$tools = Join-Path $Root 'tools'
$outFile = Join-Path $Root 'src\Strings.cs'
$groups = @(
    @{ Method = 'AddUi';    En = 'g1-ui.en.txt';    Zh = 'g1-ui.txt' },
    @{ Method = 'AddRules'; En = 'g2-rules.en.txt'; Zh = 'g2-rules.txt' },
    @{ Method = 'AddCore';  En = 'g3-core.en.txt';  Zh = 'g3-core.txt' }
)

function ToCSharp([string]$s) {
    return '"' + $s + '"';
}

$methods = New-Object System.Collections.Generic.List[string]
$total = 0
$problems = 0

foreach ($g in $groups) {
    $zhPath = Join-Path $tools $g.Zh
    $enPath = Join-Path $tools $g.En
    if (-not (Test-Path $zhPath) -or -not (Test-Path $enPath)) {
        Write-Output "缺少文件: $($g.Zh) 或 $($g.En)（该组跳过）"
        $problems++
        continue
    }
    $zh = [IO.File]::ReadAllLines($zhPath, (New-Object Text.UTF8Encoding($false)))
    $en = [IO.File]::ReadAllLines($enPath, (New-Object Text.UTF8Encoding($false)))
    if ($zh.Count -ne $en.Count) {
        Write-Output ("行数不一致 {0}: 中文 {1} / 英文 {2}（该组先留空，补齐译文后重跑本脚本）" -f $g.En, $zh.Count, $en.Count)
        $problems++
        $methods.Add("        private static void $($g.Method)(Dictionary<string, string> map)")
        $methods.Add('        {')
        $methods.Add('        }')
        $methods.Add('')
        continue
    }

    $body = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $zh.Count; $i++) {
        $body.Add('            Add(map, ' + (ToCSharp $zh[$i]) + ', ' + (ToCSharp $en[$i]) + ');')
    }
    $methods.Add("        private static void $($g.Method)(Dictionary<string, string> map)")
    $methods.Add('        {')
    foreach ($line in $body) { $methods.Add($line) }
    $methods.Add('        }')
    $methods.Add('')
    $total += $zh.Count
    Write-Output ("{0,-18} {1,4} 条" -f $g.En, $zh.Count)
}

$head = @(
    '// 英文词条表：键 = 源码里 L.T("...") 中的中文原文（含 \n \t \" 等转义），值 = 英文译文。',
    '// 由 tools\build-strings.ps1 从 tools\g*.en.txt 生成，请勿手改。',
    'using System.Collections.Generic;',
    '',
    'namespace AePluginManager',
    '{',
    '    internal static partial class Strings',
    '    {',
    '        public static Dictionary<string, string> All()',
    '        {',
    '            var map = new Dictionary<string, string>();',
    '            AddUi(map);',
    '            AddRules(map);',
    '            AddCore(map);',
    '            return map;',
    '        }',
    '',
    '        private static void Add(Dictionary<string, string> map, string zh, string en)',
    '        {',
    '            if (!string.IsNullOrEmpty(zh) && !map.ContainsKey(zh)) map[zh] = en;',
    '        }',
    ''
)
$tail = @('    }', '}')

$all = New-Object System.Collections.Generic.List[string]
foreach ($l in $head) { $all.Add($l) }
foreach ($l in $methods) { $all.Add($l) }
foreach ($l in $tail) { $all.Add($l) }

[IO.File]::WriteAllLines($outFile, $all, (New-Object Text.UTF8Encoding($true)))
Write-Output "已生成 $outFile（$total 条词条，$problems 个问题）"
