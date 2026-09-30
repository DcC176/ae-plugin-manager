using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AePluginManager
{
    /// <summary>AE 插件分类。target 为相对 AE 版本根目录（Support Files）的安装位置。</summary>
    internal enum PluginKind
    {
        Effect,    // .aex 特效插件
        Script,    // .jsx / .jsxbin 脚本面板
        Preset,    // .ffx 预设
        MediaCore, // Media Core 编解码插件
        Extension, // CEP 扩展
        Other
    }

    internal static class Kinds
    {
        /// <summary>可安装载荷的扩展名 -> 分类。</summary>
        public static PluginKind Classify(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".aex":
                case ".8bf":
                    return PluginKind.Effect;
                case ".jsx":
                case ".jsxbin":
                    return PluginKind.Script;
                case ".ffx":
                case ".prf":
                    return PluginKind.Preset;
                case ".plugin":
                    return PluginKind.MediaCore;
                default:
                    return PluginKind.Other;
            }
        }

        public static bool IsPayload(string ext)
        {
            return Classify(ext) != PluginKind.Other;
        }

        /// <summary>分类对应的安装根目录（相对 Support Files）。</summary>
        public static string TargetFolder(PluginKind kind)
        {
            switch (kind)
            {
                case PluginKind.Effect: return @"Plug-ins";
                case PluginKind.Script: return @"Scripts\ScriptUI Panels";
                case PluginKind.Preset: return @"Presets";
                case PluginKind.MediaCore: return @"(Media Core plug-ins)";
                default: return @"Plug-ins";
            }
        }

        public static string Label(PluginKind kind)
        {
            switch (kind)
            {
                case PluginKind.Effect: return L.T("特效插件");
                case PluginKind.Script: return L.T("脚本");
                case PluginKind.Preset: return L.T("预设");
                case PluginKind.MediaCore: return L.T("编解码插件");
                case PluginKind.Extension: return L.T("扩展");
                default: return L.T("其他");
            }
        }

        public static string ArchLabel(string arch)
        {
            if (arch == "x64") return L.T("64 位");
            if (arch == "x86") return L.T("32 位");
            if (string.IsNullOrEmpty(arch)) return L.T("未知");
            return arch;
        }
    }

    /// <summary>一个 AE 安装实例。</summary>
    internal sealed class AeVersion
    {
        public string Version;      // 例如 "2025"
        public string Root;         // ...\Adobe After Effects 2025
        public string SupportFiles; // ...\Support Files（2020 之前无此层时等于 Root）

        public string PathOf(PluginKind kind)
        {
            return Path.Combine(SupportFiles, Kinds.TargetFolder(kind));
        }

        public string PluginsDir { get { return Path.Combine(SupportFiles, "Plug-ins"); } }
        public string ScriptsDir { get { return Path.Combine(SupportFiles, "Scripts"); } }
        public string PresetsDir { get { return Path.Combine(SupportFiles, "Presets"); } }
        public string ExtensionsDir { get { return Path.Combine(SupportFiles, @"Plug-ins\Extensions"); } }

        public override string ToString() { return "AE " + Version; }
    }

    /// <summary>已安装条目（扫描结果）。</summary>
    internal sealed class InstalledItem
    {
        public AeVersion Ae;
        public string FullPath;
        public string RelPath;     // 相对版本根目录
        public string Name;        // 文件名或扩展包目录名
        public PluginKind Kind;
        public long Size;
        public string Arch;        // x64 / x86 / ""
        public bool IsBundle;      // 目录形式的扩展包
        public DateTime Modified;
    }

    internal enum ConflictLevel { High, Medium, Low }

    /// <summary>冲突的修复动作类型。</summary>
    internal enum RepairKind
    {
        None,          // 仅提示，无可执行动作
        MoveToRecycle, // 把多余/错位/残留的文件移入回收区（可还原）
        CloseAe        // 需要用户先关闭 AE
    }

    internal sealed class Conflict
    {
        public string Rule;
        public ConflictLevel Level;
        public string Title;
        public string Detail;
        public List<InstalledItem> Items = new List<InstalledItem>();
        /// <summary>建议动作（仅描述，不自动执行）。</summary>
        public string Advice;

        // ---- 一键修复 ----
        public RepairKind Repair = RepairKind.None;
        /// <summary>按钮文案，例如"移入回收区（保留最新版）"。</summary>
        public string RepairLabel = "";
        /// <summary>修复动作的说明，展示在详情区。</summary>
        public string RepairDetail = "";
        /// <summary>要移入回收区的条目（RepairKind.MoveToRecycle 时有效）。</summary>
        public List<InstalledItem> RepairItems = new List<InstalledItem>();
        /// <summary>需要用户先选择保留哪一份（同版本多副本时，由用户自行抉择）。</summary>
        public bool RepairNeedsPick;

        public bool CanRepair { get { return Repair != RepairKind.None && (RepairItems.Count > 0 || Repair == RepairKind.CloseAe || RepairNeedsPick); } }
    }

    /// <summary>安装方案中的单个动作。</summary>
    internal sealed class PlanAction
    {
        public string SourcePath;
        public string TargetPath;
        public PluginKind Kind;
        public long Size;
        public bool TargetExists;      // 目标已存在 -> 需要备份
        public string ExistingArch;    // 已存在文件位数（用于提示覆盖风险）
    }

    /// <summary>可安装的一个版本候选（中文版 / 英文版 / Mac 版 / 直接安装）。</summary>
    internal sealed class VariantOption
    {
        public string Root;
        public string Display = "";
        public string FallbackLabel = "";
        public int FileCount;
        public long Bytes;
        public string KindText = "";
        public bool MacOnly;
        public bool Preferred;   // 自动推荐的默认项
        internal int Score;      // 语言/平台评分，仅排序用
    }

    /// <summary>解压目录里的一个文件（安装识别的中间产物）。</summary>
    internal sealed class Entry
    {
        public string Full;
        public string Rel;   // 相对扫描根
        public long Size;
    }

    /// <summary>一次"拖入识别"的结果：解压一次，之后可在候选间切换而不必重新解压。</summary>
    internal sealed class DetectSession
    {
        public string SourcePath = "";
        public string WorkDir;
        public bool Scratch;                    // WorkDir 是本程序创建的临时目录，可清理
        public string PayloadRoot = "";
        public int DefaultChoice;
        public int JunkCount;

        public List<Entry> Payload = new List<Entry>();
        public List<Entry> Docs = new List<Entry>();
        public List<VariantOption> Choices = new List<VariantOption>();
        public List<InstallerInfo> Installers = new List<InstallerInfo>();
        public List<string> Notes = new List<string>();

        public string ManualReason;
        public string ManualHint;
        public string ManualDir;

        public bool NeedChoice { get { return Choices.Count > 1; } }
    }

    /// <summary>包内找到的安装程序（只有安装器的插件包走这条路）。</summary>
    internal sealed class InstallerInfo
    {
        public string FullPath;
        public string RelativePath = "";   // 解压目录内的相对位置，便于展示
        public string Kind = "";           // exe / msi / dmg / pkg
        public string Arch = "";           // x64 / x86 / ARM / ""
        public long Size;
        public bool Chinese;
        public bool Preferred;
        public bool NotExtracted;          // 体积过大没有解压出来（需要手动处理）

        public string Display
        {
            get
            {
                var sb = new StringBuilder();
                if (Preferred) sb.Append("★ ");
                sb.Append(System.IO.Path.GetFileName(FullPath));
                if (Arch.Length > 0) sb.Append("　[").Append(Kinds.ArchLabel(Arch)).Append(']');
                if (Chinese) sb.Append(L.T("　中文版"));
                sb.Append("　").Append(Fs.SizeText(Size));
                if (NotExtracted) sb.Append(L.T("　（体积过大，未解压）"));
                if (RelativePath.Length > 0) sb.Append("　·　").Append(RelativePath);
                return sb.ToString();
            }
        }
    }

    internal sealed class InstallPlan
    {
        public string PackageName = "";
        public string SourcePath = "";
        public int DetectedByVersion;  // 放在哪个 AE 版本上生成
        public List<PlanAction> Actions = new List<PlanAction>();
        public List<string> Notes = new List<string>();
        public List<string> Skipped = new List<string>();          // 跳过项的文字描述（给用户看）
        public string ManualReason;    // 非空表示需要人工安装
        public string ManualHint;
        public string TempDir;         // 解压出的临时目录（执行后可清理）
        public bool Scratch;           // TempDir 是否为本程序创建的临时目录
        /// <summary>包内的安装程序（只含安装器的包用这条路径：由软件拉起向导，用户点几下装完）。</summary>
        public List<InstallerInfo> Installers = new List<InstallerInfo>();
        /// <summary>因体积过大未解压的安装器（文件名 + 原始大小）。</summary>
        public List<KeyValuePair<string, long>> SkippedInstallers = new List<KeyValuePair<string, long>>();

        public long TotalBytes
        {
            get { long t = 0; foreach (var a in Actions) t += a.Size; return t; }
        }

        /// <summary>复制一份方案（安装到多个 AE 版本时，各版本独立生成目标路径）。</summary>
        public InstallPlan Clone()
        {
            var p = new InstallPlan
            {
                PackageName = PackageName,
                SourcePath = SourcePath,
                DetectedByVersion = DetectedByVersion,
                ManualReason = ManualReason,
                ManualHint = ManualHint,
                TempDir = TempDir
            };
            foreach (var a in Actions)
                p.Actions.Add(new PlanAction
                {
                    SourcePath = a.SourcePath,
                    TargetPath = a.TargetPath,
                    Kind = a.Kind,
                    Size = a.Size,
                    TargetExists = a.TargetExists,
                    ExistingArch = a.ExistingArch
                });
            p.Notes.AddRange(Notes);
            p.Skipped.AddRange(Skipped);
            p.Installers.AddRange(Installers);
            return p;
        }
    }
}
