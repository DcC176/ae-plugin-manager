using System;
using System.Collections.Generic;
using System.IO;

namespace AePluginManager
{
    /// <summary>只读扫描已安装的插件。不修改任何文件。</summary>
    internal static class Scanner
    {
        /// <summary>Adobe 自带、无需作为第三方插件呈现的子目录。</summary>
        private static readonly HashSet<string> AdobeOwnedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Effects", "Format", "DataFormat", "Keyframe", "Extensions",
            "(AdobePSL)", "Cineware by Maxon", "MAXON CINEWARE AE"
        };

        /// <summary>跳过的脚本子目录：Adobe 自带资源与可选安装项。</summary>
        private static readonly HashSet<string> SkipScriptDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "(instructional)", "(support)", "Shutdown", "Startup"
        };

        private const int MaxDepth = 3;

        /// <summary>Adobe 自带的根级脚本，不作为第三方插件纳管。</summary>
        private static readonly HashSet<string> AdobeBuiltinScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Change Render Locations.jsx", "Convert Selected Properties to Markers.jsx",
            "Demo Palette.jsx", "Double-Up.jsx", "Find and Replace Text.jsx",
            "Particular Migrator.jsx", "Render and Email.jsx", "Scale Composition.jsx",
            "Scale Selected Layers.jsx", "Smart Import.jsx", "Sort Layers by In Point.jsx",
            "Update Legacy Expressions.jsx", "UpgradeLegacyBlurs.jsx",
            "Create Nulls From Paths.jsx", "VR Comp Editor.jsx",
            "About the ScriptUI Panels folder.txt", "Why_the_parentheses_in_folder_names.txt"
        };

        /// <summary>Adobe 自带组件所在路径（Mocha、Cineware、各种 bundle、预览等），不参与冲突检测。</summary>
        private static readonly string[] AdobeOwnedMarkers =
        {
            @"\Effects\", @"\Cineware", @"\MAXON CINEWARE", @"\mochaAE", @".bundle\",
            @"\(AdobePSL)\", @"\Extensions\", @"\Presets\", @"\Plug-ins\Format\", @"\Plug-ins\Keyframe\"
        };

        /// <summary>是否属于 Adobe 自带内容（这些文件不应被报告为"插件问题"）。</summary>
        public static bool IsAdobeOwned(string fullPath)
        {
            string p = fullPath;
            foreach (var m in AdobeOwnedMarkers)
                if (p.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            string name = Path.GetFileName(fullPath);
            if (AdobeBuiltinScripts.Contains(name)) return true;
            return false;
        }

        public static List<InstalledItem> Scan(IEnumerable<AeVersion> versions)
        {
            var list = new List<InstalledItem>();
            foreach (var ae in versions)
            {
                ScanPlugins(ae, list);
                ScanScripts(ae, list);
                ScanPresets(ae, list);
            }
            return list;
        }

        // ---------- Plug-ins ----------
        private static void ScanPlugins(AeVersion ae, List<InstalledItem> list)
        {
            string root = ae.PluginsDir;
            if (!Directory.Exists(root)) return;

            // Extensions 目录本身作为一个扩展点单独处理
            Walk(ae, ae.ExtensionsDir, PluginKind.Extension, list, 0, root);

            foreach (var entry in Directory.GetFileSystemEntries(root))
            {
                string name = Path.GetFileName(entry);

                if (Directory.Exists(entry))
                {
                    if (AdobeOwnedDirs.Contains(name)) continue;

                    if (name.EndsWith(".plugin", StringComparison.OrdinalIgnoreCase))
                    {
                        AddBundle(ae, entry, PluginKind.MediaCore, list);
                        continue;
                    }
                    // 第三方插件子目录（Trapcode / VideoCopilot / 自带数据的插件目录）
                    Walk(ae, entry, PluginKind.Effect, list, 0, root);
                    continue;
                }

                string ext = Path.GetExtension(name);
                var kind = Kinds.Classify(ext);
                if (kind == PluginKind.Other)
                {
                    // 与插件同目录的 .dll 依赖或 SDK 文件：作为特效插件记录，便于冲突检测
                    if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) kind = PluginKind.Effect;
                    else continue;
                }
                AddFile(ae, entry, kind, list);
            }
        }

        private static void Walk(AeVersion ae, string dir, PluginKind kind, List<InstalledItem> list, int depth, string stopAt)
        {
            if (depth > MaxDepth) return;
            string name = Path.GetFileName(dir);

            // Media Core 插件常见形态：一个目录里放若干 .aex
            bool isBundleRoot = name.EndsWith(".plugin", StringComparison.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(dir))
            {
                string fname = Path.GetFileName(file);
                string ext = Path.GetExtension(fname);

                // Safari/资源分支与残留文件不视为已安装插件
                if (fname.StartsWith("._", StringComparison.Ordinal)) continue;
                if (ext.Equals(".bak", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".old", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                var k = Kinds.Classify(ext);
                if (k == PluginKind.Other)
                {
                    if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) k = PluginKind.Effect;
                    else continue;
                }
                if (isBundleRoot) k = PluginKind.MediaCore;
                AddFile(ae, file, k, list);
            }

            // 扩展包目录（含 .aex 的第三方目录）整体视为一个扩展
            foreach (var sub in Directory.GetDirectories(dir))
            {
                string sname = Path.GetFileName(sub);
                if (sname.EndsWith(".plugin", StringComparison.OrdinalIgnoreCase))
                {
                    AddBundle(ae, sub, PluginKind.MediaCore, list);
                    continue;
                }
                if (kind == PluginKind.Extension && depth == 0 && LooksLikeExtensionBundle(sub))
                {
                    AddBundle(ae, sub, PluginKind.Extension, list);
                    continue;
                }
                if (depth + 1 <= MaxDepth) Walk(ae, sub, kind, list, depth + 1, stopAt);
            }
        }

        private static bool LooksLikeExtensionBundle(string dir)
        {
            string aex = Path.Combine(dir, Path.GetFileName(dir) + ".aex");
            if (File.Exists(aex)) return true;
            // CEP 扩展常见结构：<name>\CSXS\manifest.xml
            return File.Exists(Path.Combine(dir, @"CSXS\manifest.xml"));
        }

        private static void AddBundle(AeVersion ae, string dir, PluginKind kind, List<InstalledItem> list)
        {
            list.Add(new InstalledItem
            {
                Ae = ae,
                FullPath = dir,
                RelPath = Rel(ae, dir),
                Name = Path.GetFileName(dir),
                Kind = kind,
                Size = Fs.DirSize(dir),
                IsBundle = true,
                Modified = SafeTime(dir)
            });
        }

        private static void AddFile(AeVersion ae, string file, PluginKind kind, List<InstalledItem> list)
        {
            var fi = new FileInfo(file);
            list.Add(new InstalledItem
            {
                Ae = ae,
                FullPath = file,
                RelPath = Rel(ae, file),
                Name = fi.Name,
                Kind = kind,
                Size = fi.Length,
                Arch = kind == PluginKind.Effect ? Fs.PeArch(file) : "",
                Modified = fi.LastWriteTime
            });
        }

        // ---------- Scripts ----------
        private static void ScanScripts(AeVersion ae, List<InstalledItem> list)
        {
            string root = ae.ScriptsDir;
            if (!Directory.Exists(root)) return;

            foreach (var file in Directory.GetFiles(root))
                AddScriptFile(ae, file, list);

            foreach (var dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                if (SkipScriptDirs.Contains(name)) continue;

                // ScriptUI Panels：里面的条目才是"用户脚本"（含 Adobe 自带）
                if (name.Equals("ScriptUI Panels", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var f in Directory.GetFiles(dir)) AddScriptFile(ae, f, list);
                    foreach (var d in Directory.GetDirectories(dir))
                        AddBundle(ae, d, PluginKind.Script, list);
                    continue;
                }

                // 第三方脚本目录（整体作为一个包）
                AddBundle(ae, dir, PluginKind.Script, list);
            }
        }

        private static void AddScriptFile(AeVersion ae, string file, List<InstalledItem> list)
        {
            string ext = Path.GetExtension(file);
            if (ext.Equals(".aex", StringComparison.OrdinalIgnoreCase)) return; // 错位的插件文件由冲突检测单独报告
            if (!(ext.Equals(".jsx", StringComparison.OrdinalIgnoreCase) ||
                  ext.Equals(".jsxbin", StringComparison.OrdinalIgnoreCase))) return;
            if (AdobeBuiltinScripts.Contains(Path.GetFileName(file))) return;   // Adobe 自带脚本不纳管
            AddFile(ae, file, PluginKind.Script, list);
        }

        // ---------- Presets ----------
        private static void ScanPresets(AeVersion ae, List<InstalledItem> list)
        {
            string root = ae.PresetsDir;
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.GetFiles(root, "*.ffx"))
                AddFile(ae, file, PluginKind.Preset, list);
        }

        // ---------- 辅助 ----------
        private static string Rel(AeVersion ae, string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetFullPath(ae.Root).TrimEnd('\\') + "\\";
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
            }
            catch { return path; }
        }

        private static DateTime SafeTime(string path)
        {
            try { return Directory.GetLastWriteTime(path); } catch { return DateTime.MinValue; }
        }
    }
}
