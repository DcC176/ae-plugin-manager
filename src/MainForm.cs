using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AePluginManager
{
    internal sealed class MainForm : Form
    {
        private readonly List<AeVersion> _versions;
        private readonly Executor _exe;

        private List<InstalledItem> _installed = new List<InstalledItem>();
        private ConflictDetector.Result _analysis;
        private DetectSession _session;      // 一次拖入识别的工作区（可在候选间切换，无需重新解压）
        private InstallPlan _plan;
        private List<Session> _sessions = new List<Session>();

        private TabControl _tabs;

        // 已安装
        private ListView _lvInstalled;
        private ComboBox _cboVersion, _cboKind;
        private TextBox _txtItemInfo;
        private Label _lblInstalled;

        // 冲突
        private ListView _lvConflicts;
        private TextBox _txtConflict;
        private Label _lblConflicts;
        private Button _btnFix;

        // 安装
        private ListView _lvPlan;
        private ListBox _lstChoices;
        private Label _lblChoice;
        private Panel _choicePanel;
        private TextBox _txtPlanInfo;
        private CheckedListBox _clbTargets;
        private Button _btnInstall, _btnOpenInstaller, _btnClear, _btnRunInstallerAdmin;
        private Panel _instPanel;
        private ListBox _lstInstallers;
        private Label _lblInstaller;
        private List<InstalledItem> _beforeInstall;
        private Label _lblDrop;
        private bool _manualPlan;
        private bool _updatingChoices;

        // 回收区
        private ListView _lvRecycle;
        private TextBox _txtRecycleInfo;
        private Label _lblRecycle;

        private StatusStrip _status;
        private ToolStripStatusLabel _statusText, _statusData;
        private FileSystemWatcher _watcher;
        private Timer _debounce;
        private bool _scanning;

        public MainForm(List<AeVersion> versions, Executor executor)
        {
            _versions = versions;
            _exe = executor;

            Text = L.T("AE 插件管理器");
            ClientSize = new Size(1000, 680);
            StartPosition = FormStartPosition.CenterScreen;
            Font = Ui.BaseFont;
            AutoScaleMode = AutoScaleMode.Font;
            MinimumSize = new Size(820, 560);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            ScanAll();
            RefreshRecycle();
            SetupWatcher();
        }

        // ==================== 界面骨架 ====================

        /// <summary>切换到指定标签页（自检与快捷键共用）。</summary>
        public void SelectTab(int index)
        {
            if (index >= 0 && index < _tabs.TabPages.Count) _tabs.SelectedIndex = index;
        }

        /// <summary>自检用：把一个插件包走一遍"拖入识别"流程。</summary>
        public void LoadPathForTest(string path)
        {
            DetectPath(path);
            SelectTab(2);
        }

        /// <summary>自检用：在候选列表里选中第 N 项（触发重新生成方案）。</summary>
        public void SelectChoiceForTest(int index)
        {
            if (index >= 0 && index < _lstChoices.Items.Count) _lstChoices.SelectedIndex = index;
        }

        /// <summary>自检用：直接运行选中的安装器（不开确认框），用于验证"运行安装器→重扫→报告新增"链路。</summary>
        public void RunSelectedInstallerForTest()
        {
            Console.Error.WriteLine(string.Format("[shot] plan={0} installers={1} selIndex={2} manual={3}",
                _plan == null ? "null" : "ok",
                _plan == null ? -1 : _plan.Installers.Count,
                _lstInstallers.SelectedIndex, _manualPlan));
            if (_plan != null) _plan.Installers.ForEach(delegate (InstallerInfo i)
            {
                Console.Error.WriteLine("[shot]   " + i.FullPath + " kind=" + i.Kind + " notExtracted=" + i.NotExtracted);
            });
            var ins = SelectedInstaller();
            if (ins == null) throw new InvalidOperationException(L.T("没有可运行的安装器"));
            RunInstallerCore(ins, false, false);
        }

        private void BuildUi()
        {
            _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            _tabs.TabPages.Add(BuildInstalledTab());
            _tabs.TabPages.Add(BuildConflictTab());
            _tabs.TabPages.Add(BuildInstallTab());
            _tabs.TabPages.Add(BuildRecycleTab());

            _status = new StatusStrip { SizingGrip = false };
            _statusText = new ToolStripStatusLabel(L.T("就绪")) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _statusData = new ToolStripStatusLabel(L.T("数据目录：") + _exe.DataDir) { IsLink = true };
            _statusData.Click += (s, e) => _exe.OpenFolder(_exe.DataDir);
            _status.Items.Add(_statusText);
            _status.Items.Add(_statusData);

            var menu = new MenuStrip { Dock = DockStyle.Top };
            var mFile = new ToolStripMenuItem(L.T("软件(&F)"));
            mFile.DropDownItems.Add(L.T("重新扫描\tF5"), null, (s, e) => ScanAll());
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add(L.T("更改备份位置…"), null, (s, e) => ChangeDataDir());
            mFile.DropDownItems.Add(L.T("打开数据目录（备份与回收区）"), null, (s, e) => _exe.OpenFolder(_exe.DataDir));
            mFile.DropDownItems.Add(L.T("查看操作日志"), null, (s, e) => OpenLog());
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add(L.T("关于"), null, (s, e) => ShowAbout());
            mFile.DropDownItems.Add(L.T("退出"), null, (s, e) => Close());
            menu.Items.Add(mFile);

            var mHelp = new ToolStripMenuItem(L.T("安全说明(&S)"));
            mHelp.Click += (s, e) => ShowSafety();
            menu.Items.Add(mHelp);

            Controls.Add(_tabs);
            Controls.Add(_status);
            Controls.Add(menu);
            MainMenuStrip = menu;

            KeyPreview = true;
            KeyDown += MainForm_KeyDown;
        }

        /// <summary>快捷键：F5 重新扫描，Ctrl+Tab / Ctrl+1~4 切换标签页。</summary>
        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { ScanAll(); e.Handled = true; return; }

            if (e.Control && e.KeyCode == Keys.Tab)
            {
                _tabs.SelectedIndex = (_tabs.SelectedIndex + 1) % _tabs.TabPages.Count;
                e.Handled = true;
                return;
            }
            if (e.Control && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D4)
            {
                int index = e.KeyCode - Keys.D1;
                if (index < _tabs.TabPages.Count) _tabs.SelectedIndex = index;
                e.Handled = true;
            }
        }

        private TabPage BuildInstalledTab()
        {
            var page = new TabPage(L.T("已安装插件")) { Padding = new Padding(8), BackColor = SystemColors.Control };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, WrapContents = false, AutoSize = false };
            _cboVersion = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Margin = new Padding(0, 3, 8, 0) };
            _cboVersion.Items.Add(L.T("全部版本"));
            foreach (var v in _versions) _cboVersion.Items.Add("AE " + v.Version);
            _cboVersion.SelectedIndex = 0;
            _cboVersion.SelectedIndexChanged += (s, e) => FillInstalled();

            _cboKind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130, Margin = new Padding(0, 3, 8, 0) };
            _cboKind.Items.AddRange(new object[] { L.T("全部类型"), L.T("特效插件"), L.T("脚本"), L.T("预设"), L.T("扩展"), L.T("编解码插件") });
            _cboKind.SelectedIndex = 0;
            _cboKind.SelectedIndexChanged += (s, e) => FillInstalled();

            var btnRescan = new Button { Text = L.T("重新扫描"), AutoSize = true, Margin = new Padding(0, 1, 8, 0) };
            btnRescan.Click += (s, e) => ScanAll();
            var btnOpen = new Button { Text = L.T("在资源管理器中显示"), AutoSize = true, Margin = new Padding(0, 1, 8, 0) };
            btnOpen.Click += (s, e) => OpenSelected(_lvInstalled, true);
            var btnUninstall = new Button { Text = L.T("卸载到回收区"), AutoSize = true, Margin = new Padding(0, 1, 0, 0) };
            btnUninstall.Click += (s, e) => UninstallSelected();

            _lblInstalled = new Label { AutoSize = true, Margin = new Padding(12, 7, 0, 0), ForeColor = Color.DimGray };
            bar.Controls.AddRange(new Control[] { _cboVersion, _cboKind, btnRescan, btnOpen, btnUninstall, _lblInstalled });

            _lvInstalled = new ListView();
            Ui.StyleGrid(_lvInstalled);
            _lvInstalled.Columns.Add(L.T("名称"), 260);
            _lvInstalled.Columns.Add(L.T("类型"), 80);
            _lvInstalled.Columns.Add(L.T("AE 版本"), 80);
            _lvInstalled.Columns.Add(L.T("位数"), 50);
            _lvInstalled.Columns.Add(L.T("大小"), 80);
            _lvInstalled.Columns.Add(L.T("位置"), 330);
            _lvInstalled.Columns.Add(L.T("修改时间"), 120);
            _lvInstalled.SelectedIndexChanged += (s, e) => ShowItemInfo();
            _lvInstalled.DoubleClick += (s, e) => OpenSelected(_lvInstalled, true);
            _lvInstalled.Resize += (s, e) => Ui.SizeColumns(_lvInstalled, 0.26, 0.08, 0.08, 0.05, 0.08, 0.33, 0.12);

            _txtItemInfo = new TextBox
            {
                Dock = DockStyle.Bottom,
                Height = 54,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle
            };

            page.Controls.Add(_lvInstalled);
            page.Controls.Add(_txtItemInfo);
            page.Controls.Add(bar);
            return page;
        }

        private TabPage BuildConflictTab()
        {
            var page = new TabPage(L.T("冲突与不适用")) { Padding = new Padding(8), BackColor = SystemColors.Control };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, WrapContents = false };
            var btnRescan = new Button { Text = L.T("重新检测"), AutoSize = true, Margin = new Padding(0, 1, 8, 0) };
            btnRescan.Click += (s, e) => ScanAll();
            _btnFix = new Button { Text = L.T("一键修复"), AutoSize = true, Enabled = false, Margin = new Padding(0, 1, 8, 0) };
            _btnFix.Click += (s, e) => RepairSelected();
            var btnUninstall = new Button { Text = L.T("把选中条目移入回收区"), AutoSize = true, Margin = new Padding(0, 1, 8, 0) };
            btnUninstall.Click += (s, e) => UninstallFromConflict();
            var btnOpen = new Button { Text = L.T("在资源管理器中显示"), AutoSize = true, Margin = new Padding(0, 1, 0, 0) };
            btnOpen.Click += (s, e) => OpenSelected(_lvConflicts, false);
            _lblConflicts = new Label { AutoSize = true, Margin = new Padding(12, 7, 0, 0), ForeColor = Color.DimGray };
            bar.Controls.AddRange(new Control[] { btnRescan, _btnFix, btnUninstall, btnOpen, _lblConflicts });

            _lvConflicts = new ListView();
            Ui.StyleGrid(_lvConflicts);
            _lvConflicts.Columns.Add(L.T("级别"), 60);
            _lvConflicts.Columns.Add(L.T("问题"), 520);
            _lvConflicts.Columns.Add(L.T("修复方案"), 260);
            _lvConflicts.Columns.Add(L.T("涉及文件"), 80);
            _lvConflicts.SelectedIndexChanged += (s, e) => ShowConflictInfo();
            _lvConflicts.Resize += (s, e) => Ui.SizeColumns(_lvConflicts, 0.06, 0.54, 0.31, 0.09);

            _txtConflict = new TextBox
            {
                Dock = DockStyle.Bottom,
                Height = 118,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Ui.BaseFont
            };

            page.Controls.Add(_lvConflicts);
            page.Controls.Add(_txtConflict);
            page.Controls.Add(bar);
            return page;
        }

        private TabPage BuildInstallTab()
        {
            var page = new TabPage(L.T("安装插件")) { Padding = new Padding(8), BackColor = SystemColors.Control };
            page.AllowDrop = true;

            _lblDrop = new Label
            {
                Dock = DockStyle.Top,
                Height = 62,
                Text = L.T("把插件压缩包（.zip / .rar / .7z）、插件文件夹，或单个插件文件（.aex / .jsxbin / .jsx / .ffx）拖到这里\n") +
                       L.T("包内只有 .exe / .msi 安装器也可以：软件会列出安装器，由你确认后拉起安装向导（不静默安装）"),
                TextAlign = ContentAlignment.MiddleCenter,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(245, 248, 255),
                ForeColor = Color.FromArgb(60, 60, 60)
            };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false };
            var btnPick = new Button { Text = L.T("选择压缩包或文件夹…"), AutoSize = true, Margin = new Padding(0, 2, 8, 0) };
            btnPick.Click += (s, e) => PickAndDetect();
            _clbTargets = new CheckedListBox { Width = 190, Height = 28, CheckOnClick = true, Margin = new Padding(8, 1, 8, 0) };
            foreach (var v in _versions) _clbTargets.Items.Add("AE " + v.Version);
            // 默认勾选最新版本；勾选变化实时更新安装按钮状态
            if (_clbTargets.Items.Count > 0) _clbTargets.SetItemChecked(0, true);
            _clbTargets.ItemCheck += (s, e) => BeginInvoke(new MethodInvoker(UpdateInstallButton));
            _btnInstall = new Button { Text = L.T("开始安装（自动备份）"), AutoSize = true, Enabled = false, Margin = new Padding(0, 2, 8, 0) };
            _btnInstall.Click += (s, e) => RunInstall();
            _btnOpenInstaller = new Button { Text = L.T("打开所在目录"), AutoSize = true, Visible = false, Margin = new Padding(0, 2, 8, 0) };
            _btnOpenInstaller.Click += (s, e) => OpenInstallerOrRun();
            _btnRunInstallerAdmin = new Button { Text = L.T("以管理员身份运行安装器"), AutoSize = true, Visible = false, Margin = new Padding(0, 2, 8, 0) };
            _btnRunInstallerAdmin.Click += (s, e) => RunInstaller(true);
            _btnClear = new Button { Text = L.T("清空"), AutoSize = true, Visible = false, Margin = new Padding(0, 2, 0, 0) };
            _btnClear.Click += (s, e) => ClearPlan();

            bar.Controls.AddRange(new Control[] { btnPick, _clbTargets, _btnInstall, _btnOpenInstaller, _btnRunInstallerAdmin, _btnClear });

            // ---- 包内只有安装程序时：列出安装器供选择，由软件拉起安装向导 ----
            _instPanel = new Panel { Dock = DockStyle.Top, Height = 96, Visible = false };
            _lblInstaller = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Text = L.T("  这个包只有安装程序，请选择要运行的安装器（安装向导由本软件拉起，不静默安装）："),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(0, 90, 160),
                Font = Ui.BaseFont
            };
            _lstInstallers = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Ui.BaseFont
            };
            _instPanel.Controls.Add(_lstInstallers);
            _instPanel.Controls.Add(_lblInstaller);

            // ---- 版本候选：包内含中文版/英文版/Mac 版等多个可装内容时，由用户自行选择 ----
            _choicePanel = new Panel { Dock = DockStyle.Top, Height = 116, Visible = false };
            _lblChoice = new Label
            {
                Dock = DockStyle.Top,
                Height = 22,
                Text = L.T("  这个包里有多个可安装的内容，请选择要装的版本："),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(180, 90, 0),
                Font = Ui.BaseFont
            };
            _lstChoices = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Ui.BaseFont
            };
            _lstChoices.SelectedIndexChanged += (s, e) => OnChoiceChanged();
            _choicePanel.Controls.Add(_lstChoices);
            _choicePanel.Controls.Add(_lblChoice);

            _lvPlan = new ListView();
            Ui.StyleGrid(_lvPlan);
            _lvPlan.MultiSelect = false;
            _lvPlan.Columns.Add(L.T("类型"), 80);
            _lvPlan.Columns.Add(L.T("将写入的位置"), 560);
            _lvPlan.Columns.Add(L.T("大小"), 80);
            _lvPlan.Columns.Add(L.T("状态"), 110);
            _lvPlan.Resize += (s, e) => Ui.SizeColumns(_lvPlan, 0.09, 0.63, 0.09, 0.19);

            _txtPlanInfo = new TextBox
            {
                Dock = DockStyle.Bottom,
                Height = 106,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle
            };

            page.Controls.Add(_lvPlan);
            page.Controls.Add(_txtPlanInfo);
            page.Controls.Add(_choicePanel);
            page.Controls.Add(_instPanel);
            page.Controls.Add(bar);
            page.Controls.Add(_lblDrop);

            page.DragEnter += (s, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
            page.DragDrop += (s, e) => { var f = FirstFile(e); if (f != null) DetectPath(f); };
            _lblDrop.AllowDrop = true;
            _lblDrop.DragEnter += (s, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
            _lblDrop.DragDrop += (s, e) => { var f = FirstFile(e); if (f != null) DetectPath(f); };
            _lvPlan.AllowDrop = true;
            _lvPlan.DragEnter += (s, e) => e.Effect = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
            _lvPlan.DragDrop += (s, e) => { var f = FirstFile(e); if (f != null) DetectPath(f); };

            return page;
        }

        private TabPage BuildRecycleTab()
        {
            var page = new TabPage(L.T("回收区")) { Padding = new Padding(8), BackColor = SystemColors.Control };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, WrapContents = false };
            var btnRefresh = new Button { Text = L.T("刷新"), AutoSize = true, Margin = new Padding(0, 1, 8, 0) };
            btnRefresh.Click += (s, e) => RefreshRecycle();
            var btnRestore = new Button { Text = L.T("一键还原"), AutoSize = true, Margin = new Padding(0, 1, 8, 0) };
            btnRestore.Click += (s, e) => RestoreSelected();
            var btnPurge = new Button { Text = L.T("彻底删除备份"), AutoSize = true, Margin = new Padding(0, 1, 8, 0) };
            btnPurge.Click += (s, e) => PurgeSelected();
            var btnOpen = new Button { Text = L.T("打开备份目录"), AutoSize = true, Margin = new Padding(0, 1, 0, 0) };
            btnOpen.Click += (s, e) => _exe.OpenFolder(_exe.RecycleDir);
            _lblRecycle = new Label { AutoSize = true, Margin = new Padding(12, 7, 0, 0), ForeColor = Color.DimGray };
            bar.Controls.AddRange(new Control[] { btnRefresh, btnRestore, btnPurge, btnOpen, _lblRecycle });

            _lvRecycle = new ListView();
            Ui.StyleGrid(_lvRecycle);
            _lvRecycle.MultiSelect = false;
            _lvRecycle.Columns.Add(L.T("操作"), 60);
            _lvRecycle.Columns.Add(L.T("时间"), 140);
            _lvRecycle.Columns.Add(L.T("内容"), 520);
            _lvRecycle.Columns.Add(L.T("文件数"), 70);
            _lvRecycle.SelectedIndexChanged += (s, e) => ShowRecycleInfo();
            _lvRecycle.Resize += (s, e) => Ui.SizeColumns(_lvRecycle, 0.07, 0.16, 0.62, 0.15);

            _txtRecycleInfo = new TextBox
            {
                Dock = DockStyle.Bottom,
                Height = 110,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle
            };

            page.Controls.Add(_lvRecycle);
            page.Controls.Add(_txtRecycleInfo);
            page.Controls.Add(bar);
            return page;
        }

        // ==================== 扫描 ====================

        private void ScanAll()
        {
            if (_scanning) return;
            _scanning = true;
            Cursor = Cursors.WaitCursor;
            _statusText.Text = L.T("正在扫描 AE 插件目录…");
            try
            {
                _installed = Scanner.Scan(_versions);
                _analysis = ConflictDetector.Analyze(_versions, _installed);
                FillInstalled();
                FillConflicts();
                int high = 0;
                foreach (var c in _analysis.Conflicts) if (c.Level == ConflictLevel.High) high++;
                _statusText.Text = string.Format(L.T("共发现 {0} 个已安装条目，{1} 项问题（其中 {2} 项严重）{3}"),
                    _installed.Count, _analysis.Conflicts.Count, high,
                    AeEnv.IsAeRunning() ? L.T("　⚠ After Effects 正在运行") : "");
            }
            catch (Exception ex)
            {
                Ui.Error(L.T("扫描失败"), ex.Message);
            }
            finally
            {
                Cursor = Cursors.Default;
                _scanning = false;
            }
        }

        private void FillInstalled()
        {
            _lvInstalled.BeginUpdate();
            _lvInstalled.Items.Clear();
            int shown = 0;
            string verFilter = _cboVersion.SelectedIndex <= 0 ? null : _cboVersion.SelectedItem.ToString().Replace("AE ", "");
            string kindFilter = _cboKind.SelectedIndex <= 0 ? null : _cboKind.SelectedItem.ToString();

            foreach (var it in _installed)
            {
                if (verFilter != null && it.Ae.Version != verFilter) continue;
                if (kindFilter != null && Kinds.Label(it.Kind) != kindFilter) continue;

                var row = new ListViewItem(it.Name);
                row.SubItems.Add(Kinds.Label(it.Kind));
                row.SubItems.Add("AE " + it.Ae.Version);
                row.SubItems.Add(Kinds.ArchLabel(it.Arch));
                row.SubItems.Add(Fs.SizeText(it.Size));
                row.SubItems.Add(it.RelPath);
                row.SubItems.Add(it.Modified == DateTime.MinValue ? "" : it.Modified.ToString("yyyy-MM-dd HH:mm"));
                row.Tag = it;
                if (it.IsBundle) row.Font = new Font(Ui.BaseFont, FontStyle.Regular);
                if (it.Arch == "x86") row.ForeColor = Ui.LevelColor(ConflictLevel.High);
                _lvInstalled.Items.Add(row);
                shown++;
            }
            _lvInstalled.EndUpdate();
            Ui.SizeColumns(_lvInstalled, 0.26, 0.08, 0.08, 0.05, 0.08, 0.33, 0.12);
            _lblInstalled.Text = string.Format(L.T("显示 {0} / 共 {1}"), shown, _installed.Count);
        }

        private void FillConflicts()
        {
            _lvConflicts.BeginUpdate();
            _lvConflicts.Items.Clear();
            foreach (var c in _analysis.Conflicts)
            {
                var row = new ListViewItem(Ui.LevelText(c.Level));
                row.SubItems.Add(c.Title);
                row.SubItems.Add(c.CanRepair ? c.RepairLabel : "—");
                row.SubItems.Add(c.Items.Count.ToString());
                row.Tag = c;
                row.ForeColor = Ui.LevelColor(c.Level);
                _lvConflicts.Items.Add(row);
            }
            _lvConflicts.EndUpdate();
            Ui.SizeColumns(_lvConflicts, 0.06, 0.54, 0.31, 0.09);
            _btnFix.Enabled = false;

            int high = 0, med = 0, fixable = 0;
            foreach (var c in _analysis.Conflicts)
            {
                if (c.Level == ConflictLevel.High) high++;
                else if (c.Level == ConflictLevel.Medium) med++;
                if (c.CanRepair && c.Repair == RepairKind.MoveToRecycle) fixable++;
            }
            _lblConflicts.Text = string.Format(L.T("严重 {0} · 注意 {1} · 提示 {2}　可一键修复 {3} 项"),
                high, med, _analysis.Conflicts.Count - high - med, fixable);
        }

        private void RefreshRecycle()
        {
            _sessions = _exe.LoadSessions();
            var list = new List<Session>();
            foreach (var s in _sessions) if (s.Op == "uninstall") list.Add(s);

            _lvRecycle.BeginUpdate();
            _lvRecycle.Items.Clear();
            foreach (var s in list)
            {
                var row = new ListViewItem(s.OpLabel);
                row.SubItems.Add(s.TimeText);
                row.SubItems.Add(s.Description);
                row.SubItems.Add(s.Records.Count.ToString());
                row.Tag = s;
                _lvRecycle.Items.Add(row);
            }
            _lvRecycle.EndUpdate();
            Ui.SizeColumns(_lvRecycle, 0.07, 0.16, 0.62, 0.15);
            _lblRecycle.Text = string.Format(L.T("{0} 次卸载记录（共 {1} 个条目）"), list.Count,
                CountRecords(list));
        }

        private static int CountRecords(List<Session> list)
        {
            int n = 0;
            foreach (var s in list) n += s.Records.Count;
            return n;
        }

        // ==================== 详情面板 ====================

        private void ShowItemInfo()
        {
            if (_lvInstalled.SelectedItems.Count == 0) { _txtItemInfo.Text = ""; return; }
            var it = _lvInstalled.SelectedItems[0].Tag as InstalledItem;
            if (it == null) return;
            _txtItemInfo.Text = string.Format(
                L.T("名称：{0}\r\n类型：{1}{2}\r\n位置：{3}\r\n大小：{4}　修改时间：{5}\r\n"),
                it.Name, Kinds.Label(it.Kind), it.IsBundle ? L.T("（目录形式）") : "",
                it.FullPath, Fs.SizeText(it.Size),
                it.Modified == DateTime.MinValue ? L.T("未知") : it.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        private void ShowConflictInfo()
        {
            if (_lvConflicts.SelectedItems.Count == 0)
            {
                _txtConflict.Text = "";
                _btnFix.Enabled = false;
                _btnFix.Text = L.T("一键修复");
                return;
            }
            var c = _lvConflicts.SelectedItems[0].Tag as Conflict;
            if (c == null) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(c.Title);
            sb.AppendLine(c.Detail);
            if (c.Advice.Length > 0) sb.AppendLine(L.T("建议：") + c.Advice);
            if (c.CanRepair)
            {
                sb.AppendLine();
                sb.AppendLine(L.T("【修复方案】") + c.RepairLabel);
                sb.AppendLine(c.RepairDetail);
            }
            if (c.Items.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(L.T("涉及文件："));
                int n = 0;
                foreach (var it in c.Items)
                {
                    if (n++ >= 12) { sb.AppendLine(L.T("  … 以及另外 ") + (c.Items.Count - 12) + L.T(" 个")); break; }
                    sb.AppendLine("  " + it.FullPath);
                }
            }
            _txtConflict.Text = sb.ToString().Replace("\n", "\r\n");

            _btnFix.Enabled = c.CanRepair;
            _btnFix.Text = c.Repair == RepairKind.CloseAe ? L.T("查看关闭方法")
                         : (c.CanRepair ? L.T("一键修复：") + Fs.Elide(c.RepairLabel, 22) : L.T("一键修复"));
        }

        /// <summary>执行选中冲突项的修复动作（全部经回收区，可还原）。</summary>
        private void RepairSelected()
        {
            if (_lvConflicts.SelectedItems.Count == 0) { Ui.Warn(L.T("请先选中一个问题")); return; }
            var c = _lvConflicts.SelectedItems[0].Tag as Conflict;
            if (c == null || !c.CanRepair) { Ui.Warn(L.T("这一项没有可自动执行的修复动作")); return; }

            if (c.Repair == RepairKind.CloseAe)
            {
                Ui.Info(L.T("关闭 After Effects"), c.RepairDetail);
                return;
            }

            // 同版本多副本：先让用户挑出要保留的那一份，软件不替他决定
            var toRemove = new List<InstalledItem>(c.RepairItems);
            if (c.RepairNeedsPick)
            {
                var keep = PickItemToKeep(c);
                if (keep == null) return;                      // 用户取消
                toRemove.Clear();
                foreach (var it in c.Items)
                    if (!ReferenceEquals(it, keep)) toRemove.Add(it);
                if (toRemove.Count == 0) { Ui.Warn(L.T("至少需要保留一份")); return; }
            }

            if (toRemove.Count == 0) { Ui.Warn(L.T("这一项没有需要处理的文件")); return; }

            if (AeEnv.IsAeRunning() &&
                !Ui.Confirm(L.T("After Effects 正在运行，文件可能被占用导致操作失败。\n\n仍要继续吗？")))
                return;

            string list = string.Join("\n", toRemove.ConvertAll(i => "  " + i.FullPath).ToArray());
            if (!Ui.Confirm(string.Format(
                L.T("【{0}】\n\n{1}\n\n即将处理 {2} 个文件：\n{3}\n\n") +
                L.T("这些文件会被移动到回收区（不会真正删除），随时可以在「回收区」一键还原。是否继续？"),
                c.Title, c.RepairLabel, toRemove.Count, list)))
                return;

            Cursor = Cursors.WaitCursor;
            var result = _exe.Uninstall(toRemove);
            Cursor = Cursors.Default;

            ScanAll();
            RefreshRecycle();
            Ui.Info(L.T("修复结果"), result.Message + L.T("\n\n完成后可再点「重新检测」确认该项已消失。"));
        }

        /// <summary>
        /// 列出同名插件的全部副本，由用户选定要保留的那一份。
        /// 默认选中"路径最短"（通常是 Plug-ins 根目录下那份），但决定权在用户。
        /// </summary>
        private InstalledItem PickItemToKeep(Conflict c)
        {
            using (var dlg = new Form())
            {
                dlg.Text = L.T("选择要保留的副本 — ") + c.Title;
                dlg.ClientSize = new Size(760, 300);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.Font = Ui.BaseFont;

                var tip = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 48,
                    Padding = new Padding(12, 10, 12, 6),
                    Text = L.T("同一个插件存在多个副本时 After Effects 只会加载其中一个，具体加载哪个取决于目录扫描顺序。\n") +
                           L.T("请选择要保留的那一份，其余副本将被移入回收区（可随时还原）：")
                };

                var table = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 2,
                    RowCount = c.Items.Count,
                    AutoScroll = true,
                    Padding = new Padding(12, 4, 12, 4)
                };
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                var radios = new List<RadioButton>();
                int defaultIndex = 0;
                for (int i = 0; i < c.Items.Count; i++)
                    if (c.Items[i].RelPath.Length < c.Items[defaultIndex].RelPath.Length) defaultIndex = i;

                for (int i = 0; i < c.Items.Count; i++)
                {
                    var it = c.Items[i];
                    string arch = it.Arch != null && it.Arch.Length > 0 ? "　" + Kinds.ArchLabel(it.Arch) : "";
                    var rb = new RadioButton
                    {
                        Text = string.Format(L.T("{0}　[{1}]　{2}{3}\n     修改时间 {4}{5}"),
                            it.Name, Fs.SizeText(it.Size), it.RelPath, arch,
                            it.Modified == DateTime.MinValue ? L.T("未知") : it.Modified.ToString("yyyy-MM-dd HH:mm"),
                            it.Ae == null ? "" : "　·　AE " + it.Ae.Version),
                        AutoSize = true,
                        MaximumSize = new Size(670, 0),
                        Margin = new Padding(0, 6, 0, 6),
                        Checked = i == defaultIndex
                    };
                    radios.Add(rb);
                    table.Controls.Add(rb, 1, i);
                }

                var bar = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    FlowDirection = FlowDirection.RightToLeft,
                    Height = 44,
                    Padding = new Padding(0, 8, 12, 0)
                };
                var btnOk = new Button { Text = L.T("保留这一份，其余移入回收区"), AutoSize = true, DialogResult = DialogResult.OK };
                var btnCancel = new Button { Text = L.T("取消"), AutoSize = true, DialogResult = DialogResult.Cancel };
                bar.Controls.Add(btnOk);
                bar.Controls.Add(btnCancel);

                dlg.Controls.Add(table);
                dlg.Controls.Add(tip);
                dlg.Controls.Add(bar);
                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                for (int i = 0; i < radios.Count; i++)
                    if (radios[i].Checked) return c.Items[i];
                return null;
            }
        }

        private void ShowRecycleInfo()
        {
            if (_lvRecycle.SelectedItems.Count == 0) { _txtRecycleInfo.Text = ""; return; }
            var s = _lvRecycle.SelectedItems[0].Tag as Session;
            if (s == null) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Format(L.T("{0}　{1}　{2} 个条目"), s.OpLabel, s.TimeText, s.Records.Count));
            sb.AppendLine(L.T("还原位置："));
            int n = 0;
            foreach (var r in s.Records)
            {
                if (n++ >= 15) { sb.AppendLine(L.T("  … 以及另外 ") + (s.Records.Count - 15) + L.T(" 个")); break; }
                sb.AppendLine("  " + r.Source);
            }
            _txtRecycleInfo.Text = sb.ToString();
        }

        // ==================== 安装流程 ====================

        private static bool HasFiles(DragEventArgs e)
        {
            return e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
        }

        private static string FirstFile(DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            return files != null && files.Length > 0 ? files[0] : null;
        }

        private void PickAndDetect()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = L.T("选择插件压缩包（也可直接拖入文件夹）");
                dlg.Filter = "插件压缩包|*.zip;*.rar;*.7z|所有文件|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK) DetectPath(dlg.FileName);
            }
        }

        /// <summary>拖入/选择后的第一步：识别 + 列出可安装的内容候选。</summary>
        private void DetectPath(string path)
        {
            Cursor = Cursors.WaitCursor;
            _statusText.Text = L.T("正在识别：") + Path.GetFileName(path) + " …";
            Application.DoEvents();
            try
            {
                _session = Installer.Analyze(path);
                _plan = null;
                // 记录安装前的状态，便于安装器跑完后指出"新装了什么"
                _beforeInstall = new List<InstalledItem>(_installed);
            }
            catch (Exception ex)
            {
                Ui.Error(L.T("识别失败"), ex.Message);
                _session = null;
            }
            finally { Cursor = Cursors.Default; }

            FillChoices();
            RebuildPlan();
        }

        /// <summary>把识别到的候选填进选择框；只有 1 个候选时隐藏选择区。</summary>
        private void FillChoices()
        {
            _updatingChoices = true;
            _lstChoices.Items.Clear();

            if (_session == null || _session.ManualReason != null || !_session.NeedChoice)
            {
                _choicePanel.Visible = false;
                _updatingChoices = false;
                return;
            }

            foreach (var choice in _session.Choices)
                _lstChoices.Items.Add((choice.Preferred ? "★ " : "   ") + choice.Display);
            _lstChoices.SelectedIndex = _session.DefaultChoice;
            _lblChoice.Text = string.Format(L.T("  这个包里有 {0} 个可安装的内容，请选择要装的版本（★ 为推荐）："), _session.Choices.Count);
            _choicePanel.Visible = true;
            _updatingChoices = false;
        }

        private void OnChoiceChanged()
        {
            if (_updatingChoices || _session == null) return;
            if (_lstChoices.SelectedIndex < 0) return;
            _session.DefaultChoice = _lstChoices.SelectedIndex;
            RebuildPlan();
        }

        /// <summary>按当前选定的候选重建安装方案。</summary>
        private void RebuildPlan()
        {
            _plan = null;
            _manualPlan = false;

            if (_session == null) { ShowPlan(); return; }

            var ae = FirstCheckedVersion() ?? _versions[0];
            try
            {
                _plan = Installer.BuildPlan(_session, _session.DefaultChoice, ae, false);
                _manualPlan = _plan.ManualReason != null;
                if (_manualPlan && _session.ManualDir != null) _plan.TempDir = _session.ManualDir;
            }
            catch (Exception ex)
            {
                Ui.Error(L.T("生成安装方案失败"), ex.Message);
            }
            ShowPlan();
        }

        private void ShowPlan()
        {
            _lvPlan.BeginUpdate();
            _lvPlan.Items.Clear();
            _txtPlanInfo.Text = "";
            _btnClear.Visible = _plan != null;
            FillInstallerList();

            if (_plan == null)
            {
                _btnInstall.Enabled = false;
                _btnOpenInstaller.Visible = false;
                _btnRunInstallerAdmin.Visible = false;
                _statusText.Text = L.T("就绪");
                _lvPlan.EndUpdate();
                return;
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(L.T("来源：") + _plan.SourcePath);
            sb.AppendLine(L.T("识别包名：") + _plan.PackageName);
            sb.AppendLine();

            if (_manualPlan)
            {
                sb.AppendLine(L.T("【需要运行安装向导】") + _plan.ManualReason);
                sb.AppendLine();
                sb.AppendLine(_plan.ManualHint);
                _btnInstall.Enabled = false;
                _btnOpenInstaller.Visible = true;
                _statusText.Text = L.T("请在上方选择安装器后点「运行选中的安装器」");
            }
            else
            {
                sb.AppendLine(string.Format(L.T("【安装方案】{0} 个文件，共 {1}"), _plan.Actions.Count, Fs.SizeText(_plan.TotalBytes)));
                foreach (var a in _plan.Actions)
                {
                    var row = new ListViewItem(Kinds.Label(a.Kind));
                    row.SubItems.Add(TargetForDisplay(a));
                    row.SubItems.Add(Fs.SizeText(a.Size));
                    string state = a.TargetExists
                        ? L.T("覆盖（已备份）")
                        : L.T("新增");
                    if (a.ExistingArch == "x86") state = L.T("覆盖 32 位旧版");
                    row.SubItems.Add(state);
                    if (a.TargetExists) row.ForeColor = Ui.LevelColor(ConflictLevel.Medium);
                    if (a.ExistingArch == "x86") row.ForeColor = Ui.LevelColor(ConflictLevel.High);
                    _lvPlan.Items.Add(row);
                }
                int targets = CountChecked();
                sb.AppendLine();
                sb.AppendLine(string.Format(L.T("将安装到：{0}"), targets == 0 ? L.T("（未勾选任何 AE 版本）") : targets + L.T(" 个 AE 版本")));
                sb.AppendLine(L.T("写入前会自动备份被覆盖的文件；安装记录可在「回收区」一键还原。"));
                UpdateInstallButton();
                _btnOpenInstaller.Visible = false;
                _statusText.Text = string.Format(L.T("方案就绪：{0} 个文件，{1}"), _plan.Actions.Count, Fs.SizeText(_plan.TotalBytes));
            }

            foreach (var n in _plan.Notes) sb.AppendLine("· " + n);
            _txtPlanInfo.Text = sb.ToString();
            _lvPlan.EndUpdate();
            Ui.SizeColumns(_lvPlan, 0.09, 0.63, 0.09, 0.19);
        }

        /// <summary>把识别到的安装程序填进"安装器"列表；没有安装器时隐藏该区。</summary>
        private void FillInstallerList()
        {
            _lstInstallers.Items.Clear();
            if (_plan == null || _plan.Installers.Count == 0)
            {
                _instPanel.Visible = false;
                _btnOpenInstaller.Visible = false;
                _btnRunInstallerAdmin.Visible = false;
                return;
            }

            int preferred = 0;
            for (int i = 0; i < _plan.Installers.Count; i++)
            {
                var ins = _plan.Installers[i];
                _lstInstallers.Items.Add(ins.Display);
                if (ins.Preferred) preferred = i;
            }
            _lstInstallers.SelectedIndex = preferred;

            bool macOnly = true;
            foreach (var ins in _plan.Installers)
                if (ins.Kind == "exe" || ins.Kind == "msi") { macOnly = false; break; }

            _lblInstaller.Text = macOnly
                ? L.T("  包内只有 macOS 安装包，无法安装到 Windows 版 AE")
                : L.T("  请选择要运行的安装器（★ 为推荐；向导由本软件拉起，不静默安装）：");
            _instPanel.Visible = true;

            // 未解压出来的超大安装器无法直接运行，此时不显示运行按钮
            bool canRun = false;
            foreach (var ins in _plan.Installers)
                if (!ins.NotExtracted && (ins.Kind == "exe" || ins.Kind == "msi")) canRun = true;

            _btnOpenInstaller.Visible = true;
            _btnRunInstallerAdmin.Visible = canRun;
            UpdateRunInstallerButton();
        }

        private InstallerInfo SelectedInstaller()
        {
            if (_plan == null) return null;
            int i = _lstInstallers.SelectedIndex;
            if (i < 0 || i >= _plan.Installers.Count) return null;
            return _plan.Installers[i];
        }

        private void UpdateRunInstallerButton()
        {
            var ins = SelectedInstaller();
            bool canRun = ins != null && !ins.NotExtracted && (ins.Kind == "exe" || ins.Kind == "msi");
            _btnOpenInstaller.Text = canRun ? L.T("运行选中的安装器") : L.T("打开所在目录");
            _btnRunInstallerAdmin.Visible = canRun;
        }

        /// <summary>
        /// 运行安装程序。不静默、不加隐藏参数：把安装向导原样交给用户，
        /// 装完后再扫一遍 AE 目录，告诉用户这次多了哪些文件。
        /// </summary>
        private void RunInstaller(bool asAdmin)
        {
            var ins = SelectedInstaller();
            if (ins == null) { Ui.Warn(L.T("请先在上方选择要运行的安装器")); return; }
            if (ins.NotExtracted) { Ui.Warn(L.T("这个安装器体积过大，没有解压出来。\n请先手动解压原压缩包后再运行。")); return; }
            if (!File.Exists(ins.FullPath)) { Ui.Warn(L.T("安装程序不存在：") + ins.FullPath); return; }

            if (!Ui.Confirm(string.Format(
                L.T("即将运行安装程序：\n\n  {0}\n\n") +
                L.T("· 安装向导会正常显示，请按向导操作；\n") +
                L.T("· 本软件不添加任何静默/隐藏参数，也不会替你点下一步；\n") +
                L.T("· 安装器可能要求管理员权限（若提示失败，请改用「以管理员身份运行安装器」）；\n") +
                L.T("· 装完后本软件会重新扫描 AE 目录，告诉你新增了哪些插件。\n\n是否继续？"),
                Path.GetFileName(ins.FullPath))))
                return;

            RunInstallerCore(ins, asAdmin, true);
        }

        /// <summary>运行安装器并对比 AE 目录变化。interactive=false 时不开确认框（自检用）。</summary>
        private void RunInstallerCore(InstallerInfo ins, bool asAdmin, bool interactive)
        {
            var before = new List<InstalledItem>(_installed);
            var startedAt = DateTime.Now;

            Cursor = Cursors.WaitCursor;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(ins.FullPath) { UseShellExecute = true };
                if (asAdmin) psi.Verb = "runas";
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) { Ui.Error(L.T("无法启动安装程序")); return; }
                    Cursor = Cursors.Default;
                    _statusText.Text = L.T("等待安装向导结束…");
                    Application.DoEvents();
                    p.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                Ui.Error(L.T("启动安装程序失败"), ex.Message + L.T("\n\n如果提示权限不足，请改用「以管理员身份运行安装器」。"));
                return;
            }
            Cursor = Cursors.Default;

            // 安装后对比：找出新增的插件文件
            ScanAll();
            var added = new List<InstalledItem>();
            foreach (var it in _installed)
            {
                bool known = false;
                foreach (var old in before)
                    if (string.Equals(old.FullPath, it.FullPath, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                if (!known || it.Modified > startedAt.AddSeconds(-2)) added.Add(it);
            }

            var sb = new System.Text.StringBuilder();
            if (added.Count == 0)
            {
                sb.AppendLine(L.T("安装向导已结束，但没有在 AE 插件目录里发现新文件。"));
                sb.AppendLine();
                sb.AppendLine(L.T("可能的原因："));
                sb.AppendLine(L.T("· 你在向导里点了取消；"));
                sb.AppendLine(L.T("· 安装器把插件装到了 C:\\Program Files\\Adobe\\Common\\Plug-ins（共享目录，需要管理员权限）；"));
                sb.AppendLine(L.T("· 安装器要求管理员权限但被拒绝。"));
                sb.AppendLine();
                sb.AppendLine(L.T("可以试试「以管理员身份运行安装器」，或点「重新扫描」后再看。"));
            }
            else
            {
                sb.AppendLine(string.Format(L.T("安装完成，本次新增/更新了 {0} 个插件："), added.Count));
                int n = 0;
                foreach (var it in added)
                {
                    if (n++ >= 15) { sb.AppendLine(L.T("  … 以及另外 ") + (added.Count - 15) + L.T(" 个（见「已安装插件」页）")); break; }
                    sb.AppendLine(string.Format("  · AE {0}　{1}　{2}", it.Ae.Version, it.Name, Fs.SizeText(it.Size)));
                }
                sb.AppendLine();
                sb.AppendLine(L.T("这些文件已经可以在「已安装插件」页里管理了。"));
            }

            if (interactive) Ui.Info(L.T("安装器运行结果"), sb.ToString());
            else Console.Error.WriteLine(L.T("[shot] 安装器运行结果:\n") + sb.ToString());
        }

        private string TargetForDisplay(PlanAction a)
        {
            // 展示时用第一个勾选的目标版本换算，便于确认实际落点
            var ae = FirstCheckedVersion() ?? _versions[0];
            return RemapTarget(a.TargetPath, _versions[0], ae);
        }

        private static string RemapTarget(string path, AeVersion from, AeVersion to)
        {
            if (string.Equals(from.Root, to.Root, StringComparison.OrdinalIgnoreCase)) return path;
            string p = path.Replace(from.SupportFiles, to.SupportFiles);
            return p.Replace(from.Root, to.Root);
        }

        private AeVersion FirstCheckedVersion()
        {
            for (int i = 0; i < _clbTargets.Items.Count; i++)
            {
                if (!_clbTargets.GetItemChecked(i)) continue;
                string v = _clbTargets.Items[i].ToString().Replace("AE ", "");
                foreach (var ae in _versions) if (ae.Version == v) return ae;
            }
            return null;
        }

        private int CountChecked()
        {
            int n = 0;
            for (int i = 0; i < _clbTargets.Items.Count; i++) if (_clbTargets.GetItemChecked(i)) n++;
            return n;
        }

        /// <summary>安装按钮仅在"有可执行方案 + 至少勾选一个 AE 版本"时可用。</summary>
        private void UpdateInstallButton()
        {
            _btnInstall.Enabled = _plan != null && !_manualPlan &&
                                  _plan.Actions.Count > 0 && CountChecked() > 0;
        }

        private void RunInstall()
        {
            if (_plan == null || _manualPlan) return;
            var targets = new List<AeVersion>();
            for (int i = 0; i < _clbTargets.Items.Count; i++)
            {
                if (!_clbTargets.GetItemChecked(i)) continue;
                string v = _clbTargets.Items[i].ToString().Replace("AE ", "");
                foreach (var ae in _versions) if (ae.Version == v) targets.Add(ae);
            }
            if (targets.Count == 0) { Ui.Warn(L.T("请先勾选要安装到的 AE 版本")); return; }
            if (AeEnv.IsAeRunning() &&
                !Ui.Confirm(L.T("After Effects 正在运行，插件文件可能被占用导致写入失败。\n\n仍要继续吗？")))
                return;

            int overwrite = 0;
            foreach (var a in _plan.Actions) if (a.TargetExists) overwrite++;

            if (!Ui.Confirm(string.Format(
                L.T("即将把「{0}」安装到 {1} 个 AE 版本：\n  {2}\n\n共 {3} 个文件（{4}）{5}\n\n") +
                L.T("被覆盖的文件会先备份到数据目录，可随时还原。是否继续？"),
                _plan.PackageName, targets.Count,
                string.Join("、", targets.ConvertAll(t => "AE " + t.Version).ToArray()),
                _plan.Actions.Count, Fs.SizeText(_plan.TotalBytes),
                overwrite > 0 ? L.T("，其中 ") + overwrite + L.T(" 个覆盖现有文件") : "")))
                return;

            Cursor = Cursors.WaitCursor;
            var report = new System.Text.StringBuilder();
            int totalDone = 0, totalFailed = 0;
            var touched = new List<string>();
            foreach (var ae in targets)
            {
                _statusText.Text = L.T("正在安装到 AE ") + ae.Version + " …";
                Application.DoEvents();
                var one = _plan.Clone();
                foreach (var a in one.Actions) a.TargetPath = RemapTarget(a.TargetPath, _versions[0], ae);
                var result = _exe.Apply(one, ae);
                totalDone += result.Done;
                totalFailed += result.Failed;
                foreach (var d in result.TouchedDirs) if (!touched.Contains(d)) touched.Add(d);
                report.AppendLine("AE " + ae.Version + "：" + result.Message);
                if (result.RolledBack) report.AppendLine(L.T("  （该版本的改动已全部回滚，AE 目录保持原样）"));
            }
            Cursor = Cursors.Default;

            CleanTempQuietly();
            ScanAll();
            RefreshRecycle();

            string summary = string.Format(L.T("安装完成：{0} 个文件写入成功{1}。\n\n{2}\n") +
                L.T("如需撤销，请到「回收区」选择对应记录一键还原。"),
                totalDone, totalFailed > 0 ? "，" + totalFailed + L.T(" 个失败") : "", report.ToString());
            if (totalFailed > 0) Ui.Warn(L.T("安装结果"), summary); else Ui.Info(L.T("安装结果"), summary);

            OpenDirs(touched);
            ClearPlan();
        }

        /// <summary>按钮双职责：选中的是可运行安装器就运行它，否则打开所在目录。</summary>
        private void OpenInstallerOrRun()
        {
            var ins = SelectedInstaller();
            if (ins != null && !ins.NotExtracted && (ins.Kind == "exe" || ins.Kind == "msi")) { RunInstaller(false); return; }

            string path = ins != null ? ins.FullPath : (_plan != null ? _plan.SourcePath : null);
            if (string.IsNullOrEmpty(path)) return;
            if (Directory.Exists(path)) _exe.OpenFolder(path);
            else if (File.Exists(path)) _exe.OpenFolder(Path.GetDirectoryName(path));
            else _exe.OpenFolder(_plan != null ? _plan.SourcePath : path);
        }

        private void ClearPlan()
        {
            _plan = null;
            _session = null;
            _manualPlan = false;
            _lvPlan.Items.Clear();
            _txtPlanInfo.Text = "";
            _btnInstall.Enabled = false;
            _btnOpenInstaller.Visible = false;
            _btnClear.Visible = false;
            _updatingChoices = true;
            _lstChoices.Items.Clear();
            _updatingChoices = false;
            _choicePanel.Visible = false;
            CleanTempQuietly();
        }

        /// <summary>只清理本程序自己解压出来的临时目录；用户拖入的文件夹/压缩包原文一律不动。</summary>
        private void CleanTempQuietly()
        {
            try
            {
                if (_session != null && !_session.Scratch) return;
                Installer.CleanTemp();
            }
            catch { }
        }
        private static void OpenDirs(List<string> dirs)
        {
            int n = 0;
            foreach (var d in dirs)
            {
                if (n++ >= 2) break;
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + d + "\""); } catch { }
            }
        }

        // ==================== 卸载 / 还原 ====================

        private void UninstallSelected()
        {
            var items = new List<InstalledItem>();
            foreach (ListViewItem row in _lvInstalled.SelectedItems)
            {
                var it = row.Tag as InstalledItem;
                if (it != null) items.Add(it);
            }
            DoUninstall(items);
        }

        private void UninstallFromConflict()
        {
            if (_lvConflicts.SelectedItems.Count == 0) { Ui.Warn(L.T("请先在列表中选中一个问题")); return; }
            var c = _lvConflicts.SelectedItems[0].Tag as Conflict;
            if (c == null || c.Items.Count == 0) { Ui.Warn(L.T("这个问题没有可操作的单个文件")); return; }

            var items = new List<InstalledItem>();
            foreach (var it in c.Items)
                if (it.Kind != PluginKind.Other && !IsInside(it.FullPath, _exe.DataDir)) items.Add(it);
            if (items.Count == 0) { Ui.Warn(L.T("这个问题没有可操作的单个文件")); return; }

            DoUninstall(items);
        }

        private static bool IsInside(string path, string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        private void DoUninstall(List<InstalledItem> items)
        {
            if (items.Count == 0) return;
            if (AeEnv.IsAeRunning() && !Ui.Confirm(L.T("After Effects 正在运行，文件可能被占用。\n\n仍要继续吗？"))) return;

            string preview = items.Count == 1
                ? items[0].FullPath
                : string.Join("\n", items.ConvertAll(i => "  " + i.RelPath).GetRange(0, Math.Min(8, items.Count)).ToArray()) +
                  (items.Count > 8 ? "\n  …" : "");

            if (!Ui.Confirm(string.Format(L.T("把以下 {0} 个条目移入回收区？\n\n{1}\n\n") +
                L.T("文件不会真正删除，可在「回收区」一键还原。"), items.Count, preview)))
                return;

            Cursor = Cursors.WaitCursor;
            var result = _exe.Uninstall(items);
            Cursor = Cursors.Default;
            ScanAll();
            RefreshRecycle();
            Ui.Info(L.T("卸载结果"), result.Message);
        }

        private void RestoreSelected()
        {
            if (_lvRecycle.SelectedItems.Count == 0) { Ui.Warn(L.T("请先选择一条记录")); return; }
            var s = _lvRecycle.SelectedItems[0].Tag as Session;
            if (s == null) return;
            if (!Ui.Confirm(string.Format(L.T("把「{0}」的 {1} 个条目还原到原位置？\n\n") +
                L.T("若原位置已有同名文件，将被回收区中的版本覆盖。"), s.Description, s.Records.Count)))
                return;

            Cursor = Cursors.WaitCursor;
            var result = _exe.Restore(s);
            Cursor = Cursors.Default;
            ScanAll();
            RefreshRecycle();
            Ui.Info(L.T("还原结果"), result.Message);
        }

        private void PurgeSelected()
        {
            if (_lvRecycle.SelectedItems.Count == 0) { Ui.Warn(L.T("请先选择一条记录")); return; }
            var s = _lvRecycle.SelectedItems[0].Tag as Session;
            if (s == null) return;
            if (!Ui.Confirm(string.Format(L.T("彻底删除「{0}」在备份区的数据？\n\n") +
                L.T("删除后无法再还原这些文件（AE 目录不受影响）。"), s.Description)))
                return;
            var result = _exe.Purge(s);
            RefreshRecycle();
            Ui.Info(L.T("删除结果"), result.Message);
        }

        // ==================== 其它 ====================

        private void OpenSelected(ListView lv, bool installedMode)
        {
            if (lv.SelectedItems.Count == 0) return;
            if (installedMode)
            {
                var it = lv.SelectedItems[0].Tag as InstalledItem;
                if (it != null) _exe.OpenPath(it.FullPath);
            }
            else
            {
                var c = lv.SelectedItems[0].Tag as Conflict;
                if (c != null && c.Items.Count > 0) _exe.OpenPath(c.Items[0].FullPath);
            }
        }

        private void ChangeDataDir()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = L.T("选择备份与回收区的存放位置");
                dlg.SelectedPath = _exe.DataDir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (string.Equals(dlg.SelectedPath, _exe.DataDir, StringComparison.OrdinalIgnoreCase)) return;

                var newExe = new Executor(_versions, dlg.SelectedPath);
                newExe.Log.Write(L.T("备份位置由 ") + _exe.DataDir + L.T(" 更改为 ") + newExe.DataDir);
                _exe.DataDir = newExe.DataDir;
                _exe.BackupDir = newExe.BackupDir;
                _exe.RecycleDir = newExe.RecycleDir;
                _exe.SessionsFile = newExe.SessionsFile;
                _exe.Log = newExe.Log;
                _statusData.Text = L.T("数据目录：") + _exe.DataDir;
                RefreshRecycle();
                Ui.Info(L.T("已切换"), L.T("新的备份位置：") + _exe.DataDir + L.T("\n\n原有备份仍留在旧位置，如需继续管理请手动迁移。"));
            }
        }

        private void OpenLog()
        {
            string file = Path.Combine(_exe.DataDir, "log.txt");
            if (!File.Exists(file)) { Ui.Info(L.T("还没有操作日志")); return; }
            _exe.OpenPath(file);
        }

        private void ShowAbout()
        {
            Ui.Info(L.T("AE 插件管理器"), string.Format(
                L.T("版本 1.0\n\n") +
                L.T("本机发现 {0} 个 AE 安装：\n{1}\n\n") +
                L.T("数据目录（备份 / 回收区 / 日志）：\n{2}\n\n") +
                L.T("按 Blender BLT 的使用思路设计：扫描纳管、冲突检测、拖入即装、可回滚。"),
                _versions.Count,
                string.Join("\n", _versions.ConvertAll(v => "  AE " + v.Version + "　" + v.Root).ToArray()),
                _exe.DataDir));
        }

        private void ShowSafety()
        {
            Ui.Info(L.T("安全说明"), 
                L.T("本软件对 AE 安装目录的写操作遵守三条硬规则：\n\n") +
                L.T("1. 只写入已发现的 AE 安装目录（Plug-ins / Scripts / Presets），\n") +
                L.T("   路径穿越与越界写入会被拒绝并在日志中记录。\n\n") +
                L.T("2. 任何覆盖前都先备份原文件到数据目录，安装失败会自动整体回滚。\n\n") +
                L.T("3. 没有\"删除\"功能：卸载是把文件移动到回收区，可一键还原。\n") +
                L.T("   唯一会真正删数据的是回收区里的\"彻底删除备份\"按钮，\n") +
                L.T("   且它只作用于本软件的备份区，不触碰 AE 目录。\n\n") +
                L.T("此外，本软件不会执行插件自带的 .exe/.msi 安装器——\n") +
                L.T("那类插件请在安装向导中手动安装，再用本软件纳管。\n\n") +
                L.T("数据目录：") + _exe.DataDir);
        }

        // ==================== 目录变更监听 ====================

        private void SetupWatcher()
        {
            _debounce = new Timer { Interval = 800 };
            _debounce.Tick += (s, e) => { _debounce.Stop(); ScanAll(); RefreshRecycle(); };

            try
            {
                _watcher = new FileSystemWatcher();
                foreach (var v in _versions)
                {
                    // 一个 watcher 只能盯一个目录，这里只盯第一个版本的核心目录，其余靠手动/F5 刷新
                    _watcher.Path = v.PluginsDir;
                    break;
                }
                _watcher.IncludeSubdirectories = true;
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
                _watcher.Created += OnFsChanged;
                _watcher.Deleted += OnFsChanged;
                _watcher.Renamed += OnFsChanged;
                _watcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        private void OnFsChanged(object sender, FileSystemEventArgs e)
        {
            if (_scanning || !IsHandleCreated) return;
            try { BeginInvoke(new MethodInvoker(() => { _debounce.Stop(); _debounce.Start(); })); }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { if (_watcher != null) _watcher.EnableRaisingEvents = false; } catch { }
            CleanTempQuietly();
            base.OnFormClosed(e);
        }
    }
}
