using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace AePluginManager
{
    /// <summary>发现本机的 AE 安装，以及需要检测的共享插件目录。</summary>
    internal static class AeEnv
    {
        /// <summary>搜索 AE 安装位置的父目录列表。</summary>
        private static readonly string[] Roots =
        {
            @"E:\Adobe",
            @"D:\Adobe",
            @"C:\Program Files\Adobe",
            @"C:\Program Files (x86)\Adobe",
            @"F:\Adobe"
        };

        public static List<AeVersion> Discover()
        {
            var found = new List<AeVersion>();

            foreach (var root in Roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root, "Adobe After Effects*"))
                {
                    var v = FromFolder(dir);
                    if (v != null) AddUnique(found, v);
                }
            }

            // 注册表登记过的安装路径（覆盖非默认安装位置）
            foreach (var key in new[] { @"SOFTWARE\Adobe\After Effects", @"SOFTWARE\WOW6432Node\Adobe\After Effects" })
            {
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(key))
                    {
                        if (k == null) continue;
                        foreach (var sub in k.GetSubKeyNames())
                        {
                            if (sub.Length == 0 || !char.IsDigit(sub[0])) continue;   // 跳过 "Common Files" 等非版本子键
                            using (var vk = k.OpenSubKey(sub))
                            {
                                if (vk == null) continue;
                                var path = vk.GetValue("InstallPath") as string;
                                if (string.IsNullOrEmpty(path)) continue;
                                var v = FromFolder(path.TrimEnd('\\'));
                                if (v != null) AddUnique(found, v);
                            }
                        }
                    }
                }
                catch { }
            }

            found.Sort((a, b) => string.CompareOrdinal(b.Version, a.Version));
            return found;
        }

        /// <summary>与本机 AE 无关、但会被 AE 加载的共享插件目录（只读检测）。</summary>
        public static List<string> SharedPluginDirs()
        {
            var list = new List<string>();
            string common = @"C:\Program Files\Adobe\Common\Plug-ins";
            if (Directory.Exists(common))
            {
                foreach (var sub in Directory.GetDirectories(common))
                    list.Add(sub);
            }
            string vc = @"E:\Adobe\VideoCopilot";
            if (Directory.Exists(vc)) list.Add(vc);
            return list;
        }

        private static AeVersion FromFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
            folder = folder.TrimEnd('\\');

            // 注册表里登记的是 "...\Support Files" 这一层，向上找到版本根目录
            if (Path.GetFileName(folder).Equals("Support Files", StringComparison.OrdinalIgnoreCase))
            {
                string parent = Path.GetDirectoryName(folder);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) folder = parent;
            }

            string name = Path.GetFileName(folder);
            string version = name.Replace("Adobe After Effects", "").Trim();
            if (version.Length == 0 || !char.IsDigit(version[0])) return null;

            string support = Path.Combine(folder, "Support Files");
            if (!Directory.Exists(support)) support = folder;
            if (!Directory.Exists(Path.Combine(support, "Plug-ins"))) return null;

            return new AeVersion { Version = version, Root = folder, SupportFiles = support };
        }

        private static void AddUnique(List<AeVersion> list, AeVersion v)
        {
            foreach (var e in list)
                if (string.Equals(e.Root.TrimEnd('\\'), v.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            list.Add(v);
        }

        /// <summary>AE 主程序是否正在运行（正在运行时不建议安装，插件文件會被占用）。</summary>
        public static bool IsAeRunning()
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    string n = null;
                    try { n = p.ProcessName; } catch { }
                    if (n != null && n.IndexOf("AfterFX", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            catch { }
            return false;
        }
    }
}
