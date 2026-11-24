using System;

namespace PixelStacking.Data
{
    /// <summary>編輯畫布的觀看方向。體素座標：X = 長（頂部視角往右）、Y = 寬（頂部視角往下）、Z = 高（層數，往上）。</summary>
    public enum ViewDirection { Top, Bottom, Front, Back, Left, Right }

    /// <summary>
    /// 可以裁切的體素範圍（含頭尾）。預覽用它「切開」模型看內部：範圍外的格子不畫。
    /// </summary>
    public struct ClipRange : IEquatable<ClipRange>
    {
        public int XMin, XMax, YMin, YMax, ZMin, ZMax;

        public static ClipRange Full(int w, int h, int d)
        {
            return new ClipRange { XMin = 0, XMax = w - 1, YMin = 0, YMax = h - 1, ZMin = 0, ZMax = d - 1 };
        }

        public bool Equals(ClipRange o)
        {
            return XMin == o.XMin && XMax == o.XMax && YMin == o.YMin && YMax == o.YMax && ZMin == o.ZMin && ZMax == o.ZMax;
        }

        public override bool Equals(object obj) { return obj is ClipRange c && Equals(c); }

        public override int GetHashCode()
        {
            unchecked { return (((((XMin * 31 + XMax) * 31 + YMin) * 31 + YMax) * 31 + ZMin) * 31 + ZMax); }
        }
    }

    /// <summary>
    /// 把「某個視角、第 s 個切片上的畫布格 (cx, cy)」對應到體素陣列的線性索引。
    /// 切片編號 s 越大 = 越靠近觀看者（所以 s-1 是「後面那一片」，用來當半透明參考）。
    /// 每個視角都是正確的（沒有鏡像）正交視圖：
    ///   頂部　從上往下看　　→ +X　↓ +Y（切片 = Z，往上）
    ///   底部　從下往上看　　→ +X　↓ −Y（切片 = Z，往下）
    ///   正面　站在 +Y 側往 −Y 看　→ +X　↑ +Z（切片 = Y）
    ///   背面　站在 −Y 側往 +Y 看　→ −X　↑ +Z
    ///   左側　站在 −X 側往 +X 看　→ +Y　↑ +Z（切片 = X）
    ///   右側　站在 +X 側往 −X 看　→ −Y　↑ +Z
    /// </summary>
    public static class ViewMapper
    {
        public static int CanvasWidth(ViewDirection v, int w, int h, int d)
        {
            return (v == ViewDirection.Left || v == ViewDirection.Right) ? h : w;
        }

        public static int CanvasHeight(ViewDirection v, int w, int h, int d)
        {
            return (v == ViewDirection.Top || v == ViewDirection.Bottom) ? h : d;
        }

        public static int SliceCount(ViewDirection v, int w, int h, int d)
        {
            switch (v)
            {
                case ViewDirection.Top:
                case ViewDirection.Bottom: return d;
                case ViewDirection.Front:
                case ViewDirection.Back: return h;
                default: return w;
            }
        }

        /// <summary>索引 = origin + cx * stepX + cy * stepY（索引公式：z * w * h + y * w + x）。</summary>
        public static void Plane(ViewDirection v, int s, int w, int h, int d, out int origin, out int stepX, out int stepY)
        {
            int wh = w * h;
            switch (v)
            {
                case ViewDirection.Top:
                    origin = s * wh; stepX = 1; stepY = w; break;
                case ViewDirection.Bottom:
                    origin = (d - 1 - s) * wh + (h - 1) * w; stepX = 1; stepY = -w; break;
                case ViewDirection.Front:
                    origin = (d - 1) * wh + s * w; stepX = 1; stepY = -wh; break;
                case ViewDirection.Back:
                    origin = (d - 1) * wh + (h - 1 - s) * w + (w - 1); stepX = -1; stepY = -wh; break;
                case ViewDirection.Left:
                    origin = (d - 1) * wh + (w - 1 - s); stepX = w; stepY = -wh; break;
                default: // Right
                    origin = (d - 1) * wh + (h - 1) * w + s; stepX = -w; stepY = -wh; break;
            }
        }

