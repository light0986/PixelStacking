using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using PixelStacking.Controls;
using PixelStacking.Data;

namespace PixelStacking
{
    /// <summary>
    /// 說明選單的「給 AI 用」：命令列版 pstk.exe 與它的說明書（AI說明書.md）都放在 exe 旁邊。
    /// 「複製給 AI 的提示詞」會把一段話放進剪貼簿，內容是 pstk.exe 的完整路徑，並且叫 AI「先讀說明書、讀完才能用別的命令」。
    /// 提示詞是給 AI 看的，固定用英文，不跟著介面語言換。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>exe 所在的資料夾（用組件的位置，不用 AppDomain.BaseDirectory：被別的程式載入、測試時後者會指到別處）。</summary>
        private static string AppFolder { get { return Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); } }

        internal static string PstkPath { get { return Path.Combine(AppFolder, "pstk.exe"); } }

        internal static string AiManualPath { get { return Path.Combine(AppFolder, "AI說明書.md"); } }

        /// <summary>要複製給 AI 的提示詞（測試也用這個）。</summary>
        internal static string BuildAiPrompt()
        {
            string pstk = PstkPath, manual = AiManualPath;
            return "You can create and edit voxel / sprite-stacking models with PixelStacking through its command-line tool:\n"
                + "  " + pstk + "\n"
                + "BEFORE you do anything else, read the manual completely: run\n"
                + "  \"" + pstk + "\" manual\n"
                + "(the same text is in the file \"" + manual + "\"). Do not run any other pstk command until you have read it.\n"
                + "Then do the task below. Check your work as the manual says (pstk render / slice / info) and tell me the output file names.\n"
                + "\n"
                + "Task: <describe what you want here>\n";
        }

        private void AiPrompt_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(PstkPath)) { MessageBox.Show(this, LangResources.Format(TextKey.Ai_PstkMissing, PstkPath), LangResources.Get(TextKey.Main_PromptForAI), MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            string text = BuildAiPrompt();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    ClipText.Text = LangResources.Get(TextKey.Main_CopiedThePromptForAI);
                    return;
                }
                catch (COMException) { System.Threading.Thread.Sleep(60); }   // 剪貼簿暫時被別的程式占著
            }
            MessageBox.Show(this, LangResources.Get(TextKey.Main_TheClipboardIsTemporarilyUnavailable), LangResources.Get(TextKey.Main_PromptForAI), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void AiManual_Click(object sender, RoutedEventArgs e)
        {
            string path = AiManualPath;
            if (!File.Exists(path)) { MessageBox.Show(this, LangResources.Format(TextKey.Ai_ManualMissing, path), LangResources.Get(TextKey.Main_AIManual), MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Win32Exception)
            {
                try { Process.Start("notepad.exe", "\"" + path + "\""); }   // 沒有關聯 .md 的程式就用記事本
                catch (Win32Exception ex) { MessageBox.Show(this, ex.Message, LangResources.Get(TextKey.Main_AIManual), MessageBoxButton.OK, MessageBoxImage.Warning); }
            }
        }
    }
}
