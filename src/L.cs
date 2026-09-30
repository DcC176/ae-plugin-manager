// 本地化运行时：中文为源码主语言，英文通过查表得到。
// 设计取舍：不重写所有构造逻辑，只在"字符串成型处"（赋给控件、弹窗、报告）查表翻译，
// 这样动态拼接出来的完整句子也能整体命中词条。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AePluginManager
{
    internal static class L
    {
        public static bool IsEn;

        private static Dictionary<string, string> _map;

        /// <summary>由 Main 在最早期调用：先看命令行 --lang，再看偏好文件，最后看系统界面语言。</summary>
        public static void Init(string[] args)
        {
            string forced = null;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--lang") forced = args[i + 1];

            if (string.Equals(forced, "en", StringComparison.OrdinalIgnoreCase)) { IsEn = true; return; }
            if (string.Equals(forced, "zh", StringComparison.OrdinalIgnoreCase)) { IsEn = false; return; }

            string saved = ReadPreference();
            if (string.Equals(saved, "en", StringComparison.OrdinalIgnoreCase)) { IsEn = true; return; }
            if (string.Equals(saved, "zh", StringComparison.OrdinalIgnoreCase)) { IsEn = false; return; }

            try
            {
                var culture = CultureInfo.CurrentUICulture;
                IsEn = !(culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase));
            }
            catch { IsEn = false; }
        }

        /// <summary>语言偏好写在 %APPDATA%\AEPluginManager\lang.txt，不碰 AE 目录。</summary>
        private static string PreferenceFile()
        {
            return Path.Combine(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AEPluginManager"),
                "lang.txt");
        }

        private static string ReadPreference()
        {
            try
            {
                string file = PreferenceFile();
                return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
            }
            catch { return null; }
        }

        public static void SavePreference(string lang)
        {
            string file = PreferenceFile();
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, lang, new UTF8Encoding(false));
        }

        /// <summary>英文单复数：只有 1 个时用单数，避免出现 "1 files"。</summary>
        public static string P(int count, string singular, string plural)
        {
            return count == 1 ? singular : plural;
        }

        /// <summary>翻译一条已成型的中文文案；英文词条缺失时原样返回（并在自检里能查出来）。</summary>
        public static string T(string zh)
        {
            if (!IsEn || string.IsNullOrEmpty(zh)) return zh;
            string en;
            return Map.TryGetValue(zh, out en) ? en : zh;
        }

        /// <summary>供组合式文案使用：先按中文格式化，再整体查表（词条键为含 {0} 的模板）。</summary>
        public static string F(string zhFormat, params object[] args)
        {
            string s = string.Format(zhFormat, args);
            if (!IsEn) return s;
            string en;
            if (Map.TryGetValue(zhFormat, out en))
            {
                try { return string.Format(en, args); }
                catch { return en; }
            }
            return s;
        }

        public static Dictionary<string, string> Map
        {
            get
            {
                if (_map == null) _map = Strings.All();
                return _map;
            }
        }

        /// <summary>自检用：统计有多少条英文词条能对上中文模板里的格式占位符。</summary>
        public static int Count { get { return Map.Count; } }
    }
}
