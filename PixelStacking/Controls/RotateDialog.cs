using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>「旋轉選取區」對話框：再轉幾度（正 = 順時針、負 = 逆時針），任意角度。</summary>
    public sealed class RotateDialog : Window
    {
        private readonly TextBox _angle = new TextBox { Width = 70, Margin = new Thickness(6, 0, 6, 0), Text = "45", VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _error = new TextBlock { Foreground = Brushes.OrangeRed, Margin = new Thickness(0, 6, 0, 0), MinHeight = 18 };

        /// <summary>要再轉的角度（度，正 = 順時針）。按確定後才有意義。</summary>
        public double Degrees { get; private set; }

        public RotateDialog(double currentDegrees)
        {
            DialogStyle.Apply(this);
            Title = LangResources.Get(TextKey.Menu_RotateSelection);
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            AutomationProperties.SetAutomationId(_angle, "RotAngle");

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(new TextBlock { Text = LangResources.Get(TextKey.Rotate_RotateBy), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(_angle);
            row.Children.Add(new TextBlock { Text = LangResources.Get(TextKey.Common_Deg), VerticalAlignment = VerticalAlignment.Center });

            Button ok = new Button { Content = LangResources.Get(TextKey.Btn_OK), Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            Button cancel = new Button { Content = LangResources.Get(TextKey.Common_Cancel), Width = 70, IsCancel = true };
            AutomationProperties.SetAutomationId(ok, "RotOk");
            ok.Click += Ok_Click;
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            StackPanel root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock { Text = currentDegrees == 0 ? LangResources.Get(TextKey.Rotate_RotateAroundTheExactCentre) : LangResources.Format(TextKey.Rotate_AlreadyRotated, currentDegrees.ToString("0.##", CultureInfo.InvariantCulture)) });
            root.Children.Add(row);
            root.Children.Add(new TextBlock
            {
                Text = LangResources.Get(TextKey.Rotate_PositiveClockwiseNegativeCounter),
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
            });
            root.Children.Add(_error);
            root.Children.Add(buttons);
            Content = root;
            Loaded += (s, e) => { _angle.Focus(); _angle.SelectAll(); };
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            double v;
            if (!double.TryParse(_angle.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || double.IsNaN(v) || double.IsInfinity(v))
            {
                _error.Text = LangResources.Get(TextKey.Rotate_PleaseEnterANumberNegative);
                return;
            }
            if (v < -3600 || v > 3600) { _error.Text = LangResources.Get(TextKey.Rotate_TheAngleMustBeBetween); return; }

            Degrees = v;
            DialogResult = true;
        }
    }
}
