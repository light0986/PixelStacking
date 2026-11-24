using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PixelStacking.Data
{
    /// <summary>某一幀被改到的體素：索引與改之前的值（兩個陣列等長）。</summary>
    public sealed class FrameVoxelChange
    {
        public VoxelModel Frame;
        public int[] Indices;
        public byte[] Old;
    }

    /// <summary>
    /// 一次調色盤操作造成的所有變動：整個調色盤（256 格）改之前的樣子，加上被改到的體素。拿來做復原：
    /// <see cref="Undo"/> 把調色盤與體素原封不動還原。調色盤是所有幀共用的。
    /// </summary>
    public sealed class PaletteChange
    {
        public int[] OldPalette;
        public List<FrameVoxelChange> Voxels = new List<FrameVoxelChange>();

        /// <summary>這次操作「受影響」的項目數（清掉幾個顏色、合併幾個、匯入幾個…），給提示文字用。</summary>
        public int Affected;

        public int VoxelCount
        {
            get { int n = 0; foreach (FrameVoxelChange v in Voxels) { n += v.Indices.Length; } return n; }
        }

        public void Undo(Animation anim)
        {
            Array.Copy(OldPalette, anim.Palette, OldPalette.Length);
            foreach (FrameVoxelChange v in Voxels)
            {
                for (int i = 0; i < v.Indices.Length; i++) { v.Frame.Data[v.Indices[i]] = v.Old[i]; }
            }
            foreach (VoxelModel f in anim.Frames) { f.NotifyChanged(); }
        }
    }

    /// <summary>
    /// 調色盤管理：統計用量、清掉沒用到的顏色、合併重複的顏色、換色、刪色、匯入／匯出。
    /// 調色盤 256 格（0 = 透明、不是調色盤顏色；1~255 是顏色，alpha = 0 表示空格），所有幀共用。
    /// 每個會改東西的操作都回傳 <see cref="PaletteChange"/>（沒有任何變動時回傳 null），可以拿去復原。
    /// 注意：存在復原紀錄裡的是「索引」，所以調色盤的改動一定要跟著進復原堆疊（不然復原筆畫會還原成別的顏色）。
    /// </summary>
    public static class PaletteOps
    {
        public static bool IsFree(int argb) { return ((uint)argb >> 24) == 0; }

        /// <summary>每個調色盤索引在所有幀裡被多少個體素用到（長度 256）。</summary>
        public static int[] CountUsage(Animation anim)
        {
            int[] counts = new int[256];
            foreach (VoxelModel f in anim.Frames)
            {
                byte[] d = f.Data;
                for (int i = 0; i < d.Length; i++) { counts[d[i]]++; }
            }
            return counts;
        }

        private static PaletteChange Begin(Animation anim)
        {
            return new PaletteChange { OldPalette = (int[])anim.Palette.Clone() };
        }

        /// <summary>把所有幀裡的體素索引依 map 重新對應（map[舊] = 新），記下被改到的格子。</summary>
        private static void Remap(Animation anim, byte[] map, PaletteChange change)
        {
            foreach (VoxelModel f in anim.Frames)
            {
                List<int> idx = null;
                List<byte> old = null;
                byte[] d = f.Data;
                for (int i = 0; i < d.Length; i++)
                {
                    byte nv = map[d[i]];
                    if (nv == d[i]) { continue; }

                    if (idx == null) { idx = new List<int>(); old = new List<byte>(); }
                    idx.Add(i);
                    old.Add(d[i]);
                    d[i] = nv;
                }
                if (idx != null) { change.Voxels.Add(new FrameVoxelChange { Frame = f, Indices = idx.ToArray(), Old = old.ToArray() }); }
            }
        }

        private static byte[] IdentityMap()
        {
            byte[] map = new byte[256];
            for (int i = 0; i < 256; i++) { map[i] = (byte)i; }
            return map;
        }

        /// <summary>清掉沒有任何體素用到的顏色（所有幀都沒用到才算）。回傳 null = 沒有東西可清。</summary>
        public static PaletteChange ClearUnused(Animation anim)
        {
            int[] usage = CountUsage(anim);
            PaletteChange change = Begin(anim);
            for (int i = 1; i < 256; i++)
            {
                if (!IsFree(anim.Palette[i]) && usage[i] == 0)
                {
                    anim.Palette[i] = 0;
                    change.Affected++;
                }
            }
            return change.Affected == 0 ? null : change;
        }

        /// <summary>
        /// 合併重複的顏色：ARGB 完全一樣的色格只留編號最小的一個，其餘的體素改指向它、色格清空。
        /// 回傳 null = 沒有重複。Affected = 被合併掉（清空）的色格數。
        /// </summary>
        public static PaletteChange MergeDuplicates(Animation anim)
        {
            PaletteChange change = Begin(anim);
            byte[] map = IdentityMap();
            Dictionary<int, int> first = new Dictionary<int, int>();
            for (int i = 1; i < 256; i++)
            {
                int c = anim.Palette[i];
                if (IsFree(c)) { continue; }

                int keep;
                if (first.TryGetValue(c, out keep)) { map[i] = (byte)keep; anim.Palette[i] = 0; change.Affected++; }
                else { first[c] = i; }
            }
            if (change.Affected == 0) { return null; }

            Remap(anim, map, change);
            return change;
        }

        /// <summary>
        /// 把第 index 號顏色在整個模型（所有幀）換成 argb：體素本來就指向這個色格，所以只要改色格的顏色。
        /// 如果調色盤裡已經有別的色格是這個顏色，就改成「合併過去」（體素改指向那個色格、這格清空），不會出現兩格同色。
        /// 回傳 null = 本來就是這個顏色。
        /// </summary>
        public static PaletteChange ReplaceColour(Animation anim, int index, int argb)
        {
            if (index < 1 || index > 255 || IsFree(anim.Palette[index]) || anim.Palette[index] == argb || IsFree(argb)) { return null; }

            PaletteChange change = Begin(anim);
            int existing = -1;
            for (int i = 1; i < 256; i++)
            {
                if (i != index && anim.Palette[i] == argb) { existing = i; break; }
            }

            if (existing < 0) { anim.Palette[index] = argb; }
            else
            {
                byte[] map = IdentityMap();
                map[index] = (byte)existing;
                anim.Palette[index] = 0;
                Remap(anim, map, change);
            }
            change.Affected = 1;
            return change;
        }

        /// <summary>刪掉第 index 號顏色：所有用到它的體素（所有幀）變透明，色格清空。回傳 null = 本來就是空格。</summary>
        public static PaletteChange DeleteColour(Animation anim, int index)
        {
            if (index < 1 || index > 255 || IsFree(anim.Palette[index])) { return null; }

            PaletteChange change = Begin(anim);
            byte[] map = IdentityMap();
            map[index] = 0;
            anim.Palette[index] = 0;
            Remap(anim, map, change);
            change.Affected = 1;
            return change;
        }

        // ---- 匯出／匯入（GIMP 調色盤 .gpl 純文字格式）----------------------------------------------------------
        // 每行「R G B 名稱」；GIMP 沒有透明度，所以半透明的顏色把 alpha 寫在名稱裡（a=128）；匯入時讀回來，沒有 a= 就是不透明。
        public const string GplHeader = "GIMP Palette";

        /// <summary>把調色盤裡所有有顏色的色格寫成 .gpl 文字（依色格編號）。</summary>
        public static string ExportGpl(Animation anim, string name)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(GplHeader).Append("\r\n");
            sb.Append("Name: ").Append(string.IsNullOrWhiteSpace(name) ? "PixelStacking" : name.Trim()).Append("\r\n");
            sb.Append("Columns: 8\r\n#\r\n");
            for (int i = 1; i < 256; i++)
            {
                int c = anim.Palette[i];
                if (IsFree(c)) { continue; }

                int a = (int)((uint)c >> 24), r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
                sb.Append(r.ToString().PadLeft(3)).Append(' ').Append(g.ToString().PadLeft(3)).Append(' ').Append(b.ToString().PadLeft(3))
                  .Append("\t#").Append(i.ToString("D3"));
                if (a != 255) { sb.Append(" a=").Append(a); }
                sb.Append("\r\n");
            }
            return sb.ToString();
        }

        /// <summary>匯入的結果統計。</summary>
        public sealed class ImportStats
        {
            public int Parsed, Added, AlreadyThere, NoRoom;
        }

        private static readonly Regex ColourLine = new Regex(@"^\s*(\d{1,3})\s+(\d{1,3})\s+(\d{1,3})(?:\s+(.*))?$", RegexOptions.Compiled);
        private static readonly Regex AlphaTag = new Regex(@"\ba=(\d{1,3})\b", RegexOptions.Compiled);

        /// <summary>解析 .gpl 文字成 ARGB 清單（標頭、Name、Columns、註解、空行、壞掉的行都略過；數值超過 255 的行略過）。</summary>
        public static List<int> ParseGpl(string text)
        {
            List<int> colours = new List<int>();
            if (text == null) { return colours; }

            foreach (string raw in text.Split(new[] { '\n' }, StringSplitOptions.None))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) { continue; }

                Match m = ColourLine.Match(line);
                if (!m.Success) { continue; }

                int r = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int g = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                int b = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                if (r > 255 || g > 255 || b > 255) { continue; }

                int a = 255;
                Match am = AlphaTag.Match(m.Groups[4].Success ? m.Groups[4].Value : "");
                if (am.Success) { a = Math.Max(1, Math.Min(255, int.Parse(am.Groups[1].Value, CultureInfo.InvariantCulture))); }

                colours.Add((a << 24) | (r << 16) | (g << 8) | b);
            }
            return colours;
        }

        /// <summary>
        /// 把匯入的顏色「附加」進調色盤：已經有的顏色略過，其餘依序放進第一個空格；沒有空格就放不進去。
        /// 不會動到現有的色格與體素（所以是安全的）。回傳 null = 完全沒有新增。
        /// </summary>
        public static PaletteChange ImportColours(Animation anim, IList<int> colours, out ImportStats stats)
        {
            stats = new ImportStats { Parsed = colours.Count };
            PaletteChange change = Begin(anim);
            foreach (int c in colours)
            {
                bool exists = false;
                for (int i = 1; i < 256; i++) { if (anim.Palette[i] == c) { exists = true; break; } }
                if (exists) { stats.AlreadyThere++; continue; }

                int slot = -1;
                for (int i = 1; i < 256; i++) { if (IsFree(anim.Palette[i])) { slot = i; break; } }
                if (slot < 0) { stats.NoRoom++; continue; }

                anim.Palette[slot] = c;
                stats.Added++;
            }
            change.Affected = stats.Added;
            return stats.Added == 0 ? null : change;
        }
    }
}
