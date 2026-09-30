using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AePluginManager
{
    internal static class Fs
    {
        public static void EnsureDir(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        /// <summary>复制文件，必要时创建父目录。</summary>
        public static void Copy(string src, string dst)
        {
            EnsureDir(Path.GetDirectoryName(dst));
            File.Copy(src, dst, true);
        }

        /// <summary>复制整个目录（含子目录）。</summary>
        public static void CopyDir(string src, string dst)
        {
            EnsureDir(dst);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        public static long DirSize(string dir)
        {
            long total = 0;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        public static string SizeText(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            return bytes + " B";
        }

        /// <summary>递归删除目录，忽略失败（只用于本程序自己的临时目录）。</summary>
        public static void TryDeleteDir(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            if (!IsInsideTemp(dir)) return; // 安全闸：只删临时目录
            try { Directory.Delete(dir, true); } catch { }
        }

        private static bool IsInsideTemp(string dir)
        {
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
            string full = Path.GetFullPath(dir).TrimEnd('\\');
            return full.StartsWith(temp + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>删除空目录（自底向上，只删空的，不删文件）。</summary>
        public static void PruneEmptyDirs(string dir, string stopAt)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var sub in Directory.GetDirectories(dir)) PruneEmptyDirs(sub, stopAt);
            try
            {
                if (Directory.GetFileSystemEntries(dir).Length == 0 &&
                    !string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(stopAt).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(dir, false);
            }
            catch { }
        }

        /// <summary>读取 PE 头，返回 x64 / x86 / ARM / ""（未知）。</summary>
        public static string PeArch(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 0x40) return "";
                    fs.Position = 0x3C;
                    int pe = br.ReadInt32();
                    if (pe <= 0 || pe + 6 > fs.Length) return "";
                    fs.Position = pe;
                    if (br.ReadUInt32() != 0x00004550) return "";
                    ushort machine = br.ReadUInt16();
                    if (machine == 0x8664) return "x64";
                    if (machine == 0x014c) return "x86";
                    if (machine == 0x01c0 || machine == 0x01c4 || machine == 0xAA64) return "ARM";
                    return "other";
                }
            }
            catch { return ""; }
        }

        /// <summary>从文本里抓出的版本号（如 v1.5.5 / 1.0.40）。找不到返回 ""。</summary>
        public static string VersionIn(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(text, @"[vV]?(\d+\.\d+(?:\.\d+)*)");
            return m.Success ? m.Value : "";
        }

        public static string Elide(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        /// <summary>把 Windows 路径转成适合放进回收区目录名的形式。</summary>
        public static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }

    /// <summary>会同时写内存（供界面展示）与磁盘日志文件。</summary>
    internal sealed class Logger
    {
        private readonly List<string> _lines = new List<string>();
        private readonly string _file;

        public Logger(string file)
        {
            _file = file;
            try { Fs.EnsureDir(Path.GetDirectoryName(_file)); } catch { }
        }

        public void Write(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            _lines.Add(line);
            try { File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        public string Text { get { return string.Join(Environment.NewLine, _lines.ToArray()); } }

        public IList<string> Lines { get { return _lines; } }
    }
}
