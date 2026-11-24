using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace PixelStacking.Data
{
    public enum Lang { Zh, En, Ja, Ko }

    /// <summary>
    /// 多語言（繁體中文／English／日本語／한국어）。
    /// 介面文字一律用 key 取字：<see cref="Get"/>／<see cref="Format"/>（編輯器接 ResourceDictionary，命令列版查內嵌的 Keys.tsv＋翻譯表），
    /// 取來的句子會記下 key 與參數，語言切換時 <see cref="Retranslate"/> 可以依同一個 key 重組成新語言。
    /// 程式外面來的中文「資料」（內建預設的名稱、部位名稱…）用 <see cref="T"/> 查翻譯表（完全相同才翻，找不到維持原文）。
    /// 翻譯表是內嵌資源 Resources\Strings.tsv（每行 zh ⇥ en ⇥ ja ⇥ ko），ResourceDictionary 由它產生（Tools\GenLang.ps1）。
    /// </summary>
    public static class Loc
    {
        private static readonly Dictionary<string, string[]> Exact = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static Lang _lang = Lang.Zh;

        /// <summary>語言改變後觸發（畫面上已經載入的元件要重新套用文字）。</summary>
        public static event Action LanguageChanged;

        /// <summary>每個語言在選單上的原生名稱（順序同 <see cref="Lang"/>）。</summary>
        public static readonly string[] NativeNames = { "繁體中文", "English", "日本語", "한국어" };

        /// <summary>存檔／環境變數用的代碼（順序同 <see cref="Lang"/>）。</summary>
        public static readonly string[] Codes = { "zh", "en", "ja", "ko" };

        static Loc()
        {
            LoadTable(null);
            _lang = Detect();
        }

        public static Lang Language
        {
            get => _lang;
            set
            {
                if (_lang == value) { return; }

                _lang = value;
                Generation++;
                LanguageChanged?.Invoke();
            }
        }

        /// <summary>語言換了幾次（每換一次加一）：用來判斷「記下來的句子」是不是已經是目前語言的版本。</summary>
        public static int Generation { get; private set; }

        // --- 依 key 取字（程式碼用；跟畫面無關，命令列版也用同一套）---------------------------------------------------
        /// <summary>
        /// 依 key 查目前語言的文字。編輯器啟動時換成「查 ResourceDictionary」（Controls\LangResources）；
        /// 沒有指定（命令列版、測試）就用內嵌的 Keys.tsv（key ⇥ 中文原文）加翻譯表查。回傳 null＝沒有這個 key。
        /// </summary>
        public static Func<string, string> KeyLookup;

        private sealed class Recipe { public string Key; public object[] Args; public int Generation; }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, Recipe> Recipes = new System.Runtime.CompilerServices.ConditionalWeakTable<string, Recipe>();
        private static Dictionary<string, string> _keyToZh;

        private static string DefaultLookup(string key)
        {
            if (_keyToZh == null)
            {
                Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
                Assembly asm = typeof(Loc).Assembly;
                string name = null;
                foreach (string n in asm.GetManifestResourceNames()) { if (n.EndsWith("Keys.tsv", StringComparison.OrdinalIgnoreCase)) { name = n; break; } }
                if (name != null)
                {
                    string text;
                    using (Stream s = asm.GetManifestResourceStream(name))
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8)) { text = r.ReadToEnd(); }
                    foreach (string raw in text.Split('\n'))
                    {
                        string line = raw.TrimEnd('\r');
                        int tab = line.IndexOf('\t');
                        if (tab > 0) { map[line.Substring(0, tab)] = Unescape(line.Substring(tab + 1)); }
                    }
                }
                _keyToZh = map;
            }

            string zh;
            if (!_keyToZh.TryGetValue(key, out zh)) { return null; }

            string[] tr;
            return Exact.TryGetValue(zh, out tr) ? tr[(int)_lang] : zh;
        }

        private static string Lookup(string key)
        {
            string s = null;
            Func<string, string> f = KeyLookup;
            if (f != null) { s = f(key); }
            return s ?? DefaultLookup(key);
        }

        private static void Remember(string text, string key, object[] args)
        {
            Recipe r;
            if (Recipes.TryGetValue(text, out r)) { return; }

            try { Recipes.Add(text, new Recipe { Key = key, Args = args, Generation = Generation }); }
            catch (ArgumentException) { }   // 同一個字串物件已經有人登記了
        }

        /// <summary>依 key 取目前語言的文字；找不到回傳 key 本身。結果會記下 key，語言切換時可以用 <see cref="Retranslate"/> 換成新語言。</summary>
        public static string Get(string key)
        {
            string s = Lookup(key);
            if (s == null) { return key; }

            Remember(s, key, null);
            return s;
        }

        /// <summary>依 key 取樣板（含 {0}、{1}…）並套入參數；結果會記下 key 與參數。</summary>
        public static string Format(string key, params object[] args)
        {
            string r = SafeFormat(Get(key), ArgTexts(args));
            if (args != null && args.Length > 0) { Remember(r, key, args); }
            return r;
        }

        private static string SafeFormat(string template, object[] args)
        {
            try { return string.Format(CultureInfo.CurrentCulture, template, args); }
            catch (FormatException) { return template; }
        }

        /// <summary>
        /// 套進樣板的參數：用 <see cref="Get"/>／<see cref="Format"/> 取來的字串換成目前語言；仍是中文原文的資料（視角名稱、部位名稱…）
        /// 走 <see cref="T"/>；數字等其他型別原樣。
        /// </summary>
        private static object[] ArgTexts(object[] args)
        {
            if (args == null) { return null; }

            object[] shown = new object[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] as string;
                shown[i] = a == null ? args[i] : (Retranslate(a) ?? T(a));
            }
            return shown;
        }

        /// <summary>
        /// 這句話如果是用 <see cref="Get"/>／<see cref="Format"/> 取來的，回傳換成「目前語言」的版本
        /// （已經是目前語言就原樣回傳同一個物件）；不是的話回傳 null（呼叫端改走 <see cref="T"/>）。
        /// </summary>
        public static string Retranslate(string s)
        {
            Recipe r;
            if (s == null || !Recipes.TryGetValue(s, out r)) { return null; }
            if (r.Generation == Generation) { return s; }

            string t = Lookup(r.Key);
            if (t == null) { return s; }
            if (r.Args == null) { return t; }

            return SafeFormat(t, ArgTexts(r.Args));
        }

        /// <summary>給訊息方塊、例外訊息用：取來的字串直接用，其餘（舊的中文原文、系統訊息）走 <see cref="T"/>。</summary>
        public static string Localize(string s)
        {
            return Retranslate(s) ?? T(s);
        }

        public static string Code(Lang l) { return Codes[(int)l]; }

        public static bool TryParse(string code, out Lang lang)
        {
            lang = Lang.Zh;
            if (string.IsNullOrWhiteSpace(code)) { return false; }

            code = code.Trim().ToLowerInvariant();
            for (int i = 0; i < Codes.Length; i++)
            {
                if (code == Codes[i] || code.StartsWith(Codes[i] + "-") || code.StartsWith(Codes[i] + "_")) { lang = (Lang)i; return true; }
            }
            return false;
        }

        /// <summary>第一次啟動用什麼語言：環境變數 PIXELSTACKING_LANG（測試用）→ 作業系統介面語言（中／日／韓，其他都用英文）。</summary>
        public static Lang Detect()
        {
            Lang l;
            if (TryParse(Environment.GetEnvironmentVariable("PIXELSTACKING_LANG"), out l)) { return l; }

            string two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (TryParse(two, out l)) { return l; }

            return Lang.En;
        }

        /// <summary>有沒有中日韓文字（含全形標點）。</summary>
        public static bool HasCjk(string s)
        {
            foreach (char c in s) { if ((c >= 0x3000 && c <= 0x9FFF) || (c >= 0xFF00 && c <= 0xFFEF) || (c >= 0xAC00 && c <= 0xD7AF)) { return true; } }
            return false;
        }

        // --- 對照表 ----------------------------------------------------------------------------------------------
        /// <summary>
        /// 載入對照表。path 是 null 就讀內嵌資源；測試可以指定外部 TSV 檔。
        /// 每行 zh ⇥ en ⇥ ja ⇥ ko，行內的換行寫成 \n、反斜線寫成 \；空行與 # 開頭的行略過。缺的語言退回英文。
        /// </summary>
        public static void LoadTable(string path)
        {
            Exact.Clear();
            _keyToZh = null;

            string text;
            if (path != null) { text = File.ReadAllText(path, Encoding.UTF8); }
            else
            {
                Assembly asm = typeof(Loc).Assembly;
                string name = null;
                foreach (string n in asm.GetManifestResourceNames()) { if (n.EndsWith("Strings.tsv", StringComparison.OrdinalIgnoreCase)) { name = n; break; } }
                if (name == null) { return; }

                using (Stream s = asm.GetManifestResourceStream(name))
                using (StreamReader r = new StreamReader(s, Encoding.UTF8)) { text = r.ReadToEnd(); }
            }

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#' || line[0] == '﻿' && line.Length == 1) { continue; }

                string[] cols = line.Split('\t');
                if (cols.Length < 2) { continue; }

                string zh = Unescape(cols[0]);
                string[] tr = new string[4];
                tr[0] = zh;
                tr[1] = Unescape(cols[1]);
                tr[2] = cols.Length > 2 && cols[2].Length > 0 ? Unescape(cols[2]) : tr[1];
                tr[3] = cols.Length > 3 && cols[3].Length > 0 ? Unescape(cols[3]) : tr[1];
                Exact[zh] = tr;
            }
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) { return s; }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char d = s[i + 1];
                    if (d == 'n') { sb.Append('\n'); i++; continue; }
                    if (d == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        /// <summary>對照表裡有幾句（測試用）。</summary>
        public static int KeyCount => Exact.Count;

        /// <summary>所有的 key 與四個語言的翻譯（測試用）。</summary>
        public static IEnumerable<KeyValuePair<string, string[]>> Entries => Exact;

        // --- 翻譯（只剩「資料」用：部位名稱、預設名稱這類程式外面來的中文）-------------------------------------------
        /// <summary>
        /// 把一句繁體中文「資料」翻成目前語言：只做完全相同比對（前後空白不算）；沒有就原樣回傳。
        /// 介面文字不走這裡——它們都用 key 取字（<see cref="Get"/>／<see cref="Format"/>）。
        /// </summary>
        public static string T(string s)
        {
            return Translate(s, _lang);
        }

        public static string Translate(string s, Lang lang)
        {
            if (lang == Lang.Zh || string.IsNullOrEmpty(s)) { return s; }

            string[] tr;
            if (Exact.TryGetValue(s, out tr)) { return tr[(int)lang]; }

            string trimmed = s.Trim();
            if (trimmed.Length != s.Length && trimmed.Length > 0 && Exact.TryGetValue(trimmed, out tr))
            {
                int lead = s.IndexOf(trimmed, StringComparison.Ordinal);
                return s.Substring(0, lead) + tr[(int)lang] + s.Substring(lead + trimmed.Length);
            }
            return s;
        }
    }
}
