using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>「新增」對話框：選長 (X)、寬 (Y)、高 (層數)，各 1~128，可從常用大小挑或直接輸入。</summary>
    public sealed class NewDialog : Window
    {
        public const int MinSize = 1, MaxSize = 128;
        private static readonly int[] Presets = { 8, 16, 24, 32, 48, 64, 96, 128 };

        private readonly ComboBox _x = Combo(), _y = Combo(), _z = Combo();
        private readonly TextBlock _info = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 340, MinHeight = 34 };
        private readonly TextBlock _error = new TextBlock { Foreground = Brushes.OrangeRed, Margin = new Thickness(0, 4, 0, 0), MinHeight = 18 };

        /// <summary>按確定後的結果：長 (X)、寬 (Y)、高（層數）。</summary>
        public int SizeX { get; private set; }
        public int SizeY { get; private set; }
        public int SizeZ { get; private set; }

        public NewDialog(int x, int y, int z)
        {
            DialogStyle.Apply(this);
            Title = LangResources.Get(TextKey.New_New);
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

            _x.Text = x.ToString();
            _y.Text = y.ToString();
            _z.Text = z.ToString();

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(Label(LangResources.Get(TextKey.New_LengthX), LangResources.Get(TextKey.New_HorizontalCellsOfTheCanvas)));
            row.Children.Add(_x);
            row.Children.Add(Label(LangResources.Get(TextKey.New_WidthY), LangResources.Get(TextKey.New_VerticalCellsOfTheCanvas)));
            row.Children.Add(_y);
            row.Children.Add(Label(LangResources.Get(TextKey.New_HeightSlices), LangResources.Get(TextKey.New_NumberOfSlicesStackedUpward)));
            row.Children.Add(_z);

            Button ok = new Button { Content = LangResources.Get(TextKey.New_Create), Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            Button cancel = new Button { Content = LangResources.Get(TextKey.Common_Cancel), Width = 80, IsCancel = true };
            ok.Click += Ok_Click;
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            StackPanel root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock { Text = LangResources.Format(TextKey.NewDialog_Prompt, MinSize, MaxSize) });
            root.Children.Add(row);
            root.Children.Add(_info);
            root.Children.Add(_error);
            root.Children.Add(new TextBlock
            {
                Text = LangResources.Get(TextKey.New_WhatIsOpenNowIs),
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
            });
            root.Children.Add(buttons);
            Content = root;

            foreach (ComboBox box in new[] { _x, _y, _z })
            {
                box.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => UpdateInfo()));
                box.SelectionChanged += (s, e) => UpdateInfo();
            }
            Loaded += (s, e) => { UpdateInfo(); _x.Focus(); };
        }

        /// <summary>讀取並驗證輸入；不合法時回傳 false 並給出原因。</summary>
        public bool TryRead(out int x, out int y, out int z, out string error)
        {
            error = null;
            y = 0; z = 0;
            if (!TryReadSize(_x, out x) || !TryReadSize(_y, out y) || !TryReadSize(_z, out z))
            {
                error = LangResources.Format(TextKey.NewDialog_SizeError, MinSize, MaxSize);
                return false;
            }
            return true;
        }

        private static bool TryReadSize(ComboBox box, out int value)
        {
            return int.TryParse(box.Text.Trim(), out value) && value >= MinSize && value <= MaxSize;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            int x, y, z;
            string error;
            if (!TryRead(out x, out y, out z, out error))
            {
                _error.Text = error;
                return;
            }

            SizeX = x; SizeY = y; SizeZ = z;
            DialogResult = true;
        }

        private void UpdateInfo()
        {
            int x, y, z;
            string error;
            if (!TryRead(out x, out y, out z, out error)) { _info.Text = ""; return; }

            _error.Text = "";
            long cells = (long)x * y * z;
            string size = cells >= 1048576 ? $"{cells / 1048576.0:0.#} MB" : $"{cells / 1024.0:0.#} KB";
            _info.Text = LangResources.Format(TextKey.NewDialog_SizeSummary, x, y, z, (cells).ToString("N0"), size)
                + (cells > 262144 ? LangResources.Get(TextKey.New_OnTheLargeSideEditing) : "");
        }

        private static ComboBox Combo()
        {
            ComboBox c = new ComboBox { Width = 62, IsEditable = true, Margin = new Thickness(6, 0, 6, 0) };
            foreach (int v in Presets) { c.Items.Add(v.ToString()); }
            return c;
        }

        private static TextBlock Label(string text, string tip)
        {
            return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        }
    }
}
