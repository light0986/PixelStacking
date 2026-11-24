using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>使用者在「有未儲存的變更」的提示裡做的選擇。</summary>
    public enum SavePromptChoice { Cancel, Save, Discard }

    /// <summary>
    /// 「尚未儲存」提示：關閉視窗、新增、開啟檔案之前，文件有改動但還沒存時跳出來，問要儲存、不儲存還是取消。
    /// 按標題列的 X、Esc 都等於「取消」（最安全：什麼都不發生）；Enter = 儲存。
    /// </summary>
    public sealed class SavePromptDialog : Window
    {
        /// <summary>使用者的選擇；沒按任何按鈕就關掉視窗是 <see cref="SavePromptChoice.Cancel"/>。</summary>
        public SavePromptChoice Choice { get; private set; } = SavePromptChoice.Cancel;

        public SavePromptDialog(string documentName)
        {
            DialogStyle.Apply(this);
            Title = LangResources.Get(TextKey.SavePrompt_NotSavedYet);
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            MinWidth = 360;
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

            Button save = MakeButton(LangResources.Get(TextKey.Menu_Save), SavePromptChoice.Save);
            save.IsDefault = true;
            Button discard = MakeButton(LangResources.Get(TextKey.SavePrompt_DonTSave), SavePromptChoice.Discard);
            Button cancel = MakeButton(LangResources.Get(TextKey.Common_Cancel), SavePromptChoice.Cancel);
            cancel.IsCancel = true;
            System.Windows.Automation.AutomationProperties.SetAutomationId(save, "PromptSave");
            System.Windows.Automation.AutomationProperties.SetAutomationId(discard, "PromptDiscard");
            System.Windows.Automation.AutomationProperties.SetAutomationId(cancel, "PromptCancel");

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            buttons.Children.Add(save);
            buttons.Children.Add(discard);
            buttons.Children.Add(cancel);

            StackPanel root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock { Text = LangResources.Format(TextKey.SavePrompt_Message, documentName), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
            root.Children.Add(new TextBlock
            {
                Text = LangResources.Get(TextKey.SavePrompt_DonTSaveThrowsAway),
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            });
            root.Children.Add(buttons);
            Content = root;
        }

        private Button MakeButton(string text, SavePromptChoice choice)
        {
            Button b = new Button { Content = text, MinWidth = 78, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            b.Click += (s, e) =>
            {
                Choice = choice;
                DialogResult = true;
            };
            return b;
        }
    }
}
