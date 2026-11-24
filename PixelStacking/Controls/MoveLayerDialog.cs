using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 「移動圖層內容」對話框：把目前圖層裡所有的體素整塊平移（X、Y、Z 三個方向的格數，可以是負的），
    /// 只動這一幀或這個圖層在所有幀的內容。超出模型的部分會被丟掉（可以復原）。
    /// </summary>
    public sealed class MoveLayerDialog : Window
    {
        private readonly TextBox _x = new TextBox { Width = 64, Text = "0" }, _y = new TextBox { Width = 64, Text = "0" }, _z = new TextBox { Width = 64, Text = "0" };
        private readonly RadioButton _thisFrame = new RadioButton { Content = LangResources.Get(TextKey.MoveLayer_OnlyThisFrame), IsChecked = true, GroupName = "MoveScope", Margin = new Thickness(0, 2, 0, 2) };
        private readonly RadioButton _allFrames = new RadioButton { Content = LangResources.Get(TextKey.MoveLayer_TheContentOfThisLayer), GroupName = "MoveScope", Margin = new Thickness(0, 2, 0, 2) };
        private readonly TextBlock _error = new TextBlock { Foreground = Brushes.OrangeRed, MinHeight = 18, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };

        public int DX { get; private set; }
        public int DY { get; private set; }
        public int DZ { get; private set; }
        public bool AllFrames { get; private set; }

        /// <param name="layerName">要移動的圖層名稱。</param>
        /// <param name="maxMove">各方向最多移多少（通常是模型尺寸）。</param>
        public MoveLayerDialog(string layerName, int maxMove)
        {
            DialogStyle.Apply(this);
            Title = LangResources.Get(TextKey.Common_MoveLayerContent);
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            AutomationProperties.SetAutomationId(_x, "MoveX"); AutomationProperties.SetAutomationId(_y, "MoveY"); AutomationProperties.SetAutomationId(_z, "MoveZ");
            AutomationProperties.SetAutomationId(_thisFrame, "MoveThisFrame"); AutomationProperties.SetAutomationId(_allFrames, "MoveAllFrames"); AutomationProperties.SetAutomationId(_error, "MoveError");

            StackPanel root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock { Text = LangResources.Format(TextKey.MoveLayer_Prompt, layerName), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            row.Children.Add(new TextBlock { Text = "X", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }); row.Children.Add(_x);
            row.Children.Add(new TextBlock { Text = "Y", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) }); row.Children.Add(_y);
            row.Children.Add(new TextBlock { Text = "Z", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) }); row.Children.Add(_z);
            root.Children.Add(row);
            root.Children.Add(new TextBlock
            {
                Text = LangResources.Format(TextKey.MoveLayer_UnitsHint, maxMove, maxMove),
                FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 4, 0, 6),
            });
            root.Children.Add(_thisFrame);
            root.Children.Add(_allFrames);
            root.Children.Add(_error);

            Button ok = new Button { Content = LangResources.Get(TextKey.MoveLayer_Move), Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            Button cancel = new Button { Content = LangResources.Get(TextKey.Common_Cancel), Width = 80, IsCancel = true };
            AutomationProperties.SetAutomationId(ok, "MoveOk"); AutomationProperties.SetAutomationId(cancel, "MoveCancel");
            ok.Click += (s, e) =>
            {
                int x, y, z;
                if (!Parse(_x.Text, maxMove, out x) || !Parse(_y.Text, maxMove, out y) || !Parse(_z.Text, maxMove, out z))
                {
                    _error.Text = LangResources.Format(TextKey.MoveLayer_RangeError, maxMove, maxMove);
                    return;
                }
                if (x == 0 && y == 0 && z == 0) { _error.Text = LangResources.Get(TextKey.MoveLayer_AllThreeDirectionsAre0); return; }

                DX = x; DY = y; DZ = z; AllFrames = _allFrames.IsChecked == true;
                DialogResult = true;
            };
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;
            Loaded += (s, e) => { _x.Focus(); _x.SelectAll(); };
        }

        private static bool Parse(string text, int max, out int value)
        {
            return int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && value >= -max && value <= max;
        }
    }
}
