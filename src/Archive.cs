using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AePluginManager
{
    /// <summary>
    /// 压缩包解压。zip 用 .NET 原生实现（可逐条筛选，不做无谓的大文件解压）；
    /// rar/7z 优先用 Windows 10 1803+ 自带的 bsdtar（libarchive，支持 rar/7z），
    /// 没有时退回 7-Zip / WinRAR，都没有则给出人工指引。
    /// </summary>
    internal static class Archive
    {
        private const long MaxExtractBytes = 4L * 1024 * 1024 * 1024;
        private const long MaxInstallerBytes = 64L * 1024 * 1024;   // 大于此体积的安装器不解压，只记录

        private static readonly string[] SkipPatterns =
        {
            "*.mp4", "*.avi", "*.mov", "*.mkv", "*.flv", "*.wmv", "*.rmvb", "*.m4v", "*.webm",
            "*.jpg", "*.jpeg", "*.png", "*.gif", "*.bmp", "*.webp", "*.psd",
            "*.zip", "*.rar", "*.7z", "*.url", "*.lnk", "*.dmg", "*.app", "*.mp3"
        };

        // ==================== ZIP（原生） ====================

        public static void ExtractZipFiltered(string zipPath, string destDir, InstallPlan plan)
        {
            // 先探测文件名编码：部分国内打包的 zip 用 GBK 存名字却不设 UTF-8 标志位，
            // 用 UTF-8 解出来是一堆 U+FFFD 替换字符（"中文版" 变成 "???İ?"），必须换编码重解。
            Encoding nameEncoding = Encoding.UTF8;
            bool repaired = false;
            try
            {
                using (var probe = ZipFile.Open(zipPath, ZipArchiveMode.Read, Encoding.UTF8))
                {
                    foreach (var entry in probe.Entries)
                    {
                        if (entry.FullName.IndexOf('\uFFFD') >= 0)
                        {
                            nameEncoding = Encoding.Default;   // 中文系统即 GBK/936
                            repaired = true;
                        }
                        break;
                    }
                }
            }
            catch { }

            long total = 0, skippedBytes = 0;
            int extracted = 0, skipped = 0;

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Read, nameEncoding))
            {
                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('/', '\\');
                    if (name.EndsWith("\\")) continue;                                    // 目录条目
                    if (name.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(name).StartsWith("._", StringComparison.Ordinal))  // mac 资源分支
                    { skipped++; skippedBytes += entry.Length; continue; }

                    if (!WorthExtracting(name, entry.Length))
                    {
                        skipped++;
                        skippedBytes += entry.Length;
                        Remember(plan, entry.FullName, entry.Length);
                        continue;
                    }

                    if (total + entry.Length > MaxExtractBytes)
                    { plan.Notes.Add(L.T("⚠ 压缩包解压量过大，已提前停止")); break; }

                    string target = SafeJoin(destDir, name);
                    if (target == null) { skipped++; continue; }
                    Fs.EnsureDir(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                    total += entry.Length;
                    extracted++;
                }
            }

            plan.Notes.Add(string.Format(L.T("解压 {0} 个文件（{1}），跳过 {2} 个非插件文件（{3}）"),
                extracted, Fs.SizeText(total), skipped, Fs.SizeText(skippedBytes)));
            if (repaired)
                plan.Notes.Add(L.T("压缩包内文件名使用 GBK 编码（未标记 UTF-8），已按系统码页正确解码"));
        }

        /// <summary>
        /// 判断压缩包内某条目是否值得解压：
        /// 载荷与依赖必解；小型文本/配置解压；64 MB 以内的安装器解压（要能运行安装向导）；视频图片等大文件跳过。
        /// </summary>
        private static bool WorthExtracting(string name, long size)
        {
            string ext = Path.GetExtension(name);
            if (Kinds.IsPayload(ext)) return true;
            if (Has(ext, ".exe", ".msi", ".dmg", ".pkg")) return size <= MaxInstallerBytes;
            if (Has(ext, ".dll", ".key", ".dat", ".bin", ".xml", ".json", ".txt", ".md", ".ini")) return size <= 2 * 1024 * 1024;
            return false;
        }

        private static bool Has(string ext, params string[] list)
        {
            foreach (var x in list) if (string.Equals(ext, x, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>记下因过大而没解压的安装器，供"需要人工安装"的提示使用。</summary>
        private static void Remember(InstallPlan plan, string entryName, long size)
        {
            string ext = Path.GetExtension(entryName);
            if (Has(ext, ".exe", ".msi", ".dmg", ".pkg") && size > MaxInstallerBytes)
                plan.SkippedInstallers.Add(new KeyValuePair<string, long>(Path.GetFileName(entryName), size));
        }

        private static string SafeJoin(string root, string relative)
        {
            try
            {
                string full = Path.GetFullPath(Path.Combine(root, relative));
                string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch { return null; }
        }

        // ==================== RAR / 7z ====================

        public static void ExtractWithExternal(string archivePath, string destDir, InstallPlan plan)
        {
            string tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
            if (File.Exists(tar) && RunTar(tar, archivePath, destDir, plan)) return;

            foreach (var tool in new[]
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
                @"C:\Program Files\NanaZip\NanaZipC.exe"
            })
            {
                if (File.Exists(tool) && Run7Zip(tool, archivePath, destDir, plan)) return;
            }

            string winrar = @"C:\Program Files\WinRAR\WinRAR.exe";
            if (File.Exists(winrar) && RunWinRar(winrar, archivePath, destDir, plan)) return;

            if (plan.ManualReason == null)
            {
                plan.ManualReason = L.T("无法解压这个压缩包");
                plan.ManualHint = L.T("系统自带解压组件不支持该压缩包（常见于加密包或损坏包）。\n") +
                                  L.T("建议：用你现有的解压软件（WinRAR / 7-Zip / Bandizip）解压到任意文件夹，\n") +
                                  L.T("然后把这个文件夹直接拖进本软件窗口。");
            }
        }

        private static bool RunTar(string tar, string archive, string dest, InstallPlan plan)
        {
            var args = new StringBuilder("-xf \"").Append(archive).Append("\" -C \"").Append(dest).Append('"');
            foreach (var p in SkipPatterns) args.Append(" --exclude=").Append(p);

            plan.Notes.Add(L.T("使用系统自带解压组件（bsdtar / libarchive）"));
            string err;
            int code = Run(tar, args.ToString(), plan, out err);
            if (code == 0 && HasFiles(dest)) return true;

            if (!string.IsNullOrEmpty(err) && err.IndexOf("passphrase", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                plan.ManualReason = L.T("压缩包已加密，需要密码");
                plan.ManualHint = L.T("本软件不尝试破解或询问压缩包密码。\n") +
                                  L.T("请用解压软件输入密码解压后，把文件夹拖进本软件。");
            }
            return false;
        }

        private static bool Run7Zip(string tool, string archive, string dest, InstallPlan plan)
        {
            var args = new StringBuilder("x -y -bso0 -bsp0 -o\"").Append(dest).Append("\" \"").Append(archive).Append('"');
            foreach (var p in SkipPatterns) args.Append(" -xr!").Append(p);

            plan.Notes.Add(L.T("使用外部解压工具：") + tool);
            string err;
            int code = Run(tool, args.ToString(), plan, out err);
            if (code == 0 && HasFiles(dest)) return true;

            if (code == 2 || (!string.IsNullOrEmpty(err) && err.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                plan.ManualReason = L.T("压缩包已加密，需要密码");
                plan.ManualHint = L.T("请用解压软件输入密码解压后，把文件夹拖进本软件。");
            }
            return false;
        }

        private static bool RunWinRar(string tool, string archive, string dest, InstallPlan plan)
        {
            var args = new StringBuilder("x -ibck -y -o+ \"").Append(archive).Append("\" \"").Append(dest).Append('"');
            foreach (var p in SkipPatterns) args.Append(" -x").Append(p);

            plan.Notes.Add(L.T("使用外部解压工具：") + tool);
            string err;
            int code = Run(tool, args.ToString(), plan, out err);
            if (code == 0 && HasFiles(dest)) return true;
            if (code == 3 || code == 11)
            {
                plan.ManualReason = L.T("压缩包解压失败（WinRAR 返回 ") + code + "）";
                plan.ManualHint = L.T("常见原因：有密码、文件损坏、CRC 校验失败。请手动解压后把文件夹拖进本软件。");
            }
            return false;
        }

        /// <summary>运行外部工具。输出重定向到临时文件，避免中文编码与管道阻塞问题。</summary>
        private static int Run(string exe, string args, InstallPlan plan, out string stderr)
        {
            stderr = "";
            string outFile = Path.Combine(Installer.TempRoot(), "tool.out.txt");
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardInput.Close();   // 加密包询问密码时不阻塞
                    string stdout = p.StandardOutput.ReadToEnd();
                    stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(600000))
                    {
                        try { p.Kill(); } catch { }
                        plan.ManualReason = L.T("解压超时（超过 10 分钟）");
                        plan.ManualHint = L.T("压缩包可能过大或已损坏。请手动解压后把文件夹拖进本软件。");
                        return -1;
                    }
                    try { File.WriteAllText(outFile, stdout + Environment.NewLine + stderr, Encoding.UTF8); } catch { }
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                stderr = ex.Message;
                return -1;
            }
        }

        private static bool HasFiles(string dir)
        {
            try { return Directory.Exists(dir) && Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length > 0; }
            catch { return false; }
        }
    }
}
