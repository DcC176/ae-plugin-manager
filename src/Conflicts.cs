using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AePluginManager
{
    /// <summary>冲突与不适用检测。纯分析，不修改文件。</summary>
    internal static class ConflictDetector
    {
        public sealed class Result
        {
            public List<Conflict> Conflicts = new List<Conflict>();
            public List<InstalledItem> Orphans = new List<InstalledItem>();
        }

        public static Result Analyze(List<AeVersion> versions, List<InstalledItem> installed)
        {
            var result = new Result();
            var conflicts = result.Conflicts;

            RuleAeRunning(conflicts);
            RuleMisplaced(versions, conflicts);
            RuleSameVersionOverride(installed, conflicts);
            RuleArchMismatch(installed, conflicts);
            RuleResiduals(versions, conflicts);
            RuleOrphans(versions, installed, result);

            conflicts.Sort((a, b) => a.Level.CompareTo(b.Level));
            return result;
        }

        // ---------- 规则 1：AE 正在运行 ----------
        private static void RuleAeRunning(List<Conflict> conflicts)
        {
            if (!AeEnv.IsAeRunning()) return;
            conflicts.Add(new Conflict
            {
                Rule = L.T("运行状态"),
                Level = ConflictLevel.Medium,
                Title = L.T("After Effects 正在运行"),
                Detail = L.T("检测到 AfterFX 进程。安装/卸载会改写插件目录，AE 运行时可能占用文件导致写入失败，") +
                         L.T("且 AE 退出时会回写插件缓存。建议先关闭 AE 再执行安装或卸载。"),
                Advice = L.T("关闭 After Effects 后重新扫描。"),
                Repair = RepairKind.CloseAe,
                RepairLabel = L.T("查看关闭方法"),
                RepairDetail = L.T("本软件不会强制结束 AE 进程（可能让你丢失未保存的工程）。\n") +
                               L.T("请在 AE 里保存工程后正常退出，再回到本软件点「重新检测」。")
            });
        }

        // ---------- 规则 2：放错目录 ----------
        private static void RuleMisplaced(List<AeVersion> versions, List<Conflict> conflicts)
        {
            foreach (var ae in versions)
            {
                var placed = new List<InstalledItem>();

                // Scripts 目录下的插件文件（.aex/.8bf）——AE 不会从 Scripts 加载插件
                foreach (var root in new[] { ae.ScriptsDir, Path.Combine(ae.ScriptsDir, "ScriptUI Panels") })
                {
                    if (!Directory.Exists(root)) continue;
                    var dirs = new List<string>();
                    dirs.Add(root);
                    dirs.AddRange(Directory.GetDirectories(root));
                    foreach (var dir in dirs)
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (var f in Directory.GetFiles(dir, "*.aex"))
                        {
                            if (Scanner.IsAdobeOwned(f)) continue;
                            placed.Add(Make(ae, f, PluginKind.Effect));
                        }
                        foreach (var f in Directory.GetFiles(dir, "*.8bf"))
                        {
                            if (Scanner.IsAdobeOwned(f)) continue;
                            placed.Add(Make(ae, f, PluginKind.Effect));
                        }
                    }
                }

                if (placed.Count > 0)
                {
                    var c = new Conflict
                    {
                        Rule = L.T("放错目录"),
                        Level = ConflictLevel.High,
                        Title = string.Format(L.T("AE {0}：{1} 个特效插件文件放在了 Scripts 目录"), ae.Version, placed.Count),
                        Detail = L.T("After Effects 只会从 Plug-ins 目录加载 .aex/.8bf。放在 Scripts 里的文件不会被加载，") +
                                 L.T("但如果 Plug-ins 里存在同名文件，两个副本的不同版本会造成\"装了却没生效\"或随机加载旧版的现象。"),
                        Advice = L.T("确认 Plug-ins 下已有可用副本后，把 Scripts 里的多余副本移入回收区。"),
                        Repair = RepairKind.MoveToRecycle,
                        RepairLabel = L.T("移入回收区（清理错位文件）")
                    };

                    // 修复方案：Plug-ins 下已有同名副本 -> 可直接清理；否则提示先复制过去
                    var orphans = new List<InstalledItem>();
                    var needCopy = new List<string>();
                    foreach (var it in placed)
                    {
                        string twin = Path.Combine(ae.PluginsDir, it.Name);
                        if (File.Exists(twin)) orphans.Add(it);
                        else needCopy.Add(it.Name);
                    }
                    c.RepairItems.AddRange(orphans);

                    if (needCopy.Count > 0)
                    {
                        c.RepairDetail = L.T("Plug-ins 下没有同名文件，直接删除会让这个插件彻底不可用，因此不提供一键操作：\n  ") +
                                         string.Join("\n  ", needCopy.ToArray()) +
                                         L.T("\n\n建议手动把它移动到：") + ae.PluginsDir + L.T("\n（或直接用「安装插件」重新安装一次）");
                        if (orphans.Count == 0) { c.Repair = RepairKind.None; c.RepairLabel = ""; }
                    }
                    if (orphans.Count > 0)
                        c.RepairDetail = string.Format(L.T("将把 {0} 个文件移入回收区（可还原）：\n  {1}"),
                            orphans.Count, string.Join("\n  ", orphans.ConvertAll(i => i.RelPath).ToArray()));

                    c.Items.Clear();
                    c.Items.AddRange(placed);
                    conflicts.Add(c);
                }            }
        }

        // ---------- 规则 3：同一版本内重复 ----------
        private static void RuleSameVersionOverride(List<InstalledItem> installed, List<Conflict> conflicts)
        {
            var byVersion = new Dictionary<string, List<InstalledItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in installed)
            {
                if (Scanner.IsAdobeOwned(it.FullPath)) continue;
                string key = it.Ae.Root + "|" + NormName(it.Name);
                List<InstalledItem> bucket;
                if (!byVersion.TryGetValue(key, out bucket)) { bucket = new List<InstalledItem>(); byVersion[key] = bucket; }
                bucket.Add(it);
            }

            foreach (var kv in byVersion)
            {
                List<List<InstalledItem>> clusters = ClusterByBaseName(kv.Value);
                foreach (var items in clusters)
                {
                    if (items.Count < 2) continue;

                    bool sizesDiffer = false;
                    for (int i = 1; i < items.Count; i++)
                        if (items[i].Size != items[0].Size) { sizesDiffer = true; break; }

                    string title = string.Format(L.T("AE {0}：\"{1}\" 存在 {2} 个副本"), items[0].Ae.Version,
                        Path.GetFileNameWithoutExtension(items[0].Name), items.Count);

                    var c = new Conflict
                    {
                        Rule = L.T("同版本重复"),
                        Level = ConflictLevel.High,
                        Title = title,
                        Detail = L.T("同一版本内出现同名插件的多个副本：") + string.Join("、", items.ConvertAll(i => i.RelPath).ToArray()) +
                                 (sizesDiffer ? L.T("。文件大小不一致，说明版本不同，AE 实际加载哪一个取决于目录扫描顺序。") : "。"),
                        Advice = L.T("保留一份，其余移入回收区；保留哪一份由你决定。"),
                        Repair = RepairKind.MoveToRecycle,
                        RepairLabel = L.T("选择保留哪一份…"),
                        RepairNeedsPick = true,
                        RepairDetail = string.Format(
                            L.T("这个插件在 AE {0} 里有 {1} 个副本，软件不替你决定保留哪个（哪份更新、哪份是你想要的，只有你知道）。\n") +
                            L.T("点「选择保留哪一份…」会列出全部副本的大小与修改时间，你挑一个保留，其余的移入回收区（可还原）。"),
                            items[0].Ae.Version, items.Count)
                    };

                    c.Items.AddRange(items);
                    conflicts.Add(c);
                }
            }
        }

        // ---------- 规则 5：位数不匹配 ----------
        private static void RuleArchMismatch(List<InstalledItem> installed, List<Conflict> conflicts)
        {
            var bad = new List<InstalledItem>();
            foreach (var it in installed)
            {
                if (it.IsBundle) continue;
                if (it.Arch != "x86" && it.Arch != "ARM") continue;
                bad.Add(it);
            }
            if (bad.Count == 0) return;

            var c = new Conflict
            {
                Rule = L.T("位数不匹配"),
                Level = ConflictLevel.High,
                Title = string.Format(L.T("{0} 个插件与 AE 位数不匹配"), bad.Count),
                Detail = L.T("这些插件是 32 位（或 ARM）二进制，而 AE CC 2019 之后只有 64 位版本，加载时会被静默忽略。") +
                         L.T("典型现象：插件装在 Plug-ins 里，效果菜单里却找不到。"),
                Advice = L.T("下载对应的 64 位版本重新安装；旧副本移入回收区。"),
                Repair = RepairKind.MoveToRecycle,
                RepairLabel = L.T("移入回收区（清理不适用插件）"),
                RepairDetail = string.Format(L.T("这些文件在 64 位 AE 上永远不会被加载，将移入回收区（可还原）：\n  {0}"),
                    string.Join("\n  ", bad.ConvertAll(i => i.RelPath).ToArray()))
            };
            c.Items.AddRange(bad);
            c.RepairItems.AddRange(bad);
            conflicts.Add(c);
        }

        // ---------- 规则 6：残留文件 ----------
        private static void RuleResiduals(List<AeVersion> versions, List<Conflict> conflicts)
        {
            var junk = new List<InstalledItem>();
            foreach (var ae in versions)
            {
                foreach (var root in new[] { ae.PluginsDir, ae.ScriptsDir, ae.PresetsDir })
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (Scanner.IsAdobeOwned(f)) continue;          // Adobe 自带组件的残留不作为插件问题
                        string ext = Path.GetExtension(f);
                        if (ext.Equals(".bak", StringComparison.OrdinalIgnoreCase) ||
                            ext.Equals(".old", StringComparison.OrdinalIgnoreCase) ||
                            ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal))
                        {
                            var fi = new FileInfo(f);
                            junk.Add(new InstalledItem
                            {
                                Ae = ae,
                                FullPath = f,
                                RelPath = RelTo(ae, f),
                                Name = fi.Name,
                                Kind = PluginKind.Other,
                                Size = fi.Length,
                                Modified = fi.LastWriteTime
                            });
                        }
                    }
                }
            }
            if (junk.Count == 0) return;

            var c = new Conflict
            {
                Rule = L.T("安装残留"),
                Level = ConflictLevel.Low,
                Title = string.Format(L.T("{0} 个安装残留文件"), junk.Count),
                Detail = L.T("插件更新或卸载后留下的 .bak/.old/.tmp 备份文件。它们不会被 AE 加载，只会让插件目录变乱，") +
                         L.T("部分破解补丁会把旧版插件改名成 .bak 留在原地，长期堆积后难以判断哪个是生效版本。"),
                Advice = L.T("可安全移入回收区。"),
                Repair = RepairKind.MoveToRecycle,
                RepairLabel = L.T("移入回收区（清理残留）"),
                RepairDetail = string.Format(L.T("这些文件不会被 AE 加载，将移入回收区（可还原）：\n  {0}"),
                    string.Join("\n  ", junk.ConvertAll(i => i.RelPath).ToArray()))
            };
            c.Items.AddRange(junk);
            c.RepairItems.AddRange(junk);
            conflicts.Add(c);
        }

        // ---------- 规则 7：孤立的插件资源目录 ----------
        private static void RuleOrphans(List<AeVersion> versions, List<InstalledItem> installed, Result result)
        {
            foreach (var ae in versions)
            {
                // 插件旁边的资源目录（<插件名>_ffx / <插件名> Data）：脚本或插件被移除后剩下的空壳
                if (!Directory.Exists(ae.PluginsDir)) continue;
                foreach (var aex in Directory.GetFiles(ae.PluginsDir, "*.aex"))
                {
                    string baseName = Path.GetFileNameWithoutExtension(aex);
                    foreach (var guess in new[] { baseName + "_ffx", baseName + " Presets", baseName + " Data" })
                    {
                        string dir = Path.Combine(ae.PluginsDir, guess);
                        if (Directory.Exists(dir))
                            result.Orphans.Add(Bundle(ae, dir, PluginKind.Effect));
                    }
                }
            }
        }

        private static InstalledItem Bundle(AeVersion ae, string dir, PluginKind kind)
        {
            return new InstalledItem
            {
                Ae = ae,
                FullPath = dir,
                RelPath = RelTo(ae, dir),
                Name = Path.GetFileName(dir),
                Kind = kind,
                Size = Fs.DirSize(dir),
                IsBundle = true,
                Modified = SafeDirTime(dir)
            };
        }

        private static DateTime SafeDirTime(string dir)
        {
            try { return Directory.GetLastWriteTime(dir); } catch { return DateTime.MinValue; }
        }

        /// <summary>把检测结果转成纯文本报告（命令行与日志用）。</summary>
        public static string ToText(Result analysis, int installedCount)
        {
            var sb = new StringBuilder();
            int high = 0, med = 0, low = 0;
            foreach (var c in analysis.Conflicts)
            {
                if (c.Level == ConflictLevel.High) high++;
                else if (c.Level == ConflictLevel.Medium) med++;
                else low++;
            }
            sb.AppendLine(string.Format(L.T("已安装条目：{0} 个；问题：{1} 项（严重 {2} / 注意 {3} / 提示 {4}）"),
                installedCount, analysis.Conflicts.Count, high, med, low));
            sb.AppendLine();
            foreach (var c in analysis.Conflicts)
            {
                sb.AppendLine(string.Format("[{0}] {1}", LevelName(c.Level), c.Title));
                sb.AppendLine("    " + c.Detail);
                if (c.Advice.Length > 0) sb.AppendLine(L.T("    建议：") + c.Advice);
                if (c.CanRepair) sb.AppendLine(L.T("    修复方案：") + c.RepairLabel + " —— " + c.RepairDetail.Replace("\n", "\n    "));
                int n = 0;
                foreach (var it in c.Items)
                {
                    if (n++ >= 8) { sb.AppendLine(L.T("    … 以及另外 ") + (c.Items.Count - 8) + L.T(" 个")); break; }
                    sb.AppendLine("      · " + it.FullPath);
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string LevelName(ConflictLevel level)
        {
            switch (level)
            {
                case ConflictLevel.High: return L.T("严重");
                case ConflictLevel.Medium: return L.T("注意");
                default: return L.T("提示");
            }
        }

        // ---------- 辅助 ----------
        private static InstalledItem Make(AeVersion ae, string path, PluginKind kind)        {
            var fi = new FileInfo(path);
            return new InstalledItem
            {
                Ae = ae,
                FullPath = path,
                RelPath = RelTo(ae, path),
                Name = fi.Name,
                Kind = kind,
                Size = fi.Length,
                Arch = kind == PluginKind.Effect ? Fs.PeArch(path) : "",
                Modified = fi.LastWriteTime
            };
        }

        private static string RelTo(AeVersion ae, string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(ae.Root).TrimEnd('\\') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
        }

        /// <summary>归一化插件名：用于识别"同一个插件的不同文件名写法"。</summary>
        private static string NormName(string fileName)
        {
            string s = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
            s = s.Replace("'", "").Replace("’", "").Replace(" ", "").Replace("-", "").Replace("_", "").Replace(".", "");
            s = Regex.Replace(s, @"(v|ver|version)?\d+$", "");   // 去掉结尾版本号/序号
            s = Regex.Replace(s, @"(cn|chs|zh|zhcn|en|eng|mac|win|x64)$", "");
            return s;
        }

        /// <summary>同一个归一化名里，只有"原始文件名相同"的才算重复，避免 AEPixelSorter2 与 DeepGlow2 被误判为一组。</summary>
        private static List<List<InstalledItem>> ClusterByBaseName(List<InstalledItem> items)
        {
            var map = new Dictionary<string, List<InstalledItem>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var it in items)
            {
                string key = Path.GetFileNameWithoutExtension(it.Name).Trim();
                List<InstalledItem> bucket;
                if (!map.TryGetValue(key, out bucket)) { bucket = new List<InstalledItem>(); map[key] = bucket; order.Add(key); }
                bucket.Add(it);
            }
            var result = new List<List<InstalledItem>>();
            foreach (var key in order)
            {
                var bucket = map[key];
                bucket.Sort((a, b) => string.CompareOrdinal(a.RelPath, b.RelPath));
                result.Add(bucket);
            }
            return result;
        }
    }
}
