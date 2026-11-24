using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>「平移」對話框：往右／往下移幾格（負數＝往左／往上），超出去的部分繞回另一邊或丟掉。</summary>
    public sealed class ShiftDialog : Window
    {
        private readonly TextBox _dx = new TextBox { Width = 56, Margin = new Thickness(6, 0, 14, 0), Text = "0" };
        private readonly TextBox _dy = new TextBox { Width = 56, Margin = new Thickness(6, 0, 6, 0), Text = "0" };
        private readonly CheckBox _wrap = new CheckBox { Content = LangResources.Get(TextKey.Shift_WrapAroundWhatGoesPast), IsChecked = false, Margin = new Thickness(0, 10, 0, 0) };
        private readonly TextBlock _error = new TextBlock { Foreground = Brushes.OrangeRed, Margin = new Thickness(0, 6, 0, 0), MinHeight = 18 };
        private readonly int _limitX, _limitY;

        /// <summary>往右移幾格（負數往左）。按確定後才有意義。</summary>
        public int Dx { get; private set; }
        /// <summary>往下移幾格（負數往上）。</summary>
        public int Dy { get; private set; }
        /// <summary>超出去的部分是不是繞回另一邊。</summary>
        public bool Wrap { get; private set; }

        public ShiftDialog(string scope, int canvasWidth, int canvasHeight)
        {
            _limitX = canvasWidth;
            _limitY = canvasHeight;
            DialogStyle.Apply(this);
            Title = LangResources.Get(TextKey.Shift_Shift);
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            System.Windows.Automation.AutomationProperties.SetAutomationId(_dx, "ShiftDx");
            System.Windows.Automation.AutomationProperties.SetAutomationId(_dy, "ShiftDy");
            System.Windows.Automation.AutomationProperties.SetAutomationId(_wrap, "ShiftWrap");

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(new TextBlock { Text = LangResources.Get(TextKey.Shift_Right), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(_dx);
            row.Children.Add(new TextBlock { Text = LangResources.Get(TextKey.Shift_Down), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(_dy);
            row.Children.Add(new TextBlock { Text = LangResources.Get(TextKey.Shift_Cells), VerticalAlignment = VerticalAlignment.Center });

            Button ok = new Button { Content = LangResources.Get(TextKey.Btn_OK), Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            Button cancel = new Button { Content = LangResources.Get(TextKey.Common_Cancel), Width = 70, IsCancel = true };
            System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "ShiftOk");
            ok.Click += Ok_Click;
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            StackPanel root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock { Text = LangResources.Format(TextKey.Shift_Prompt, scope, canvasWidth, canvasHeight) });
            root.Children.Add(row);
            root.Children.Add(new TextBlock
            {
                Text = LangResources.Get(TextKey.Shift_NegativeLeftUpWithoutWrap),
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
            });
            root.Children.Add(_wrap);
            root.Children.Add(_error);
            root.Children.Add(buttons);
            Content = root;
            Loaded += (s, e) => { _dx.Focus(); _dx.SelectAll(); };
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            int a, b;
            if (!int.TryParse(_dx.Text.Trim(), out a) || !int.TryParse(_dy.Text.Trim(), out b))
            {
                _error.Text = LangResources.Get(TextKey.Shift_PleaseEnterAWholeNumber);
                return;
            }
            if (System.Math.Abs(a) > _limitX || System.Math.Abs(b) > _limitY)
            {
                _error.Text = LangResources.Format(TextKey.Shift_LimitHint, _limitX, _limitY);
                return;
            }

            Dx = a;
            Dy = b;
            Wrap = _wrap.IsChecked == true;
            DialogResult = true;
        }
    }
}
