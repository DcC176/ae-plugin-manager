using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace AePluginManager
{
    internal sealed class MoveRecord
    {
        public string Source;        // 原始位置（安装时为被覆盖的文件；卸载时为被移除的文件/目录）
        public string Existing;      // 安装时=true 表示目标原本已存在
        public string Backup;        // 备份相对路径（安装：备份目录；卸载：回收区）
        public string Note;
        public string Archived;      // 目录条目：移动前记录的条目大小

        public string Type
        {
            get { return Existing == "true" ? L.T("覆盖") : L.T("新增"); }
        }
    }

    internal sealed class Session
    {
        public string Id;
        public DateTime Time;
        public string Op;            // install | uninstall
        public string Description;
        public List<MoveRecord> Records = new List<MoveRecord>();

        public string OpLabel { get { return Op == "install" ? L.T("安装") : L.T("卸载"); } }

        public string TimeText { get { return Time.ToString("yyyy-MM-dd HH:mm"); } }

        public override string ToString()
        {
            return string.Format(L.T("[{0}] {1} · {2} 个文件 · {3}"), OpLabel, TimeText, Records.Count, Description);
        }
    }

    internal sealed class OpResult
    {
        public int Done;
        public int Failed;
        public bool RolledBack;
        public string Message = "";
        public List<string> TouchedDirs = new List<string>();
    }

    /// <summary>
    /// 所有写入操作。安全约束：
    /// 1) 目标目录只允许位于已发现的 AE 安装目录内（防路径穿越）；
    /// 2) 覆盖前先备份，卸载是"移动到回收区"，AE 目录内永不做不可逆删除。
    /// </summary>
    internal sealed class Executor
    {
        public static readonly string RootFolderName = L.T("AE插件管理器");

        public string DataDir;
        public string BackupDir;
        public string RecycleDir;
        public string SessionsFile;
        public Logger Log;

        private readonly List<AeVersion> _versions;

        public Executor(List<AeVersion> versions, string customDataDir)
        {
            _versions = versions;
            DataDir = string.IsNullOrEmpty(customDataDir) ? DefaultDataDir() : customDataDir;
            BackupDir = Path.Combine(DataDir, "backup");
            RecycleDir = Path.Combine(DataDir, "recycle");
            SessionsFile = Path.Combine(DataDir, "sessions.tsv");
            Fs.EnsureDir(BackupDir);
            Fs.EnsureDir(RecycleDir);
            Log = new Logger(Path.Combine(DataDir, "log.txt"));
        }

        public static string DefaultDataDir()
        {
            string drive = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
            try
            {
                if (DriveInfo.GetDrives().Length > 0)
                {
                    // 优先跟 AE 同一个盘，保证备份与 AE 在同盘、便于管理
                    var found = AeEnv.Discover();
                    if (found.Count > 0) drive = Path.GetPathRoot(found[0].Root);
                }
            }
            catch { }
            return Path.Combine(drive, RootFolderName);
        }

        // ==================== 安装 ====================

        public OpResult Apply(InstallPlan plan, AeVersion ae)
        {
            var result = new OpResult();
            if (plan.Actions.Count == 0) { result.Message = L.T("没有需要复制的文件"); return result; }

            string id = "I" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backupRoot = Path.Combine(BackupDir, id);
            var session = new Session
            {
                Id = id,
                Time = DateTime.Now,
                Op = "install",
                Description = plan.PackageName + " → AE " + ae.Version
            };

            Log.Write(string.Format(L.T("开始安装 {0} 到 AE {1}，共 {2} 个文件"),
                plan.PackageName, ae.Version, plan.Actions.Count));

            int index = 0;
            foreach (var a in plan.Actions)
            {
                index++;
                string target = a.TargetPath;
                if (!IsAllowedTarget(target))
                {
                    result.Failed++;
                    Log.Write(L.T("拒绝写入 AE 目录之外的目标：") + target);
                    continue;
                }

                var rec = new MoveRecord { Source = target, Existing = File.Exists(target) ? "true" : "false" };
                try
                {
                    if (rec.Existing == "true")
                    {
                        string bak = Path.Combine(backupRoot, "old", Fs.Sanitize(index.ToString("000") + "_" + Path.GetFileName(target)));
                        Fs.Copy(target, bak);
                        rec.Backup = RelTo(DataDir, bak);
                    }
                    Fs.Copy(a.SourcePath, target);
                    if (rec.Existing != "true")
                    {
                        string bak = Path.Combine(backupRoot, "new", Fs.Sanitize(index.ToString("000") + "_" + Path.GetFileName(target)));
                        Fs.EnsureDir(Path.GetDirectoryName(bak));
                        File.WriteAllText(bak, target, Encoding.UTF8);   // 记录"新增"清单，供回滚/卸载
                        rec.Backup = RelTo(DataDir, bak);
                    }
                    session.Records.Add(rec);
                    result.Done++;
                    AddDir(result.TouchedDirs, Path.GetDirectoryName(target));
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    Log.Write(L.T("写入失败：") + target + " → " + ex.Message);
                }
            }

            if (result.Failed > 0 && result.Done > 0)
            {
                RollbackInstall(session);
                result.RolledBack = true;
                result.Message = string.Format(L.T("有 {0} 个文件写入失败（常见原因：AE 正在运行占用文件、或没有管理员权限），已把本次改动全部还原。"),
                    result.Failed);
                Log.Write(L.T("安装失败并已回滚：") + plan.PackageName);
                return result;
            }

            if (session.Records.Count > 0) AppendSession(session);

            result.Message = string.Format(L.T("安装完成：{0} 个文件（{1}）已写入 AE {2}{3}"),
                result.Done, Fs.SizeText(plan.TotalBytes), ae.Version,
                result.Failed > 0 ? "；" + result.Failed + L.T(" 个失败") : "");
            Log.Write(result.Message);
            return result;
        }

        /// <summary>把本次安装已写入的内容全部撤销（失败回滚，用户不可见）。</summary>
        private void RollbackInstall(Session session)
        {
            for (int i = session.Records.Count - 1; i >= 0; i--)
            {
                var rec = session.Records[i];
                try
                {
                    if (rec.Existing == "true")
                    {
                        string bak = Path.Combine(DataDir, rec.Backup);
                        if (File.Exists(bak)) Fs.Copy(bak, rec.Source);
                    }
                    else if (File.Exists(rec.Source))
                    {
                        File.Delete(rec.Source);
                    }
                }
                catch (Exception ex) { Log.Write(L.T("回滚失败：") + rec.Source + " → " + ex.Message); }
            }
            session.Records.Clear();
        }

        // ==================== 卸载（移入回收区） ====================

        public OpResult Uninstall(List<InstalledItem> items)
        {
            var result = new OpResult();
            if (items.Count == 0) { result.Message = L.T("没有选中任何条目"); return result; }

            string id = "U" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string bin = Path.Combine(RecycleDir, id);
            var session = new Session
            {
                Id = id,
                Time = DateTime.Now,
                Op = "uninstall",
                Description = Summarize(items)
            };

            int index = 0;
            foreach (var it in items)
            {
                index++;
                if (!IsAllowedTarget(it.FullPath))
                {
                    result.Failed++;
                    Log.Write(L.T("拒绝操作 AE 目录之外的条目：") + it.FullPath);
                    continue;
                }

                try
                {
                    string bak = Path.Combine(bin, Fs.Sanitize(index.ToString("000") + "_" + it.Name));
                    Fs.EnsureDir(Path.GetDirectoryName(bak));
                    if (Directory.Exists(it.FullPath))
                    {
                        Fs.CopyDir(it.FullPath, bak);
                        Directory.Delete(it.FullPath, true);
                    }
                    else if (File.Exists(it.FullPath))
                    {
                        File.Copy(it.FullPath, bak, true);
                        File.Delete(it.FullPath);
                    }
                    else continue;

                    session.Records.Add(new MoveRecord
                    {
                        Source = it.FullPath,
                        Existing = "true",
                        Backup = RelTo(DataDir, bak),
                        Note = it.Name,
                        Archived = it.IsBundle ? "dir" : ""
                    });
                    result.Done++;
                    AddDir(result.TouchedDirs, Path.GetDirectoryName(it.FullPath));
                    Log.Write(L.T("移入回收区：") + it.FullPath);
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    Log.Write(L.T("卸载失败：") + it.FullPath + " → " + ex.Message);
                }
            }

            if (session.Records.Count > 0)
            {
                AppendSession(session);
                foreach (var r in session.Records)
                    if (r.Archived != "dir") Fs.PruneEmptyDirs(Path.GetDirectoryName(r.Source), Path.GetPathRoot(r.Source));
            }

            result.Message = string.Format(L.T("已移入回收区：{0} 个条目{1}（可随时一键还原）"),
                result.Done, result.Failed > 0 ? "，" + result.Failed + L.T(" 个失败") : "");
            Log.Write(result.Message);
            return result;
        }

        // ==================== 还原 / 彻底删除 ====================

        public OpResult Restore(Session session)
        {
            var result = new OpResult();
            int index = 0;
            foreach (var rec in session.Records)
            {
                index++;
                try
                {
                    string from = Path.Combine(DataDir, rec.Backup);
                    if (session.Op == "install")
                    {
                        // 安装会话：已有文件用备份覆盖回去；新增文件直接删掉
                        if (rec.Existing == "true" && File.Exists(from)) Fs.Copy(from, rec.Source);
                        else if (rec.Existing != "true" && File.Exists(rec.Source)) File.Delete(rec.Source);
                    }
                    else
                    {
                        // 卸载会话：从回收区搬回原位置
                        Fs.EnsureDir(Path.GetDirectoryName(rec.Source));
                        if (Directory.Exists(from)) { Fs.CopyDir(from, rec.Source); Directory.Delete(from, true); }
                        else if (File.Exists(from)) { File.Copy(from, rec.Source, true); File.Delete(from); }
                        else continue;
                    }
                    result.Done++;
                    AddDir(result.TouchedDirs, Path.GetDirectoryName(rec.Source));
                    Log.Write(L.T("已还原：") + rec.Source);
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    Log.Write(L.T("还原失败：") + rec.Source + " → " + ex.Message);
                }
            }
            result.Message = string.Format(L.T("已还原 {0} 个条目{1}"), result.Done, result.Failed > 0 ? "，" + result.Failed + L.T(" 个失败") : "");
            return result;
        }

        /// <summary>彻底删除某个会话对应的备份/回收内容（只删本软件备份区内的数据）。</summary>
        public OpResult Purge(Session session)
        {
            var result = new OpResult();
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in session.Records)
            {
                if (string.IsNullOrEmpty(rec.Backup)) continue;
                string full = Path.Combine(DataDir, rec.Backup);
                string top = TopLevelUnder(session.Op == "install" ? BackupDir : RecycleDir, full);
                if (top != null) dirs.Add(top);
            }
            foreach (var d in dirs)
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, true); result.Done++; }
                catch (Exception ex) { result.Failed++; Log.Write(L.T("彻底删除失败：") + d + " → " + ex.Message); }
            }
            RemoveSession(session);
            result.Message = L.T("已彻底删除 ") + result.Done + L.T(" 项备份数据");
            return result;
        }

        // ==================== 会话清单 ====================

        /// <summary>启动时清理已不存在的会话目录（用户手动删过备份时保持清单一致）。</summary>
        public void CleanupSessions()
        {
            var sessions = LoadSessions();
            var keep = new List<Session>();
            bool changed = false;
            foreach (var s in sessions)
            {
                bool alive = false;
                foreach (var r in s.Records)
                {
                    if (string.IsNullOrEmpty(r.Backup)) continue;
                    string full = Path.Combine(DataDir, r.Backup);
                    if (File.Exists(full) || Directory.Exists(full) || File.Exists(full)) { alive = true; break; }
                }
                if (alive || s.Op == "install") keep.Add(s);
                else changed = true;
            }
            if (changed) SaveSessions(keep);
        }

        public List<Session> LoadSessions()
        {
            var list = new List<Session>();
            if (!File.Exists(SessionsFile)) return list;
            try
            {
                foreach (var line in File.ReadAllLines(SessionsFile, Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var f = line.Split('\t');
                    var s = new Session { Id = f[0], Op = f[2], Description = f[4] };
                    DateTime t;
                    if (DateTime.TryParseExact(f[1], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) s.Time = t;
                    if (f.Length > 5)
                    {
                        // 记录编码：路径|已存在|备份相对路径|备注|类型
                        foreach (var r in f[5].Split(';'))
                        {
                            if (r.Length == 0) continue;
                            var p = r.Split('|');
                            if (p.Length < 3) continue;
                            s.Records.Add(new MoveRecord { Source = p[0], Existing = p[1], Backup = p[2], Note = p.Length > 3 ? p[3] : "", Archived = p.Length > 4 ? p[4] : "" });
                        }
                    }
                    list.Add(s);
                }
            }
            catch (Exception ex) { Log.Write(L.T("读取会话清单失败：") + ex.Message); }
            return list;
        }

        private void AppendSession(Session s)
        {
            try
            {
                File.AppendAllText(SessionsFile, Serialize(s) + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) { Log.Write(L.T("写入会话清单失败：") + ex.Message); }
        }

        private void SaveSessions(List<Session> sessions)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# id\ttime\top\tdescription\trecords");
                foreach (var s in sessions) sb.AppendLine(Serialize(s));
                File.WriteAllText(SessionsFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Write(L.T("保存会话清单失败：") + ex.Message); }
        }

        private void RemoveSession(Session target)
        {
            var all = LoadSessions();
            var keep = new List<Session>();
            foreach (var s in all) if (s.Id != target.Id) keep.Add(s);
            SaveSessions(keep);
        }

        private static string Serialize(Session s)
        {
            var parts = new List<string>();
            foreach (var r in s.Records)
            {
                parts.Add(string.Join("|", new[]
                {
                    r.Source.Replace('\t', ' '),
                    r.Existing ?? "false",
                    r.Backup ?? "",
                    (r.Note ?? "").Replace('\t', ' '),
                    r.Archived ?? ""
                }));
            }
            return string.Join("\t", new[]
            {
                s.Id,
                s.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                s.Op,
                s.Description.Replace('\t', ' ').Replace('\n', ' '),
                s.Records.Count.ToString(CultureInfo.InvariantCulture),
                string.Join(";", parts.ToArray())
            });
        }

        // ==================== 安全阀 ====================

        /// <summary>只允许写入已发现的 AE 安装目录、共享插件目录，以及本软件自己的数据目录。</summary>
        public bool IsAllowedTarget(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); } catch { return false; }

            foreach (var root in AllowedRoots())
            {
                if (string.IsNullOrEmpty(root)) continue;
                string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private IEnumerable<string> AllowedRoots()
        {
            foreach (var v in _versions) yield return v.Root;
            foreach (var d in AeEnv.SharedPluginDirs()) yield return d;
            yield return BackupDir;
            yield return RecycleDir;
        }

        // ==================== 辅助 ====================

        public void OpenFolder(string path)
        {
            try
            {
                string dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { Log.Write(L.T("打开目录失败：") + ex.Message); }
        }

        public void OpenPath(string path)
        {
            try { Process.Start("explorer.exe", "\"" + path + "\""); }
            catch (Exception ex) { Log.Write(L.T("打开失败：") + ex.Message); }
        }

        private static void AddDir(List<string> dirs, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            foreach (var d in dirs) if (string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)) return;
            dirs.Add(dir);
        }

        private static string RelTo(string root, string path)
        {
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string p = Path.GetFullPath(path);
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? p.Substring(r.Length) : p;
        }

        private static string TopLevelUnder(string root, string path)
        {
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string p = Path.GetFullPath(path);
            if (!p.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return null;
            string rest = p.Substring(r.Length);
            int idx = rest.IndexOf('\\');
            return Path.Combine(root, idx < 0 ? rest : rest.Substring(0, idx));
        }

        private static string Summarize(List<InstalledItem> items)
        {
            if (items.Count == 1) return items[0].Name;
            return items[0].Name + L.T(" 等 ") + items.Count + L.T(" 项");
        }
    }
}
