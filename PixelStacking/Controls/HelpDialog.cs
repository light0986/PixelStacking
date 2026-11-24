using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 「說明 → 使用教學」視窗：左邊是頁籤（功能總覽、快速上手、骨架動畫教學、建議做法、快捷鍵、常見問題…），右邊是內容。
    /// 內容全部來自 <see cref="HelpContent"/>。不是強制回應的視窗（可以邊看邊操作主視窗），所以用 Show 開，同時只會開一個（見 MainWindow）。
    /// </summary>
    public sealed class HelpDialog : Window
    {
        private static readonly Brush Muted = Frozen(Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly Brush Accent = Frozen(Color.FromRgb(0x9C, 0xDC, 0xFE));
        private static readonly Brush TipBack = Frozen(Color.FromRgb(0x2F, 0x3A, 0x45));
        private static readonly Brush TipEdge = Frozen(Color.FromRgb(0x4F, 0x9D, 0xCC));

        private static Brush Frozen(Color c) { SolidColorBrush b = new SolidColorBrush(c); b.Freeze(); return b; }

        private readonly List<HelpPage> _pages = HelpContent.Pages();
        private readonly List<RadioButton> _tabs = new List<RadioButton>();
        private readonly StackPanel _body = new StackPanel { Margin = new Thickness(22, 14, 26, 24) };
        private readonly ScrollViewer _scroll;

        public int PageCount => _pages.Count;
        public int SelectedPage { get; private set; } = -1;

        public HelpDialog()
        {
            DialogStyle.Apply(this);
            Title = LangResources.Get(TextKey.Help_UserGuide);
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            Width = 940;
            Height = Math.Max(420, Math.Min(660, SystemParameters.WorkArea.Height - 60));

            StackPanel nav = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            Style tabStyle = Application.Current == null ? null : Application.Current.TryFindResource("TabRadio") as Style;
            for (int i = 0; i < _pages.Count; i++)
            {
                int index = i;
                RadioButton rb = new RadioButton { Content = _pages[i].Title, GroupName = "HelpPage", Margin = new Thickness(0, 0, 0, 2), Padding = new Thickness(8, 8, 8, 8), FontSize = 14 };
                if (tabStyle != null) { rb.Style = tabStyle; }
                AutomationProperties.SetAutomationId(rb, "HelpTab" + i);
                rb.Checked += (s, e) => ShowPage(index);
                nav.Children.Add(rb);
                _tabs.Add(rb);
            }

            Border navBorder = new Border
            {
                Width = 168,
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
                BorderThickness = new Thickness(0, 0, 1, 0),
                Child = nav,
            };

            _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body, Focusable = false };
            AutomationProperties.SetAutomationId(_scroll, "HelpContent");

            DockPanel root = new DockPanel();
            DockPanel.SetDock(navBorder, Dock.Left);
            root.Children.Add(navBorder);
            root.Children.Add(_scroll);
            Content = root;

            Loaded += (s, e) => { if (SelectedPage < 0) { _tabs[0].IsChecked = true; } };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape || e.Key == Key.F1) { Close(); e.Handled = true; } };
        }

        /// <summary>切到第 index 頁。</summary>
        public void ShowPage(int index)
        {
            if (index < 0 || index >= _pages.Count) { return; }

            SelectedPage = index;
            if (_tabs[index].IsChecked != true) { _tabs[index].IsChecked = true; return; }

            HelpPage page = _pages[index];
            _body.Children.Clear();
            _body.Children.Add(new TextBlock { Text = page.Title, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Accent, Margin = new Thickness(0, 0, 0, 8) });

            int step = 0;
            foreach (HelpBlock b in page.Blocks)
            {
                if (b.Kind != HelpKind.Step) { step = 0; }
                switch (b.Kind)
                {
                    case HelpKind.Heading:
                        _body.Children.Add(new TextBlock { Text = b.Text, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Accent, Margin = new Thickness(0, 16, 0, 4), TextWrapping = TextWrapping.Wrap });
                        break;
                    case HelpKind.Paragraph:
                        _body.Children.Add(new TextBlock { Text = b.Text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 4, 0, 4) });
                        break;
                    case HelpKind.Bullet:
                        _body.Children.Add(Row("•", b.Text, Muted));
                        break;
                    case HelpKind.Step:
                        step++;
                        _body.Children.Add(Row(step + ".", b.Text, Accent));
                        break;
                    case HelpKind.Tip:
                        _body.Children.Add(new Border
                        {
                            Background = TipBack,
                            BorderBrush = TipEdge,
                            BorderThickness = new Thickness(3, 0, 0, 0),
                            Padding = new Thickness(12, 8, 12, 8),
                            Margin = new Thickness(0, 10, 0, 6),
                            Child = new TextBlock { Text = b.Text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 21 },
                        });
                        break;
                }
            }
            _scroll.ScrollToTop();
        }

        private static UIElement Row(string mark, string text, Brush markBrush)
        {
            Grid g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = mark, Foreground = markBrush, FontSize = 13.5, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) });
            TextBlock t = new TextBlock { Text = text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        /// <summary>目前這一頁全部文字（測試用）。</summary>
        public string CurrentText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            Collect(_body, sb);
            return sb.ToString();
        }

        private static void Collect(DependencyObject d, System.Text.StringBuilder sb)
        {
            TextBlock tb = d as TextBlock;
            if (tb != null) { sb.AppendLine(tb.Text); return; }

            foreach (object c in LogicalTreeHelper.GetChildren(d))
            {
                DependencyObject dc = c as DependencyObject;
                if (dc != null) { Collect(dc, sb); }
            }
        }
    }
}
