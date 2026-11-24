using System;
using System.Windows;
using System.Windows.Controls;
using PixelStacking.Data;

namespace PixelStacking
{
    /// <summary>語言選單：繁體中文／English／日本語／한국어。選擇會記在 ui.txt（Lang.zh／Lang.en…），下次啟動沿用；第一次啟動跟著作業系統語言。</summary>
    public partial class MainWindow
    {
        private const string LangKeyPrefix = "Lang.";

        /// <summary>啟動時：環境變數 PIXELSTACKING_LANG（測試用）優先，其次是上次選的，最後是作業系統語言（Loc 的預設）。</summary>
        private void LoadSavedLanguage()
        {
            Lang l;
            if (Loc.TryParse(Environment.GetEnvironmentVariable("PIXELSTACKING_LANG"), out l)) { Loc.Language = l; return; }

            foreach (var kv in UiState.Load())
            {
                if (kv.Value && kv.Key.StartsWith(LangKeyPrefix) && Loc.TryParse(kv.Key.Substring(LangKeyPrefix.Length), out l)) { Loc.Language = l; return; }
            }
        }

        private void UpdateLanguageChecks()
        {
            LangMenuZh.IsChecked = Loc.Language == Lang.Zh;
            LangMenuEn.IsChecked = Loc.Language == Lang.En;
            LangMenuJa.IsChecked = Loc.Language == Lang.Ja;
            LangMenuKo.IsChecked = Loc.Language == Lang.Ko;
        }

        private void Language_Click(object sender, RoutedEventArgs e)
        {
            Lang l;
            if (!Loc.TryParse((string)((MenuItem)sender).Tag, out l)) { return; }

            SetLanguage(l, true);
        }

        /// <summary>切換語言：畫面上所有文字立刻換掉（Loc 會通知 LocHook 重新套用），選擇存進 ui.txt。</summary>
        internal void SetLanguage(Lang l, bool save)
        {
            Loc.Language = l;
            UpdateLanguageChecks();
            if (!save) { return; }

            var map = UiState.Load();
            foreach (Lang each in new[] { Lang.Zh, Lang.En, Lang.Ja, Lang.Ko }) { map[LangKeyPrefix + Loc.Code(each)] = each == l; }
            UiState.Save(map);
        }
    }
}
