using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AePluginManager
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            L.Init(args);   // 先定语言，后面的界面与报告文案才查得到词条

            // 先统一取出所有带值参数，再分派命令（顺序无关）
            string detectPath = ValueOf(args, "--detect");
            string versionFilter = ValueOf(args, "--version");
            string shotDir = ValueOf(args, "--shot");

            // 自检截图优先：可同时带 --detect 以核验"安装方案预览"的渲染
            if (shotDir != null)
            {
                Environment.ExitCode = ShotCommand(shotDir, detectPath, ValueOf(args, "--detect2"),
                    ValueOf(args, "--choice"), Array.IndexOf(args, "--runinstaller") >= 0);
                return;
            }
            if (detectPath != null)
            {
                Environment.ExitCode = DetectCommand(detectPath, versionFilter);
                return;
            }
            if (ValueOf(args, "--install") != null)
            {
                Environment.ExitCode = InstallCommand(ValueOf(args, "--install"), versionFilter);
                return;
            }
            if (Array.IndexOf(args, "--sessions") >= 0)
            {
                Environment.ExitCode = SessionsCommand();
                return;
            }
            if (ValueOf(args, "--restore") != null)
            {
                Environment.ExitCode = RestoreCommand(ValueOf(args, "--restore"));
                return;
            }
            if (ValueOf(args, "--uninstall") != null)
            {
                Environment.ExitCode = UninstallCommand(ValueOf(args, "--uninstall"));
                return;
            }
            if (Array.IndexOf(args, "--conflicts") >= 0)
            {
                Environment.ExitCode = ConflictsCommand();
                return;
            }
            if (ValueOf(args, "--list") != null || Array.IndexOf(args, "--list") >= 0)
            {
                Environment.ExitCode = ListCommand();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Ui.EnsureFonts();

            var versions = AeEnv.Discover();
            if (versions.Count == 0)
            {
                Ui.Error(L.T("没有找到任何 After Effects 安装。\n\n") +
                         L.T("本软件通过以下位置查找 AE：\n") +
                         "  E:\\Adobe、D:\\Adobe、C:\\Program Files\\Adobe\n" +
                         L.T("以及注册表登记的安装路径。"));
                return;
            }

            var exe = new Executor(versions, null);
            Installer.CleanTemp();
            exe.CleanupSessions();

            Application.Run(new MainForm(versions, exe));
        }

        // ==================== 命令行（自检/排错用） ====================

        private static string ValueOf(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        /// <summary>自检：把 4 个标签页分别渲染为 PNG，用于核验界面布局（不依赖窗口焦点）。</summary>
        private static int ShotCommand(string outDir, string detectPath, string detect2, string choice, bool runInstaller)
        {
            Console.Error.WriteLine(L.T("[shot] 开始 outDir=") + outDir + " detect=" + (detectPath ?? "<null>") + " run=" + runInstaller);
            try
            {
                var versions = AeEnv.Discover();
                if (versions.Count == 0) { WriteReport(L.T("没有找到 AE 安装")); return 2; }
                var exe = new Executor(versions, null);

                int choiceIndex = 0;
                if (!string.IsNullOrEmpty(choice)) int.TryParse(choice, out choiceIndex);

                Directory.CreateDirectory(outDir);
                using (var form = new MainForm(versions, exe))
                {
                    form.Show();
                    Application.DoEvents();
                    string[] names = { "installed", "conflicts", "install", "recycle" };
                    string current = detectPath;
                    for (int i = 0; i < names.Length; i++)
                    {
                        // 安装页可以先后核验两个包（例如"需人工安装"与"多候选"两种情形）
                        if (i == 2 && !string.IsNullOrEmpty(detect2)) current = detect2;
                        if (i == 2 && !string.IsNullOrEmpty(current))
                        {
                            form.LoadPathForTest(current);
                            Application.DoEvents();
                            form.SelectChoiceForTest(choiceIndex);
                            // 自检开关 --runinstaller：真的运行选中的安装器，验证"运行→重扫→报告新增"链路
                            if (runInstaller)
                            {
                                form.RunSelectedInstallerForTest();
                                Application.DoEvents();
                            }
                        }
                        else
                        {
                            form.SelectTab(i);
                        }
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(300);
                        Application.DoEvents();
                        using (var bmp = new System.Drawing.Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                        {
                            form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height));
                            bmp.Save(Path.Combine(outDir, "tab-" + names[i] + ".png"),
                                System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    form.Close();
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(L.T("[shot] 失败: ") + ex);
                WriteReport(L.T("截图失败：") + ex);
                return 5;
            }
        }

        private static int ConflictsCommand()
        {
            var versions = AeEnv.Discover();
            var installed = Scanner.Scan(versions);
            var analysis = ConflictDetector.Analyze(versions, installed);
            WriteReport(ConflictDetector.ToText(analysis, installed.Count));
            return 0;
        }

        private static int ListCommand()
        {
            var versions = AeEnv.Discover();
            var lines = new List<string>();
            lines.Add(L.F("发现 {0} 个 AE 安装：", versions.Count));
            foreach (var v in versions)
                lines.Add(string.Format("  AE {0}  {1}", v.Version, v.Root));

            lines.Add("");
            lines.Add(L.T("共享插件目录（只读检测）："));
            foreach (var d in AeEnv.SharedPluginDirs())
                lines.Add("  " + d);

            string report = string.Join(Environment.NewLine, lines.ToArray());
            WriteReport(report);
            return 0;
        }

        private static AeVersion FindVersion(string filter)
        {
            var versions = AeEnv.Discover();
            if (versions.Count == 0) return null;
            if (string.IsNullOrEmpty(filter)) return versions[0];
            foreach (var v in versions) if (v.Version == filter) return v;
            return null;
        }

        /// <summary>命令行安装：与界面走同一套检测、备份、执行逻辑。</summary>
        private static int InstallCommand(string path, string versionFilter)
        {
            var ae = FindVersion(versionFilter);
            if (ae == null) { WriteReport(L.T("没有找到 AE ") + versionFilter); return 2; }

            var plan = Installer.Detect(path, ae, false);
            if (plan.ManualReason != null)
            {
                WriteReport(L.T("【需要人工安装】") + plan.ManualReason + Environment.NewLine + plan.ManualHint);
                Installer.CleanTemp();
                return 3;
            }

            var exe = new Executor(AeEnv.Discover(), null);
            var result = exe.Apply(plan, ae);
            var sb = new StringBuilder();
            sb.AppendLine(L.T("包名：") + plan.PackageName);
            sb.AppendLine(L.T("目标：AE ") + ae.Version);
            sb.AppendLine(L.T("结果：") + result.Message);
            if (result.RolledBack) sb.AppendLine(L.T("（已整体回滚，AE 目录保持原样）"));
            sb.AppendLine(L.T("数据目录：") + exe.DataDir);
            foreach (var s in exe.LoadSessions())
                sb.AppendLine(L.T("会话 ") + s.Id + "　" + s.OpLabel + "　" + s.Records.Count + L.T(" 个条目　") + s.Description);
            WriteReport(sb.ToString());
            Installer.CleanTemp();
            return result.Failed == 0 ? 0 : 4;
        }

        private static int SessionsCommand()
        {
            var exe = new Executor(AeEnv.Discover(), null);
            exe.CleanupSessions();
            var sb = new StringBuilder();
            sb.AppendLine(L.T("数据目录：") + exe.DataDir);
            foreach (var s in exe.LoadSessions())
            {
                sb.AppendLine(string.Format(L.T("{0}　{1}　{2}　{3} 个条目　{4}"),
                    s.Id, s.OpLabel, s.Time.ToString("yyyy-MM-dd HH:mm:ss"), s.Records.Count, s.Description));
                foreach (var r in s.Records)
                    sb.AppendLine("    " + r.Type + "  " + r.Source);
            }
            WriteReport(sb.ToString());
            return 0;
        }

        private static int RestoreCommand(string id)
        {
            var exe = new Executor(AeEnv.Discover(), null);
            foreach (var s in exe.LoadSessions())
            {
                if (!string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                var result = exe.Restore(s);
                WriteReport(L.T("还原 ") + s.Id + "：" + result.Message);
                return result.Failed == 0 ? 0 : 4;
            }
            WriteReport(L.T("没有找到会话 ") + id);
            return 2;
        }

        /// <summary>命令行卸载：把指定插件文件/目录移入回收区（不删除）。</summary>
        private static int UninstallCommand(string path)
        {
            var versions = AeEnv.Discover();
            var exe = new Executor(versions, null);

            string full;
            try { full = Path.GetFullPath(path); }
            catch { WriteReport(L.T("路径无效：") + path); return 2; }
            if (!File.Exists(full) && !Directory.Exists(full)) { WriteReport(L.T("路径不存在：") + full); return 2; }
            if (!exe.IsAllowedTarget(full)) { WriteReport(L.T("拒绝操作：该路径不在已发现的 AE 安装目录内。")); return 2; }

            var item = new InstalledItem
            {
                FullPath = full,
                Name = Path.GetFileName(full.TrimEnd('\\')),
                IsBundle = Directory.Exists(full),
                Size = Directory.Exists(full) ? Fs.DirSize(full) : new FileInfo(full).Length
            };
            foreach (var v in versions)
            {
                string r = Path.GetFullPath(v.Root).TrimEnd('\\') + "\\";
                if (string.Equals(Path.GetDirectoryName(full).TrimEnd('\\') + "\\", r, StringComparison.OrdinalIgnoreCase))
                { item.Ae = v; break; }
            }
            if (item.Ae == null)
                foreach (var v in versions)
                    if (full.StartsWith(Path.GetFullPath(v.Root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) { item.Ae = v; break; }
            if (item.Ae == null) item.Ae = versions[0];

            var result = exe.Uninstall(new List<InstalledItem> { item });
            var sb = new StringBuilder();
            sb.AppendLine(L.T("卸载：") + full);
            sb.AppendLine(L.T("结果：") + result.Message);
            sb.AppendLine(L.T("数据目录：") + exe.DataDir);
            WriteReport(sb.ToString());
            return result.Failed == 0 ? 0 : 4;
        }

        private static int DetectCommand(string path, string versionFilter)
        {
            var versions = AeEnv.Discover();
            if (versions.Count == 0) { WriteReport(L.T("没有找到 AE 安装")); return 2; }

            AeVersion ae = versions[0];
            if (!string.IsNullOrEmpty(versionFilter))
            {
                ae = null;
                foreach (var v in versions) if (v.Version == versionFilter) { ae = v; break; }
                if (ae == null) { WriteReport(L.T("没有找到 AE ") + versionFilter); return 2; }
            }
            var plan = Installer.Detect(path, ae, false);

            var sb = new StringBuilder();
            sb.AppendLine(L.T("检测对象：") + path);
            sb.AppendLine(L.T("目标版本：AE ") + ae.Version + "（" + ae.Root + "）");
            sb.AppendLine(L.T("包名：") + plan.PackageName);
            sb.AppendLine();
            if (plan.ManualReason != null)
            {
                sb.AppendLine(L.T("【需要人工安装】") + plan.ManualReason);
                sb.AppendLine(plan.ManualHint);
            }
            else
            {
                sb.AppendLine(L.T("【安装方案】共 ") + plan.Actions.Count + L.T(" 个文件，") + Fs.SizeText(plan.TotalBytes));
                foreach (var a in plan.Actions)
                    sb.AppendLine(string.Format("  [{0}] {1}{2}{3}",
                        Kinds.Label(a.Kind),
                        a.TargetPath,
                        a.TargetExists ? L.T("   ← 覆盖现有文件") : "",
                        a.ExistingArch != null && a.ExistingArch.Length > 0 ? L.T("（现有为") + Kinds.ArchLabel(a.ExistingArch) + "）" : ""));
            }
            sb.AppendLine();
            sb.AppendLine(L.T("【说明】"));
            foreach (var n in plan.Notes) sb.AppendLine("  - " + n);

            try { if (plan.TempDir != null) Installer.CleanTemp(); } catch { }
            WriteReport(sb.ToString());
            return plan.ManualReason == null && plan.Actions.Count > 0 ? 0 : 3;
        }

        /// <summary>把报告写到 UTF-8 文件，避免控制台编码问题。</summary>
        private static void WriteReport(string text)
        {
            string file = Path.Combine(Path.GetTempPath(), "AEPluginManager-report.txt");
            try
            {
                File.WriteAllText(file, text, new UTF8Encoding(false));
                Console.WriteLine("report: " + file);
            }
            catch (Exception ex) { Console.WriteLine(L.T("写入报告失败: ") + ex.Message); }
        }
    }
}
