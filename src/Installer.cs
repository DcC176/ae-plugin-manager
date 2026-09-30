using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AePluginManager
{
    /// <summary>安装方案生成：识别载荷、剔除教程垃圾、列出候选版本、映射安装目标。</summary>
    internal static class Installer
    {
        private static readonly HashSet<string> JunkExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".avi", ".mov", ".mkv", ".flv", ".wmv", ".rmvb", ".m4v", ".webm", ".mp3", ".wav", ".flac",
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".psd",
            ".url", ".lnk", ".torrent", ".nfo", ".ds_store", ".db", ".ini.tmp",
            ".rar", ".zip", ".7z", ".exe", ".msi", ".dmg", ".pkg", ".app", ".apk"
        };

        private static readonly string[] JunkFolderWords =
        {
            "lookae", "shejibaozang", "ads", L.T("广告"), L.T("教程"), L.T("视频教程"), L.T("使用教程"), L.T("官方教程"),
            "__macosx", "crack", L.T("破解补丁"), "$recycle.bin", "system volume information"
        };

        private static readonly string[] IncludeOnDemandExt = { ".txt", ".pdf", ".md", ".docx", ".doc", ".rtf", ".chm" };

        // ==================== 载荷清单 ====================

        private static List<Entry> Collect(string root)
        {
            var all = new List<Entry>();
            CollectInto(root, root, all, 0);
            return all;
        }

        private static void CollectInto(string root, string dir, List<Entry> list, int depth)
        {
            if (depth > 6) return;
            foreach (var f in Directory.GetFiles(dir))
            {
                list.Add(new Entry
                {
                    Full = f,
                    Rel = Rel(root, f),
                    Size = new FileInfo(f).Length
                });
            }
            foreach (var d in Directory.GetDirectories(dir))
            {
                string name = Path.GetFileName(d);
                if (IsJunkFolder(name)) continue;
                CollectInto(root, d, list, depth + 1);
            }
        }

        private static bool IsJunkFolder(string name)
        {
            string low = name.ToLowerInvariant();
            foreach (var w in JunkFolderWords)
                if (low.Contains(w)) return true;
            return false;
        }

        private static string Rel(string root, string path)
        {
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string p = Path.GetFullPath(path);
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? p.Substring(r.Length) : Path.GetFileName(p);
        }

        /// <summary>条目是否属于"可安装载荷"（不含需另存依赖的 .dll）。</summary>
        private static bool IsPayload(Entry e)
        {
            return Kinds.IsPayload(Path.GetExtension(e.Full));
        }

        private static bool IsJunk(Entry e)
        {
            string ext = Path.GetExtension(e.Full);
            if (JunkExt.Contains(ext)) return true;
            if (Path.GetFileName(e.Full).StartsWith("._", StringComparison.Ordinal)) return true;
            string low = e.Full.ToLowerInvariant();
            if (low.Contains(@"\__macosx\")) return true;
            return false;
        }

        private static bool IsIncludeOnDemand(Entry e)
        {
            string ext = Path.GetExtension(e.Full);
            foreach (var x in IncludeOnDemandExt)
                if (string.Equals(ext, x, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(Path.GetFileName(e.Full), "install-as-admin.zip", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ==================== 语言版本优选 ====================

        private static int ChineseScore(string name)
        {
            string low = name.ToLowerInvariant();
            if (low.Contains(L.T("汉化"))) return 100;
            if (low.Contains(L.T("中英"))) return 90;
            if (low.Contains(L.T("中文"))) return 80;
            if (low.Contains(L.T("简体")) || low.Contains("chs") || low.Contains("zh")) return 70;
            if (low.Contains(L.T("英文")) || low.Contains("english")) return 10;
            if (low.Contains("mac")) return -50;
            return 0;
        }

        // ==================== 主流程 ====================

        /// <summary>
        /// 识别一个待安装对象：解压 + 分类 + 列出可安装版本候选。只读，不改动任何目标文件。
        /// 界面据此让用户在候选里自行挑选，然后调 BuildPlan 生成方案。
        /// </summary>
        public static DetectSession Analyze(string source)
        {
            var session = new DetectSession { SourcePath = source };
            var plan = new InstallPlan { SourcePath = source };

            string kind;
            session.WorkDir = Prepare(source, plan, out kind);
            session.Scratch = string.Equals(kind, ".zip", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(kind, ".rar", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(kind, ".7z", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(kind, "payload", StringComparison.OrdinalIgnoreCase);

            // 解压/读取阶段就失败：直接给出人工安装指引
            if (plan.ManualReason != null)
            {
                session.ManualReason = plan.ManualReason;
                session.ManualHint = plan.ManualHint;
                session.Notes.AddRange(plan.Notes);
                session.ManualDir = session.WorkDir;
                return session;
            }

            session.Notes.AddRange(plan.Notes);
            session.JunkCount = 0;
            foreach (var e in Collect(session.WorkDir))
            {
                if (IsPayload(e)) { session.Payload.Add(e); continue; }
                if (IsJunk(e)) { session.JunkCount++; continue; }
                if (IsIncludeOnDemand(e)) { session.Docs.Add(e); continue; }
                session.JunkCount++; // 未识别的杂项一律不装
            }

            if (session.Payload.Count == 0)
            {
                plan.PackageName = Path.GetFileName(source.TrimEnd('\\'));
                HandleNoPayload(session.WorkDir, plan);
                session.ManualReason = plan.ManualReason;
                session.ManualHint = plan.ManualHint;
                session.ManualDir = session.WorkDir;
                session.Installers.AddRange(plan.Installers);   // 只有安装器的包：把清单带给界面
                return session;
            }

            session.PayloadRoot = CommonAncestor(session.WorkDir, session.Payload);
            session.Choices = BuildChoices(session);
            session.DefaultChoice = 0;
            for (int i = 0; i < session.Choices.Count; i++)
                if (session.Choices[i].Preferred) { session.DefaultChoice = i; break; }

            return session;
        }

        /// <summary>按识别结果与用户选定的版本候选，生成最终安装方案。</summary>
        public static InstallPlan BuildPlan(DetectSession session, int choiceIndex, AeVersion ae, bool includeDocs)
        {
            var plan = new InstallPlan
            {
                SourcePath = session.SourcePath,
                TempDir = session.WorkDir,
                Scratch = session.Scratch
            };
            if (ae == null) { plan.ManualReason = L.T("没有可用的 AE 版本"); return plan; }

            plan.Notes.AddRange(session.Notes);
            plan.Notes.Add(string.Format(L.T("来源：{0}"), Path.GetFileName(session.SourcePath.TrimEnd('\\'))));
            if (session.JunkCount > 0)
                plan.Notes.Add(string.Format(L.T("已自动排除 {0} 个非插件文件（教程视频/图片/网址/安装器/说明文档等）"), session.JunkCount));
            plan.Skipped.Add(string.Format(L.T("{0} 个非插件文件"), session.JunkCount));

            if (session.ManualReason != null)
            {
                plan.ManualReason = session.ManualReason;
                plan.ManualHint = session.ManualHint;
                plan.PackageName = Path.GetFileName(session.SourcePath.TrimEnd('\\'));
                plan.Installers.AddRange(session.Installers);   // 界面据此列出可运行的安装器
                return plan;
            }

            if (choiceIndex < 0 || choiceIndex >= session.Choices.Count) choiceIndex = session.DefaultChoice;
            var choice = session.Choices[choiceIndex];
            if (session.Choices.Count > 1)
                plan.Notes.Add(string.Format(L.T("已选安装版本：{0}（共 {1} 个可选）"), choice.Display, session.Choices.Count));

            var items = new List<Entry>();
            foreach (var e in session.Payload)
                if (IsUnder(choice.Root, e.Full)) items.Add(e);

            // 载荷根随之调整：选中子目录时，该目录就是新的载荷根
            string payloadRoot = string.Equals(choice.Root, session.PayloadRoot, StringComparison.OrdinalIgnoreCase)
                ? session.PayloadRoot
                : CommonAncestor(choice.Root, items);

            plan.PackageName = PickPackageName(session.SourcePath, choice.Root, session.WorkDir);
            BuildActions(plan, payloadRoot, choice.Root, items, ae, includeDocs ? session.Docs : null);

            if (plan.Actions.Count == 0)
            {
                plan.ManualReason = L.T("识别到载荷文件，但无法映射到 AE 的安装目录（扩展名不在支持范围内）");
                plan.ManualHint = L.T("支持的类型：.aex/.8bf 特效插件、.jsx/.jsxbin 脚本、.ffx 预设、.plugin 编解码插件。");
            }
            return plan;
        }

        /// <summary>一次性识别并生成默认方案（命令行与快速路径使用）。</summary>
        public static InstallPlan Detect(string source, AeVersion cleanVersion, bool includeDocs)
        {
            var session = Analyze(source);
            return BuildPlan(session, session.DefaultChoice, cleanVersion, includeDocs);
        }

        /// <summary>列出所有可安装的版本候选（根目录 / 中文版 / 英文版 / Mac 版 / 各子目录）。</summary>
        private static List<VariantOption> BuildChoices(DetectSession session)
        {
            var options = new List<VariantOption>();
            var dirs = new List<string>();
            foreach (var d in Directory.GetDirectories(session.PayloadRoot)) dirs.Add(d);

            // 只有一层子目录且其中没有载荷时，再看下一层（例如 "Deep Glow v1.5.5" 下面才是 中文版/英文版）
            if (dirs.Count == 1 && !session.Payload.Exists(delegate (Entry e) { return IsUnder(dirs[0], e.Full); }))
                foreach (var d in Directory.GetDirectories(dirs[0])) dirs.Add(d);

            var rootItems = new List<Entry>();
            foreach (var e in session.Payload)
                if (IsUnder(session.PayloadRoot, e.Full)) rootItems.Add(e);

            // 根目录候选：包含根下的脚本与依赖目录（依赖目录会随脚本一起安装）
            bool rootHasScript = false;
            foreach (var e in rootItems)
                if (Kinds.Classify(Path.GetExtension(e.Full)) == PluginKind.Script) { rootHasScript = true; break; }

            var root = MakeOption(session, session.PayloadRoot, L.T("根目录"), rootHasScript ? 120 : 5);
            options.Add(root);

            foreach (var d in dirs)
            {
                var items = new List<Entry>();
                foreach (var e in session.Payload)
                    if (IsUnder(d, e.Full)) items.Add(e);
                if (items.Count == 0) continue;
                options.Add(MakeOption(session, d, Path.GetFileName(d), ChineseScore(Path.GetFileName(d))));
            }

            if (options.Count > 1)
            {
                options.Sort(delegate (VariantOption a, VariantOption b)
                {
                    int c = b.Score.CompareTo(a.Score);
                    return c != 0 ? c : string.CompareOrdinal(a.Display, b.Display);
                });
                options[0].Preferred = true;
            }
            else if (options.Count == 1)
            {
                options[0].Preferred = true;
            }
            return options;
        }

        private static VariantOption MakeOption(DetectSession session, string root, string label, int score)
        {
            var opt = new VariantOption { Root = root, Score = score, FallbackLabel = label };
            var items = new List<Entry>();
            foreach (var e in session.Payload)
                if (IsUnder(root, e.Full)) items.Add(e);

            long size = 0;
            foreach (var e in items) size += e.Size;
            opt.FileCount = items.Count;
            opt.Bytes = size;

            var kinds = new SortedSet<string>();
            foreach (var e in items)
            {
                var k = Kinds.Classify(Path.GetExtension(e.Full));
                if (k != PluginKind.Other) kinds.Add(Kinds.Label(k));
            }
            var kindList = new List<string>(kinds);
            opt.KindText = string.Join("/", kindList.ToArray());

            string name = string.Equals(root, session.PayloadRoot, StringComparison.OrdinalIgnoreCase)
                ? L.T("根目录（含全部内容）")
                : label;
            if (name.IndexOf("mac", StringComparison.OrdinalIgnoreCase) >= 0) opt.MacOnly = true;

            opt.Display = string.Format(L.T("{0} · {1} 个文件 · {2}{3}"),
                name, opt.FileCount, Fs.SizeText(size), opt.MacOnly ? " · macOS" : "");
            return opt;
        }

        private static string PickPackageName(string source, string variantRoot, string workDir)
        {
            string s = Path.GetFileName(source.TrimEnd('\\'));
            if (!string.IsNullOrEmpty(s))
            {
                // 压缩包名去掉扩展名，作为"包名"更自然（也会成为带结构安装时的壳目录名）
                string ext = Path.GetExtension(s);
                if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".rar", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".7z", StringComparison.OrdinalIgnoreCase))
                    s = Path.GetFileNameWithoutExtension(s);
                return s;
            }
            string v = Path.GetFileName(variantRoot.TrimEnd('\\'));
            if (!string.IsNullOrEmpty(v) && !string.Equals(variantRoot, workDir, StringComparison.OrdinalIgnoreCase)) return v;
            return Path.GetFileName(workDir);
        }

        /// <summary>
        /// 把载荷映射成"源 -> 目标"动作列表。
        /// 目录规则：载荷全部位于同一个目录（含语言变体目录）时直接平铺到 AE 对应目录，
        /// 与现有插件目录的习惯一致；载荷带自己的子目录结构时，保留结构并归到以包名命名的壳目录下。
        /// </summary>
        private static void BuildActions(InstallPlan plan, string payloadRoot, string variantRoot,
                                         List<Entry> items, AeVersion ae, List<Entry> docs)
        {
            var sources = new List<Entry>(items);
            if (docs != null) sources.AddRange(docs);

            int distinctDirs = 0;
            string singleDir = null;
            foreach (var e in sources)
            {
                string dir = Path.GetDirectoryName(e.Full);
                if (singleDir == null) { singleDir = dir; distinctDirs = 1; }
                else if (!string.Equals(dir, singleDir, StringComparison.OrdinalIgnoreCase)) distinctDirs = 2;
            }
            bool flat = distinctDirs <= 1;

            // 包名是压缩包名，与载荷壳目录名重复时不必再套一层
            bool needWrapper = !flat && !string.Equals(Path.GetFileName(variantRoot.TrimEnd('\\')),
                plan.PackageName, StringComparison.OrdinalIgnoreCase);

            foreach (var e in sources)
            {
                string ext = Path.GetExtension(e.Full);

                // .ffx 预设若位于页脚（_ffx）依赖目录中，会随依赖目录一起复制，避免重复放置
                if (ext.Equals(".ffx", StringComparison.OrdinalIgnoreCase) && IsUnderAnyDep(payloadRoot, e.Full))
                    continue;

                var kind = Kinds.Classify(ext);
                if (kind == PluginKind.Other)
                {
                    if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) kind = PluginKind.Effect;
                    else continue;
                }

                string targetDir = ae.PathOf(kind);
                if (!flat)
                {
                    string sub = SubDir(variantRoot, e.Full);
                    if (needWrapper) targetDir = Path.Combine(targetDir, plan.PackageName);
                    if (sub.Length > 0) targetDir = Path.Combine(targetDir, sub);
                }

                var action = new PlanAction
                {
                    SourcePath = e.Full,
                    TargetPath = Path.Combine(targetDir, Path.GetFileName(e.Full)),
                    Kind = kind,
                    Size = e.Size
                };
                if (File.Exists(action.TargetPath))
                {
                    action.TargetExists = true;
                    action.ExistingArch = Fs.PeArch(action.TargetPath);
                }
                plan.Actions.Add(action);
            }

            // 依赖目录：与载荷同级的 *_ffx 等资源目录，必须一起复制，否则脚本或插件会缺资源
            bool hasScript = false;
            foreach (var a in plan.Actions) if (a.Kind == PluginKind.Script) { hasScript = true; break; }
            PluginKind depKind = hasScript ? PluginKind.Script : PluginKind.Effect;

            foreach (var d in Directory.GetDirectories(payloadRoot))
            {
                string name = Path.GetFileName(d);
                if (!(name.EndsWith("_ffx", StringComparison.OrdinalIgnoreCase) ||
                      name.EndsWith("Data", StringComparison.OrdinalIgnoreCase))) continue;

                string depDir = Path.GetDirectoryName(d);
                string destRoot = ae.PathOf(depKind);
                if (!flat)
                {
                    string sub = SubDir(variantRoot, d);
                    if (needWrapper) destRoot = Path.Combine(destRoot, plan.PackageName);
                    if (sub.Length > 0) destRoot = Path.Combine(destRoot, sub);
                    else destRoot = Path.Combine(destRoot, name);
                }
                else
                {
                    destRoot = Path.Combine(destRoot, name);
                }

                foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories))
                {
                    string rel = Rel(d, f);
                    string target = Path.Combine(destRoot, rel);
                    plan.Actions.Add(new PlanAction
                    {
                        SourcePath = f,
                        TargetPath = target,
                        Kind = depKind,
                        Size = new FileInfo(f).Length,
                        TargetExists = File.Exists(target)
                    });
                }
                plan.Notes.Add(string.Format(L.T("附带依赖目录「{0}」"), name));
            }

            var byKind = new Dictionary<PluginKind, int>();
            foreach (var a in plan.Actions)
            {
                int n;
                byKind.TryGetValue(a.Kind, out n);
                byKind[a.Kind] = n + 1;
            }
            foreach (var kv in byKind)
                plan.Notes.Add(string.Format(L.T("{0}：{1} 个文件"), Kinds.Label(kv.Key), kv.Value));

            int overwrite = 0;
            foreach (var a in plan.Actions) if (a.TargetExists) overwrite++;
            if (overwrite > 0)
                plan.Notes.Add(string.Format(L.T("⚠ 其中 {0} 个文件会覆盖同名文件（覆盖前自动备份，可一键还原）"), overwrite));

            var archBad = new List<string>();
            foreach (var a in plan.Actions)
            {
                if (a.Kind != PluginKind.Effect) continue;
                string arch = Fs.PeArch(a.SourcePath);
                if (arch == "x86" || arch == "ARM")
                    archBad.Add(Path.GetFileName(a.SourcePath) + "（" + Kinds.ArchLabel(arch) + "）");
            }
            if (archBad.Count > 0)
                plan.Notes.Add(L.T("⚠ 位数不匹配，装在 64 位 AE 上不会被加载：") + string.Join("、", archBad.ToArray()));
        }

        private static void HandleNoPayload(string workDir, InstallPlan plan)
        {
            var exes = new List<string>();
            var dmgs = new List<string>();

            // 解压时记录到的超大安装器（在包里，无法从磁盘再次枚举）
            var oversized = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in plan.SkippedInstallers)
            {
                string ext = Path.GetExtension(kv.Key);
                if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase))
                { if (!exes.Contains(kv.Key)) exes.Add(kv.Key); }
                else if (ext.Equals(".dmg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".pkg", StringComparison.OrdinalIgnoreCase))
                { if (!dmgs.Contains(kv.Key)) dmgs.Add(kv.Key); }
                oversized[kv.Key] = kv.Value;
            }

            WalkFind(workDir, exes, dmgs, 0);

            // 建立安装程序清单，挑出推荐的那个（本机架构 + 中文版 + 名称里非 fix）
            foreach (var f in exes)
            {
                var info = Describe(f, workDir);
                info.Chinese = info.FullPath.IndexOf(L.T("中文"), StringComparison.Ordinal) >= 0 ||
                               info.FullPath.IndexOf(L.T("汉化"), StringComparison.Ordinal) >= 0;
                long size;
                if (oversized.TryGetValue(Path.GetFileName(f), out size))
                {
                    info.Size = size;
                    info.NotExtracted = true;
                    info.RelativePath = L.T("在压缩包内（体积过大未解压）");
                }
                plan.Installers.Add(info);
            }
            foreach (var f in dmgs)
            {
                var info = Describe(f, workDir);
                long size;
                if (oversized.TryGetValue(Path.GetFileName(f), out size)) info.Size = size;
                info.NotExtracted = !File.Exists(f);
                plan.Installers.Add(info);
            }
            MarkPreferred(plan.Installers);

            var winExes = new List<InstallerInfo>();
            foreach (var i in plan.Installers)
                if (i.Kind == "exe" || i.Kind == "msi") winExes.Add(i);

            if (winExes.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine(L.T("这个包里没有可直接复制的插件文件，只有安装程序（安装器会自己把 .aex 写到 AE 插件目录）。"));
                sb.AppendLine("本软件会**拉起安装向导**（以你当前权限运行，不静默安装、不加任何隐藏参数），你在向导里点几下装完即可；");
                sb.AppendLine(L.T("安装完成后回到本窗口点「重新扫描」，新装的插件就会出现在「已安装插件」里。"));
                sb.AppendLine();
                sb.AppendLine(L.T("找到的安装程序："));
                foreach (var i in plan.Installers)
                    if (i.Kind == "exe" || i.Kind == "msi") sb.AppendLine("  " + i.Display);
                sb.AppendLine();
                sb.AppendLine(L.T("说明：安装器可能尝试写入 C:\\Program Files\\Adobe\\Common\\Plug-ins（需要管理员权限）。"));
                sb.AppendLine(L.T("若运行时提示权限不足，请改用「以管理员身份运行安装器」按钮。"));
                plan.ManualReason = L.T("包内只有安装程序（.exe/.msi），需要运行安装向导");
                plan.ManualHint = sb.ToString();
            }
            else if (dmgs.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine(L.T("包内只有 macOS 安装包（.dmg/.pkg），无法安装到 Windows 版 AE。"));
                sb.AppendLine();
                foreach (var i in plan.Installers) sb.AppendLine("  " + i.Display);
                plan.ManualReason = L.T("包内只有 macOS 安装包");
                plan.ManualHint = sb.ToString();
            }
            else
            {
                plan.ManualReason = L.T("未在包内找到可安装的插件文件");
                plan.ManualHint = L.T("支持的类型：.aex/.8bf 特效插件、.jsx/.jsxbin 脚本、.ffx 预设、.plugin 编解码插件，\n") +
                                  L.T("以及 .exe/.msi 安装程序（本软件可以拉起安装向导）。\n") +
                                  L.T("如果这个包确实不含以上内容，请确认下载是否完整。");
            }
        }

        /// <summary>从文件名与本机架构推断安装程序的位数、类型。</summary>
        private static InstallerInfo Describe(string path, string workDir)
        {
            string name = Path.GetFileName(path);
            var info = new InstallerInfo
            {
                FullPath = path,
                Kind = Path.GetExtension(path).TrimStart('.').ToLowerInvariant()
            };
            info.Size = File.Exists(path) ? new FileInfo(path).Length : 0;

            string rel = path;
            if (workDir != null && path.StartsWith(Path.GetFullPath(workDir), StringComparison.OrdinalIgnoreCase))
                rel = path.Substring(Path.GetFullPath(workDir).TrimEnd('\\').Length).TrimStart('\\');
            info.RelativePath = Directory.Exists(path) ? "" : Path.GetDirectoryName(rel);

            string low = name.ToLowerInvariant();
            if (low.Contains("_x64") || low.Contains("x64") || low.Contains("64-bit")) info.Arch = "x64";
            else if (low.Contains("_x86") || low.Contains("x86") || low.Contains("32-bit")) info.Arch = "x86";
            return info;
        }

        /// <summary>推荐规则：优先本机架构（x64），其次中文版，再看名称里是否带 fix（修版）后缀。</summary>
        private static void MarkPreferred(List<InstallerInfo> list)
        {
            InstallerInfo best = null;
            int bestScore = int.MinValue;
            foreach (var i in list)
            {
                if (i.Kind == "dmg" || i.Kind == "pkg") continue;
                int score = 0;
                if (i.Arch == "x64") score += 8;
                else if (i.Arch == "x86") score -= 4;      // 64 位 AE 用不上 32 位插件
                if (i.Chinese) score += 4;
                string low = Path.GetFileName(i.FullPath).ToLowerInvariant();
                if (low.Contains("fix") || low.Contains("crack")) score -= 2;
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best != null) best.Preferred = true;
        }

        private static void WalkFind(string dir, List<string> exes, List<string> dmgs, int depth)
        {
            if (depth > 4) return;
            foreach (var f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f);
                if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase))
                { if (!exes.Contains(f)) exes.Add(f); }
                else if (ext.Equals(".dmg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".pkg", StringComparison.OrdinalIgnoreCase))
                { if (!dmgs.Contains(f)) dmgs.Add(f); }
            }
            foreach (var d in Directory.GetDirectories(dir)) WalkFind(d, exes, dmgs, depth + 1);
        }

        // ==================== 输入准备（解压） ====================

        private static string Prepare(string source, InstallPlan plan, out string kind)
        {
            kind = "unknown";
            if (Directory.Exists(source))
            {
                kind = "folder";
                return source;
            }
            if (!File.Exists(source))
            {
                plan.ManualReason = L.T("路径不存在：") + source;
                return null;
            }

            string ext = Path.GetExtension(source).ToLowerInvariant();
            kind = ext;

            if (Kinds.IsPayload(ext))
            {
                // 直接拖入单个插件文件：无需解压，虚拟一个工作目录结构
                kind = "payload";
                string dir = Path.Combine(TempRoot(), "single");
                Fs.TryDeleteDir(dir);
                Fs.EnsureDir(dir);
                Fs.Copy(source, Path.Combine(dir, Path.GetFileName(source)));
                return dir;
            }

            if (ext == ".exe" || ext == ".msi")
            {
                plan.ManualReason = L.T("这是一个安装程序，不是可直接复制的插件包");
                plan.ManualHint = L.T("自带安装向导的插件（.exe/.msi）本软件不代为执行，以免静默写入注册表、系统目录或捆绑其他组件。\n") +
                                  L.T("请点击下方「打开所在目录」，双击安装器按向导安装；装完回到本软件重新扫描即可纳管。");
                return Path.GetDirectoryName(source);
            }

            if (ext == ".zip")
            {
                string dir = Path.Combine(TempRoot(), "unzip");
                Fs.TryDeleteDir(dir);
                Fs.EnsureDir(dir);
                Archive.ExtractZipFiltered(source, dir, plan);
                return dir;
            }

            if (ext == ".rar" || ext == ".7z")
            {
                string dir = Path.Combine(TempRoot(), "unzip");
                Fs.TryDeleteDir(dir);
                Fs.EnsureDir(dir);
                Archive.ExtractWithExternal(source, dir, plan);
                return dir;
            }

            plan.ManualReason = L.T("不支持的文件类型：") + ext;
            plan.ManualHint = L.T("支持拖入：.zip / .rar / .7z 压缩包、文件夹，或单个 .aex/.jsxbin/.jsx/.ffx 文件。");
            return null;
        }

        public static string TempRoot()
        {
            string dir = Path.Combine(Path.GetTempPath(), "AEPluginManager");
            Fs.EnsureDir(dir);
            return dir;
        }

        /// <summary>启动时清理上次遗留的临时目录（仅限本程序的临时目录）。</summary>
        public static void CleanTemp()
        {
            Fs.TryDeleteDir(Path.Combine(Path.GetTempPath(), "AEPluginManager"));
        }

        // ==================== 路径辅助 ====================

        private static bool IsUnder(string dir, string path)
        {
            string d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            string p = Path.GetFullPath(path);
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>相对 dir 的子目录部分（为空表示直接在 dir 下）。</summary>
        private static string SubDir(string dir, string path)
        {
            string d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            string p = Path.GetFullPath(path);
            if (!p.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return "";
            string rest = p.Substring(d.Length);
            int idx = rest.LastIndexOf('\\');
            return idx < 0 ? "" : rest.Substring(0, idx);
        }

        /// <summary>该文件是否位于载荷根下的某个资源目录（*_ffx / *Data）中。</summary>
        private static bool IsUnderAnyDep(string payloadRoot, string file)
        {
            string dir = Path.GetDirectoryName(file);
            while (!string.IsNullOrEmpty(dir) && !string.Equals(dir, payloadRoot, StringComparison.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(dir);
                if (name.EndsWith("_ffx", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("Data", StringComparison.OrdinalIgnoreCase)) return true;
                dir = Path.GetDirectoryName(dir);
            }
            return false;
        }

        /// <summary>所有载荷文件的最深公共目录；单层壳目录会被自动吸收。</summary>
        private static string CommonAncestor(string root, List<Entry> items)
        {
            if (items.Count == 0) return root;
            var parts = new List<string[]>();
            foreach (var e in items)
            {
                string sub = SubDir(root, e.Full);
                parts.Add(sub.Length == 0 ? new string[0] : sub.Split('\\'));
            }

            int common = parts[0].Length;
            for (int i = 1; i < parts.Count; i++)
            {
                int n = Math.Min(common, parts[i].Length);
                int j = 0;
                while (j < n && string.Equals(parts[0][j], parts[i][j], StringComparison.OrdinalIgnoreCase)) j++;
                common = j;
            }

            string dir = root;
            for (int i = 0; i < common; i++) dir = Path.Combine(dir, parts[0][i]);
            return dir;
        }
    }
}
