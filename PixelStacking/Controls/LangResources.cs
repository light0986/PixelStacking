using System;
using System.Windows;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 介面文字的 ResourceDictionary：每一句話一個 key（Resources\Lang\Strings.xx.xaml，由 Tools\GenLang.ps1 產生），
    /// XAML 用 <c>Header="{DynamicResource Menu_File}"</c> 取用，程式碼用 <see cref="Get"/>／<see cref="Format"/>。
    /// 繁體中文那份在 App.xaml 合併（預設，也是找不到 key 時的備援）；選別的語言時，把那一種語言的字典疊在最上面
    /// （後加的優先），語言再切換就換掉它，WPF 會自動更新所有 DynamicResource 的地方。
    ///
    /// 依 key 取字、記住「是哪個 key 帶哪些參數」、語言切換時重組句子，這些都在 <see cref="Loc"/>（跟 WPF 無關，命令列版共用）；
    /// 這裡只負責把 <see cref="Loc.KeyLookup"/> 接到 ResourceDictionary，並保留舊的呼叫入口（LangResources.Get／Format）。
    /// </summary>
    public static class LangResources
    {
        private static bool _installed;
        private static ResourceDictionary _overlay;

        /// <summary>安裝（可重複呼叫）：套用目前語言，之後語言一變就換字典，並讓 Loc 依 key 查 ResourceDictionary。</summary>
        public static void Install()
        {
            if (_installed) { return; }

            _installed = true;
            Loc.KeyLookup = key => { Application app = Application.Current; return app == null ? null : app.TryFindResource(key) as string; };
            Loc.LanguageChanged += () => Apply(Loc.Language);
            Apply(Loc.Language);
        }

        private static void Apply(Lang lang)
        {
            Application app = Application.Current;
            if (app == null) { return; }

            ResourceDictionary old = _overlay;
            _overlay = null;
            if (lang != Lang.Zh)
            {
                _overlay = new ResourceDictionary { Source = new Uri("/PixelStacking;component/Resources/Lang/Strings." + Loc.Code(lang) + ".xaml", UriKind.Relative) };
            }

            // 先加新的再拿掉舊的：中間不會有一刻只剩中文
            if (_overlay != null) { app.Resources.MergedDictionaries.Add(_overlay); }
            if (old != null) { app.Resources.MergedDictionaries.Remove(old); }
        }

        public static string Get(string key) { return Loc.Get(key); }

        public static string Format(string key, params object[] args) { return Loc.Format(key, args); }

        public static string Retranslate(string s) { return Loc.Retranslate(s); }

        public static string Localize(string s) { return Loc.Localize(s); }
    }
}
