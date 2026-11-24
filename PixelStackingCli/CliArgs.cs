using System;
using System.Collections.Generic;
using System.Globalization;

namespace PixelStacking.Cli
{
    /// <summary>命令列錯誤：訊息直接印給使用者（AI），code 是結束代碼（1 = 執行失敗，2 = 用法錯誤）。</summary>
    internal sealed class CliError : Exception
    {
        public readonly int Code;
        public CliError(string message, int code = 1) : base(message) { Code = code; }
    }

    /// <summary>
    /// 命令列參數：位置參數（Pos）與 --選項。「--名稱 值」帶一個值，下面 Flags 裡的是不帶值的開關。
    /// 負數（-3）不是選項，只有 "--" 開頭才是。每個選項被讀過就記下來，執行完用 <see cref="CheckUnused"/> 抓出打錯的選項。
    /// </summary>
    internal sealed class CliArgs
    {
        private static readonly HashSet<string> Flags = new HashSet<string>
        {
            "hollow", "no-shade", "no-trim", "same-colour", "diagonal", "delete", "force", "all-frames", "keep",
            "to-layer", "overwrite-transparent", "loop", "no-loop", "quiet", "top-first", "reverse-all",
            "outline", "no-silhouette", "no-creases",
        };

        public readonly List<string> Pos = new List<string>();
        private readonly Dictionary<string, List<string>> _opt = new Dictionary<string, List<string>>();
        private readonly HashSet<string> _used = new HashSet<string>();

        public static CliArgs Parse(IList<string> tokens, int start)
        {
            CliArgs a = new CliArgs();
            for (int i = start; i < tokens.Count; i++)
            {
                string t = tokens[i];
                if (t.Length > 2 && t.StartsWith("--", StringComparison.Ordinal))
                {
                    string name = t.Substring(2);
                    string value = null;
                    int eq = name.IndexOf('=');
                    if (eq > 0) { value = name.Substring(eq + 1); name = name.Substring(0, eq); }

                    name = name.ToLowerInvariant();
                    if (name == "color") { name = "colour"; }
                    if (name == "same-color") { name = "same-colour"; }

                    if (value == null && !Flags.Contains(name))
                    {
                        if (i + 1 >= tokens.Count) { throw new CliError("option --" + name + " needs a value", 2); }

                        value = tokens[++i];
                        // PowerShell turns an unquoted 5,0,0 into three separate arguments: glue the following numbers back on
                        if (name == "move" || name == "body" || name == "offset")
                        {
                            double dummy;
                            while (i + 1 < tokens.Count && double.TryParse(tokens[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out dummy)) { value += "," + tokens[++i]; }
                        }
                    }
                    List<string> list;
                    if (!a._opt.TryGetValue(name, out list)) { a._opt[name] = list = new List<string>(); }

                    list.Add(value ?? "true");
                }
                else { a.Pos.Add(t); }
            }
            return a;
        }

        public bool Has(string name) { _used.Add(name); return _opt.ContainsKey(name); }

        public string Str(string name, string def = null)
        {
            _used.Add(name);
            List<string> l;
            return _opt.TryGetValue(name, out l) ? l[l.Count - 1] : def;
        }

        public List<string> All(string name)
        {
            _used.Add(name);
            List<string> l;
            return _opt.TryGetValue(name, out l) ? l : new List<string>();
        }

        public int Int(string name, int def)
        {
            string s = Str(name);
            return s == null ? def : ParseInt(s, "--" + name);
        }

        public double Dbl(string name, double def)
        {
            string s = Str(name);
            return s == null ? def : ParseDouble(s, "--" + name);
        }

        public string PosStr(int i, string what)
        {
            if (i >= Pos.Count) { throw new CliError("missing argument: " + what, 2); }

            return Pos[i];
        }

        public int PosInt(int i, string what) { return ParseInt(PosStr(i, what), what); }

        public double PosDbl(int i, string what) { return ParseDouble(PosStr(i, what), what); }

        public void NeedExactly(int n, string usage)
        {
            if (Pos.Count < n) { throw new CliError("missing arguments. usage: " + usage, 2); }
            if (Pos.Count > n) { throw new CliError("too many arguments (" + string.Join(" ", Pos.GetRange(n, Pos.Count - n)) + "). usage: " + usage, 2); }
        }

        public void CheckUnused()
        {
            List<string> bad = new List<string>();
            foreach (string k in _opt.Keys) { if (!_used.Contains(k)) { bad.Add("--" + k); } }
            if (bad.Count > 0) { throw new CliError("unknown option for this command: " + string.Join(", ", bad), 2); }
        }

        public static int ParseInt(string s, string what)
        {
            int v;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) { throw new CliError(what + " must be an integer, got '" + s + "'", 2); }

            return v;
        }

        public static double ParseDouble(string s, string what)
        {
            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || double.IsNaN(v) || double.IsInfinity(v)) { throw new CliError(what + " must be a number, got '" + s + "'", 2); }

            return v;
        }

        /// <summary>"1,2,3" 或 "1 2 3" 這種多個整數；數量不對丟錯。</summary>
        public static int[] ParseInts(string s, int count, string what)
        {
            string[] parts = s.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != count) { throw new CliError(what + " needs " + count + " integers like 1,2,3, got '" + s + "'", 2); }

            int[] r = new int[count];
            for (int i = 0; i < count; i++) { r[i] = ParseInt(parts[i], what); }
            return r;
        }
    }
}
