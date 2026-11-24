using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using PixelStacking.Data;

namespace PixelStacking.Cli
{
    /// <summary>
    /// 一個命令：Parse 讀完所有參數與選項（不動任何檔案）、回傳要執行的動作；Program 在執行前會先檢查有沒有打錯的選項。
    /// </summary>
    internal sealed class Cmd
    {
        public string Name, Group, Usage, Summary;
        public Func<CliArgs, Action> Parse;

        public Cmd(string group, string name, string usage, string summary, Func<CliArgs, Action> parse)
        {
            Group = group; Name = name; Usage = usage; Summary = summary; Parse = parse;
        }
    }

    internal static class Program
    {
        internal static readonly List<Cmd> Commands = new List<Cmd>();

        [STAThread]
        private static int Main(string[] argv)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }
            try { Loc.Language = Lang.En; } catch (Exception) { }

            Cmds.Register(Commands);
            CmdsEdit.Register(Commands);
            CmdsRig.Register(Commands);

            if (argv.Length == 0) { PrintOverview(Console.Error); return 2; }

            string name = argv[0].ToLowerInvariant();
            if (name == "-h" || name == "--help" || name == "/?") { name = "help"; }
            if (name == "--version" || name == "-v") { name = "version"; }

            Cmd cmd = Commands.Find(c => c.Name == name);
            if (cmd == null)
            {
                Console.Error.WriteLine("error: unknown command '" + argv[0] + "'. Run `pstk help` for the list, and `pstk manual` for the full manual.");
                return 2;
            }

            try
            {
                CliArgs args = CliArgs.Parse(argv, 1);
                Action run = cmd.Parse(args);
                args.CheckUnused();
                run();
                return 0;
            }
            catch (CliError e)
            {
                Console.Error.WriteLine("error: " + e.Message);
                if (e.Code == 2) { Console.Error.WriteLine("usage: pstk " + cmd.Usage + "    (details: pstk help " + cmd.Name + ")"); }
                return e.Code;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is InvalidOperationException || e is InvalidDataException || e is FormatException)
            {
                Console.Error.WriteLine("error: " + Loc.T(e.Message));
                return 1;
            }
        }

        internal static void PrintOverview(TextWriter w)
        {
            w.WriteLine("pstk - PixelStacking command line (voxel / sprite-stacking editor, headless).");
            w.WriteLine("AI agents: run `pstk manual` FIRST and read it completely before using any other command.");
            w.WriteLine();
            string group = null;
            foreach (Cmd c in Commands)
            {
                if (c.Group != group) { group = c.Group; w.WriteLine(); w.WriteLine(group + ":"); }

                w.WriteLine("  " + c.Usage);
                w.WriteLine("        " + c.Summary);
            }
            w.WriteLine();
            w.WriteLine("All indices are 0-based (frames, layers, slices, bones' list position). Voxel coordinates are x (length), y (width), z (height, up).");
            w.WriteLine("Commands that change a file save it in place; add --out other.pstk to write somewhere else instead.");
        }

        internal static string ManualText()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream("manual.md"))
            {
                if (s == null) { return "(manual is not embedded in this build)"; }

                using (StreamReader r = new StreamReader(s, Encoding.UTF8)) { return r.ReadToEnd(); }
            }
        }
    }
}
