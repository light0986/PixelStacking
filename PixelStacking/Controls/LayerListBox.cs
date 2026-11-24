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
    /// 圖層清單：最上層排在最上面（跟一般繪圖軟體一樣）。每一列有「顯示／隱藏」勾選與名稱，點一列 = 切換成目前編輯的圖層，
    /// 連點兩下 = 要求改名。只負責顯示與回報操作，圖層資料由呼叫端（<see cref="Animation"/>）改。
    /// </summary>
    public sealed class LayerListBox : ScrollViewer
    {
        private static readonly Brush NormalBack = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
        private static readonly Brush ActiveBack = Freeze(new SolidColorBrush(Color.FromRgb(0x09, 0x47, 0x71)));
        private static readonly Brush NormalBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)));
        private static readonly Brush ActiveBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x37, 0x94, 0xFF)));
        private static readonly Brush NameBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)));
        private static readonly Brush HiddenBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)));

        private readonly StackPanel _panel = new StackPanel();
        private int _active;
        private int _count;

        /// <summary>使用者點了某一列（參數 = 圖層索引，0 = 最底層）。</summary>
        public event Action<int> LayerSelected;

        /// <summary>使用者改了某個圖層的「顯示」勾選。</summary>
        public event Action<int, bool> VisibilityToggled;

        /// <summary>使用者連點兩下某一列，想改名。</summary>
        public event Action<int> RenameRequested;

        public LayerListBox()
        {
            Content = _panel;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        public int ActiveIndex => _active;
        public int Count => _count;

        /// <summary>重建清單。<paramref name="layers"/> 第 0 個是最底層；<paramref name="active"/> 是目前編輯的圖層索引。</summary>
        public void Set(IReadOnlyList<Layer> layers, int active)
        {
            _panel.Children.Clear();
            _count = layers.Count;
            _active = Math.Max(0, Math.Min(active, layers.Count - 1));
            for (int k = layers.Count - 1; k >= 0; k--)
            {
                int index = k;
                Layer layer = layers[k];
                CheckBox eye = new CheckBox { IsChecked = layer.Visible, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) };
                eye.SetResourceReference(FrameworkElement.ToolTipProperty, TextKey.LayerListBox_ShowHideThisLayerHidden);   // 清單常駐：用資源參照，語言切換時提示會跟著換
                AutomationProperties.SetAutomationId(eye, "LayerEye" + k);
                eye.Click += (s, e) => { VisibilityToggled?.Invoke(index, eye.IsChecked == true); e.Handled = true; };
                TextBlock name = new TextBlock { Text = layer.Name, VerticalAlignment = VerticalAlignment.Center, Foreground = layer.Visible ? NameBrush : HiddenBrush, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(eye);
                Grid.SetColumn(name, 1); row.Children.Add(name);
                Border box = new Border
                {
                    Child = row,
                    Padding = new Thickness(4, 3, 4, 3),
                    Margin = new Thickness(0, 0, 0, 2),
                    BorderThickness = new Thickness(1),
                    BorderBrush = k == _active ? ActiveBorder : NormalBorder,
                    Background = k == _active ? ActiveBack : NormalBack,
                };
                box.SetResourceReference(FrameworkElement.ToolTipProperty, TextKey.LayerListBox_ClickEditThisLayerDouble);
                AutomationProperties.SetAutomationId(box, "LayerRow" + k);
                AutomationProperties.SetName(box, layer.Name);
                box.MouseLeftButtonDown += (s, e) =>
                {
                    if (e.ClickCount >= 2) { RenameRequested?.Invoke(index); }
                    else { LayerSelected?.Invoke(index); }
                    e.Handled = true;
                };
                _panel.Children.Add(box);
            }
        }
    }
}
