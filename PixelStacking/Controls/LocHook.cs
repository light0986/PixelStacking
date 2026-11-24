using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 把 <see cref="Loc"/> 接到畫面上，程式與 XAML 都不用改文字：任何元件載入時（含選單、對話框、說明視窗、清單列…）
    /// 翻譯它的文字（TextBlock／Run、按鈕與選項的內容、選單與 Expander 的標題、提示、視窗標題、下拉清單的項目），
    /// 並監聽之後的文字變動（狀態列、提示訊息是程式隨時改的）；語言切換時從記下的中文原文重新套用。
    /// </summary>
    public static class LocHook
    {
        private sealed class Entry { public string Original; public string Applied; public DependencyProperty Property; }

        private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, Entry>> Entries = new ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, Entry>>();
        private static readonly ConditionalWeakTable<ItemsControl, string[]> ItemOriginals = new ConditionalWeakTable<ItemsControl, string[]>();
        private static readonly List<WeakReference<DependencyObject>> Live = new List<WeakReference<DependencyObject>>();
        private static readonly List<WeakReference<ItemsControl>> LiveItems = new List<WeakReference<ItemsControl>>();
        private static bool _installed;
        private static bool _applying;

        /// <summary>安裝（可重複呼叫）：註冊「任何元件載入就翻譯」與「語言改變就全部重套」。</summary>
        public static void Install()
        {
            if (_installed) { return; }

            _installed = true;
            LangResources.Install();
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
            Loc.LanguageChanged += ReapplyAll;
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            FrameworkElement fe = sender as FrameworkElement;
            if (fe == null) { return; }

            Window w = fe as Window;
            if (w != null) { Watch(w); ApplyDeep(w); }
            else { Apply(fe); }
        }

        private static readonly ConditionalWeakTable<Window, object> Watched = new ConditionalWeakTable<Window, object>();

        /// <summary>
        /// 盯住一個視窗：視窗載入之後才動態加進去的元件（說明頁、清單列、縮圖列…）WPF 不會對它們發 Loaded
        /// （沒人掛 Loaded 處理常式的元件，WPF 就不廣播），所以版面一更新就約 0.12 秒後整個視窗再掃一遍；
        /// 已經翻過的元件在 <see cref="Track"/> 裡直接略過，掃描很便宜，沒變化就不會再觸發版面。
        /// </summary>
        public static void Watch(Window w)
        {
            if (w == null) { return; }

            object marker;
            if (Watched.TryGetValue(w, out marker)) { return; }

            Watched.Add(w, new object());
            System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            bool dirty = false;
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                if (!dirty) { return; }

                dirty = false;
                ApplyDeep(w);
            };
            w.LayoutUpdated += (s, e) => { dirty = true; if (!timer.IsEnabled) { timer.Start(); } };
            w.Closed += (s, e) => timer.Stop();
        }

        /// <summary>翻譯這個元件以及它邏輯樹底下的所有東西（視窗載入時、動態加了一批東西之後可以呼叫）。</summary>
        public static void ApplyDeep(DependencyObject root)
        {
            try { Apply(root); }
            catch (InvalidOperationException) { }   // 翻某一個元件失敗（例如集合在翻的時候被改了）不能害整批翻不完

            // 先把子元素抄成清單再走：翻譯時可能改到集合（例如下拉清單換項目），直接列舉會丟「集合已修改」
            List<DependencyObject> children = new List<DependencyObject>();
            try
            {
                foreach (object child in LogicalTreeHelper.GetChildren(root))
                {
                    DependencyObject d = child as DependencyObject;
                    if (d != null) { children.Add(d); }
                }
            }
            catch (InvalidOperationException) { }

            foreach (DependencyObject d in children) { ApplyDeep(d); }
        }

        private static void Apply(DependencyObject o)
        {
            TextBlock tb = o as TextBlock;
            if (tb != null)
            {
                Track(tb, TextBlock.TextProperty);

                // Text 是直接設的（本機值）時，Inlines 裡只有 WPF 自動包的那一個 Run，文字已經由上面負責；
                // 再把它當獨立文字追蹤，會把「已翻好的文字」記成原文，切回中文時反而蓋回英文。只有用 <Run>／<Span> 排版的才逐段處理。
                if (DependencyPropertyHelper.GetValueSource(tb, TextBlock.TextProperty).BaseValueSource != BaseValueSource.Local)
                {
                    foreach (Inline inl in tb.Inlines) { ApplyInline(inl); }
                }
            }

            Run run = o as Run;
            if (run != null) { Track(run, Run.TextProperty); }

            if (o is ContentControl) { Track(o, ContentControl.ContentProperty); }
            if (o is HeaderedItemsControl) { Track(o, HeaderedItemsControl.HeaderProperty); }
            if (o is HeaderedContentControl) { Track(o, HeaderedContentControl.HeaderProperty); }
            if (o is Window) { Track(o, Window.TitleProperty); }

            FrameworkElement fe = o as FrameworkElement;
            if (fe != null && fe.ToolTip is string) { Track(fe, FrameworkElement.ToolTipProperty); }

            ItemsControl ic = o as ItemsControl;
            if (ic != null && !(ic is MenuItem) && ic.ItemsSource == null) { ApplyItems(ic); }
        }

        private static void ApplyInline(Inline inl)
        {
            Run r = inl as Run;
            if (r != null) { Track(r, Run.TextProperty); }

            Span span = inl as Span;
            if (span != null) { foreach (Inline child in span.Inlines) { ApplyInline(child); } }
        }

        private static void Track(DependencyObject o, DependencyProperty dp)
        {
            string cur = o.GetValue(dp) as string;
            Dictionary<DependencyProperty, Entry> map = Entries.GetOrCreateValue(o);
            Entry entry;
            if (DependencyPropertyHelper.GetValueSource(o, dp).IsExpression) { return; }   // 綁定來的文字（例如對話框標題列）由來源負責，不能用本機值蓋掉綁定

            if (!map.TryGetValue(dp, out entry))
            {
                entry = new Entry { Property = dp };
                if (map.Count == 0) { lock (Live) { Live.Add(new WeakReference<DependencyObject>(o)); } }
                map[dp] = entry;
                DependencyPropertyDescriptor desc = DependencyPropertyDescriptor.FromProperty(dp, o.GetType());
                if (desc != null) { desc.AddValueChanged(o, (s, e) => OnChanged(o, dp)); }
            }
            if (cur != null && cur != entry.Applied) { entry.Original = cur; Push(o, entry); }
        }

        private static void OnChanged(DependencyObject o, DependencyProperty dp)
        {
            if (_applying) { return; }

            string cur = o.GetValue(dp) as string;
            Dictionary<DependencyProperty, Entry> map;
            Entry entry;
            if (cur == null || !Entries.TryGetValue(o, out map) || !map.TryGetValue(dp, out entry) || cur == entry.Applied) { return; }

            entry.Original = cur;   // 程式把文字改成新的（中文原文）：記成新的原文再翻譯
            Push(o, entry);
        }

        private static void Push(DependencyObject o, Entry entry)
        {
            if (DependencyPropertyHelper.GetValueSource(o, entry.Property).IsExpression) { return; }

            // 程式碼用 LangResources.Get／Format 取來的句子：依同一個 key 與參數重組成目前語言；其餘（舊的中文原文）走 Loc.T
            string t = Translated(entry.Original);
            entry.Applied = t;
            if (t == (o.GetValue(entry.Property) as string)) { return; }

            _applying = true;
            try { o.SetValue(entry.Property, t); }
            finally { _applying = false; }
        }

        /// <summary>
        /// 這句話現在該顯示的版本：程式碼用 Loc.Get／Format 取來的句子依 key 與參數重組成目前語言；
        /// 其餘是程式外面來的中文資料（內建預設、部位名稱…），查翻譯表（完全相同才翻，找不到維持原樣）。
        /// </summary>
        private static string Translated(string original)
        {
            return Loc.Retranslate(original) ?? Loc.T(original);
        }

        private static void ApplyItems(ItemsControl ic)
        {
            string[] originals;
            if (!ItemOriginals.TryGetValue(ic, out originals))
            {
                originals = new string[0];
                ItemOriginals.Add(ic, originals);
                lock (LiveItems) { LiveItems.Add(new WeakReference<ItemsControl>(ic)); }
            }

            // 項目是 ComboBoxItem 等容器：當成內容追蹤（就算彈出清單還沒打開也要翻，關著的下拉框顯示的就是它）
            foreach (object item in ic.Items)
            {
                if (item is ContentControl) { Track((DependencyObject)item, ContentControl.ContentProperty); }
            }

            // 項目是字串：換成翻譯後的字串（保留選取的位置）；原文記在 ItemOriginals
            bool anyString = false;
            foreach (object item in ic.Items) { if (item is string) { anyString = true; break; } }
            if (!anyString) { return; }

            string[] now = new string[ic.Items.Count];
            for (int i = 0; i < now.Length; i++) { now[i] = ic.Items[i] as string; }
            string[] orig = new string[now.Length];
            for (int i = 0; i < now.Length; i++)
            {
                // 已經是翻過的字串就用記下來的原文，不然現在的字串就是原文
                orig[i] = i < originals.Length && originals[i] != null && Translated(originals[i]) == now[i] ? originals[i] : now[i];
            }
            ItemOriginals.Remove(ic);
            ItemOriginals.Add(ic, orig);

            Selector sel = ic as Selector;
            int selected = sel == null ? -1 : sel.SelectedIndex;
            _applying = true;
            try
            {
                for (int i = 0; i < now.Length; i++)
                {
                    if (orig[i] == null) { continue; }

                    string t = Translated(orig[i]);
                    if (t != now[i]) { ic.Items[i] = t; }
                }
                if (sel != null && selected >= 0 && sel.SelectedIndex != selected) { sel.SelectedIndex = selected; }
            }
            finally { _applying = false; }
        }

        private static void ReapplyAll()
        {
            List<DependencyObject> alive = new List<DependencyObject>();
            lock (Live)
            {
                Live.RemoveAll(w => { DependencyObject d; return !w.TryGetTarget(out d); });
                foreach (WeakReference<DependencyObject> w in Live) { DependencyObject d; if (w.TryGetTarget(out d)) { alive.Add(d); } }
            }
            foreach (DependencyObject d in alive)
            {
                Dictionary<DependencyProperty, Entry> map;
                if (!Entries.TryGetValue(d, out map)) { continue; }

                foreach (Entry e in new List<Entry>(map.Values))
                {
                    if (e.Original != null) { Push(d, e); }
                }
            }

            List<ItemsControl> items = new List<ItemsControl>();
            lock (LiveItems)
            {
                LiveItems.RemoveAll(w => { ItemsControl d; return !w.TryGetTarget(out d); });
                foreach (WeakReference<ItemsControl> w in LiveItems) { ItemsControl d; if (w.TryGetTarget(out d)) { items.Add(d); } }
            }
            foreach (ItemsControl ic in items) { ApplyItems(ic); }
        }
    }
}