        /// <summary>
        /// 這個視角下：畫布往右一格（cx + 1）、往下一格（cy + 1）、往觀看者一片（切片 + 1）各自對應的體素位移（dx, dy, dz，每個分量 −1／0／+1）。
        /// 搬移選取區用它把「畫布方向」換成體素座標方向。
        /// </summary>
        public static void Axes(ViewDirection v, out int[] right, out int[] down, out int[] toward)
        {
            switch (v)
            {
                case ViewDirection.Top: right = new[] { 1, 0, 0 }; down = new[] { 0, 1, 0 }; toward = new[] { 0, 0, 1 }; break;
                case ViewDirection.Bottom: right = new[] { 1, 0, 0 }; down = new[] { 0, -1, 0 }; toward = new[] { 0, 0, -1 }; break;
                case ViewDirection.Front: right = new[] { 1, 0, 0 }; down = new[] { 0, 0, -1 }; toward = new[] { 0, 1, 0 }; break;
                case ViewDirection.Back: right = new[] { -1, 0, 0 }; down = new[] { 0, 0, -1 }; toward = new[] { 0, -1, 0 }; break;
                case ViewDirection.Left: right = new[] { 0, 1, 0 }; down = new[] { 0, 0, -1 }; toward = new[] { -1, 0, 0 }; break;
                default: right = new[] { 0, -1, 0 }; down = new[] { 0, 0, -1 }; toward = new[] { 1, 0, 0 }; break;
            }
        }

        public static void ToVoxel(int index, int w, int h, out int x, out int y, out int z)
        {
            int wh = w * h;
            z = index / wh;
            int rem = index - z * wh;
            y = rem / w;
            x = rem - y * w;
        }

        /// <summary>「從這個視角看，目前切片（含）以後面的部分」的體素範圍：預覽用它切開看內部。</summary>
        public static ClipRange Clip(ViewDirection v, int s, int w, int h, int d)
        {
            ClipRange c = ClipRange.Full(w, h, d);
            switch (v)
            {
                case ViewDirection.Top: c.ZMax = s; break;
                case ViewDirection.Bottom: c.ZMin = d - 1 - s; break;
                case ViewDirection.Front: c.YMax = s; break;
                case ViewDirection.Back: c.YMin = h - 1 - s; break;
                case ViewDirection.Left: c.XMin = w - 1 - s; break;
                default: c.XMax = s; break;
            }
            return c;
        }

        public static string Name(ViewDirection v)
        {
            switch (v)
            {
                case ViewDirection.Top: return Loc.Get(TextKey.ViewDirection_Top);
                case ViewDirection.Bottom: return Loc.Get(TextKey.ViewDirection_Bottom);
                case ViewDirection.Front: return Loc.Get(TextKey.ViewDirection_Front);
                case ViewDirection.Back: return Loc.Get(TextKey.ViewDirection_Back);
                case ViewDirection.Left: return Loc.Get(TextKey.ViewDirection_Left);
                default: return Loc.Get(TextKey.ViewDirection_Right);
            }
        }

        public static string SliceAxis(ViewDirection v)
        {
            switch (v)
            {
                case ViewDirection.Top:
                case ViewDirection.Bottom: return "Z";
                case ViewDirection.Front:
                case ViewDirection.Back: return "Y";
                default: return "X";
            }
        }

        public static string Describe(ViewDirection v)
        {
            switch (v)
            {
                case ViewDirection.Top: return Loc.Get(TextKey.ViewDirection_TopLookingDownXY);
                case ViewDirection.Bottom: return Loc.Get(TextKey.ViewDirection_BottomLookingUpXY);
                case ViewDirection.Front: return Loc.Get(TextKey.ViewDirection_FrontStandingOnTheY);
                case ViewDirection.Back: return Loc.Get(TextKey.ViewDirection_BackStandingOnTheY);
                case ViewDirection.Left: return Loc.Get(TextKey.ViewDirection_LeftStandingOnTheX);
                default: return Loc.Get(TextKey.ViewDirection_RightStandingOnTheX);
            }
        }
    }
}
