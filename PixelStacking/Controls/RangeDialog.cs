using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>「複製到範圍」對話框：問使用者要把目前這層複製到第幾層到第幾層（顯示的是從 1 開始的編號）。</summary>
    public sealed class RangeDialog : Window
    {
        private readonly TextBox _from = new TextBox { Width = 56, Margin = new Thickness(6, 0, 6, 0) };
        private readonly TextBox _to = new TextBox { Width = 56, Margin = new Thickness(6, 0, 6, 0) };
        private readonly TextBlock _error = new TextBlock { Foreground = Brushes.OrangeRed, Margin = new Thickness(0, 6, 0, 0), MinHeight = 18 };
        private readonly int _count;

        /// <summary>使用者輸入的範圍（從 1 開始，含頭尾）。按確定後才有意義。</summary>
        public int From { get; private set; }
        public int To { get; private set; }

        public RangeDialog(int count, int from, int to, string unit, int currentNumber)
        {
            _count = count;
            DialogStyle.Apply(this);
            Title = LangResources.Get(TextKey.Range_CopyToRange);
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

            _from.Text = from.ToString();
            _to.Text = to.ToString();

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(new TextBlock { Text = LangResources.Get(TextKey.Range_From), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(_from);
            row.Children.Add(new TextBlock { Text = LangResources.Get(TextKey.Range_To), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(_to);
            row.Children.Add(new TextBlock { Text = LangResources.Format(TextKey.Range_CurrentOption, unit, count, unit), VerticalAlignment = VerticalAlignment.Center });

            Button ok = new Button { Content = LangResources.Get(TextKey.Btn_OK), Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            Button cancel = new Button { Content = LangResources.Get(TextKey.Common_Cancel), Width = 70, IsCancel = true };
            ok.Click += Ok_Click;
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            StackPanel root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock { Text = LangResources.Format(TextKey.Range_Prompt, unit, currentNumber, unit) });
            root.Children.Add(row);
            root.Children.Add(_error);
            root.Children.Add(new TextBlock
            {
                Text = LangResources.Get(TextKey.Range_EveryOtherSliceInThe),
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
            });
            root.Children.Add(buttons);
            Content = root;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            int a, b;
            if (!int.TryParse(_from.Text.Trim(), out a) || !int.TryParse(_to.Text.Trim(), out b))
            {
                _error.Text = LangResources.Get(TextKey.Range_PleaseEnterAWholeNumber);
                return;
            }
            if (a < 1 || b < 1 || a > _count || b > _count)
            {
                _error.Text = LangResources.Format(TextKey.Range_Invalid, _count);
                return;
            }

            From = System.Math.Min(a, b);
            To = System.Math.Max(a, b);
            DialogResult = true;
        }
    }
}
