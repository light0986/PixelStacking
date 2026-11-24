using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PixelStacking.Data
{
    /// <summary>
    /// 記住介面小狀態（目前只有左欄各大類的收合／展開），存成 %AppData%\PixelStacking\ui.txt，
    /// 每行「名稱=1」或「名稱=0」。讀寫失敗（沒權限、檔案壞掉）一律當作沒有記錄，不影響程式使用。
    /// 環境變數 PIXELSTACKING_UI_STATE 可指定別的檔案路徑（自動化測試用，避免動到使用者真正的設定）。
    /// </summary>
    public static class UiState
    {
        public static string FilePath
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("PIXELSTACKING_UI_STATE");
                if (!string.IsNullOrEmpty(over)) { return over; }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PixelStacking", "ui.txt");
            }
        }

        /// <summary>讀出所有記錄；檔案不存在或壞掉就回傳空字典。</summary>
        public static Dictionary<string, bool> Load()
        {
            var map = new Dictionary<string, bool>();
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) { return map; }
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) { continue; }
                    string v = line.Substring(eq + 1).Trim();
                    if (v == "1" || v == "0") { map[line.Substring(0, eq).Trim()] = v == "1"; }
                }
            }
            catch (Exception) { map.Clear(); }
            return map;
        }

        /// <summary>整份寫回（資料只有幾行）。失敗就算了。</summary>
        public static void Save(IDictionary<string, bool> map)
        {
            try
            {
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
                var sb = new StringBuilder();
                foreach (var kv in map) { sb.Append(kv.Key).Append('=').Append(kv.Value ? '1' : '0').Append("\r\n"); }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }
    }
}
