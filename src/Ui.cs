using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace AePluginManager
{
    internal static class Ui
    {
        public static Font BaseFont = new Font("Microsoft YaHei UI", 9F);
        public static Font TitleFont = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
        public static Font MonoFont = new Font("Consolas", 9F);

        /// <summary>中文字体回退：优先微软雅黑，其次宋体，最后系统默认。</summary>
        public static void EnsureFonts()
        {
            if (HasFont("Microsoft YaHei UI")) return;
            string family = HasFont("Microsoft YaHei") ? "Microsoft YaHei" : (HasFont("SimSun") ? "SimSun" : null);
            if (family == null) return;
            BaseFont = new Font(family, 9F);
            TitleFont = new Font(family, 10.5F, FontStyle.Bold);
        }

        private static bool HasFont(string name)
        {
            try
            {
                using (var collection = new InstalledFontCollection())
                    foreach (var f in collection.Families)
                        if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        public static void Info(string text) { Msg(text, MessageBoxIcon.Information); }
        public static void Warn(string text) { Msg(text, MessageBoxIcon.Warning); }
        public static void Error(string text) { Msg(text, MessageBoxIcon.Error); }

        public static void Info(string title, string text) { Msg(title, text, MessageBoxIcon.Information); }
        public static void Warn(string title, string text) { Msg(title, text, MessageBoxIcon.Warning); }
        public static void Error(string title, string text) { Msg(title, text, MessageBoxIcon.Error); }

        private static void Msg(string text, MessageBoxIcon icon) { Msg(L.T("AE 插件管理器"), text, icon); }

        private static void Msg(string title, string text, MessageBoxIcon icon)
        {
            MessageBox.Show(text, title, MessageBoxButtons.OK, icon);
        }

        public static bool Confirm(string text)
        {
            return MessageBox.Show(text, L.T("AE 插件管理器"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        }

        public static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = TitleFont, Margin = new Padding(3, 6, 3, 3) };
        }

        public static void StyleGrid(ListView lv)
        {
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.GridLines = false;
            lv.MultiSelect = true;
            lv.HideSelection = false;
            lv.Dock = DockStyle.Fill;
            lv.Font = BaseFont;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        }

        /// <summary>按比例分配列宽，窗口缩放时调用。</summary>
        public static void SizeColumns(ListView lv, params double[] ratios)
        {
            if (lv.Columns.Count != ratios.Length || lv.ClientSize.Width <= 0) return;
            int total = lv.ClientSize.Width - 4;
            for (int i = 0; i < ratios.Length; i++)
                lv.Columns[i].Width = Math.Max(50, (int)(total * ratios[i]));
        }

        public static string LevelText(ConflictLevel level)
        {
            switch (level)
            {
                case ConflictLevel.High: return L.T("严重");
                case ConflictLevel.Medium: return L.T("注意");
                default: return L.T("提示");
            }
        }

        public static Color LevelColor(ConflictLevel level)
        {
            switch (level)
            {
                case ConflictLevel.High: return Color.FromArgb(192, 40, 40);
                case ConflictLevel.Medium: return Color.FromArgb(200, 120, 0);
                default: return Color.FromArgb(90, 90, 90);
            }
        }
    }
}
