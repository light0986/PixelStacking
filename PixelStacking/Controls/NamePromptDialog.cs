using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 小視窗：請使用者輸入一個名稱（例如「另存為自訂預設」）。按確定時用 validator 檢查（回傳 null = 可以，否則是錯誤訊息，視窗不關）。
    /// </summary>
    public sealed class NamePromptDialog : Window
    {
        private readonly TextBox _box = new TextBox { MinWidth = 320, MaxLength = 60 };
        private readonly TextBlock _error = new TextBlock { Foreground = Brushes.OrangeRed, Margin = new Thickness(0, 6, 0, 0), MinHeight = 18, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        private readonly Func<string, string> _validator;

        /// <summary>按確定後輸入的名稱（未整理）。</summary>
        public string Value { get; private set; }

        public NamePromptDialog(string title, string prompt, string initial, Func<string, string> validator)
        {
            _validator = validator;
            DialogStyle.Apply(this);
            Title = title;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            AutomationProperties.SetAutomationId(_box, "NameBox");
            AutomationProperties.SetAutomationId(_error, "NameError");

            _box.Text = initial ?? "";
            Button ok = new Button { Content = LangResources.Get(TextKey.Btn_OK), Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            Button cancel = new Button { Content = LangResources.Get(TextKey.Common_Cancel), Width = 80, IsCancel = true };
            AutomationProperties.SetAutomationId(ok, "NameOk");
            AutomationProperties.SetAutomationId(cancel, "NameCancel");
            ok.Click += (s, e) =>
            {
                string problem = _validator == null ? null : _validator(_box.Text);
                if (problem != null) { _error.Text = problem; return; }

                Value = _box.Text;
                DialogResult = true;
            };
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            StackPanel root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 0, 0, 8) });
            root.Children.Add(_box);
            root.Children.Add(_error);
            root.Children.Add(buttons);
            Content = root;
            Loaded += (s, e) => { _box.Focus(); _box.SelectAll(); };
        }
    }
}
