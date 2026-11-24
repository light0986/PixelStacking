using System;
using System.Collections.Generic;

namespace PixelStacking.Data
{
    /// <summary>關節轉動的軸（模型的 X／Y／Z 軸，跟鏡像對稱用同一套：X = 長、Y = 寬、Z = 高）。</summary>
    public enum BoneAxis { X = 0, Y = 1, Z = 2 }

    /// <summary>
    /// 一個部件＋它的關節：<see cref="Id"/>（1~255）就是體素上標記的部件編號，所以「這塊體素屬於哪個部件」直接存成編號；
    /// 關節 = 繞 <see cref="Axis"/> 軸、通過樞軸點 (<see cref="PivotX"/>, <see cref="PivotY"/>, <see cref="PivotZ"/>) 轉 <see cref="Angle"/> 度。
    /// 樞軸點用體素座標（格子的左邊緣 = 整數，格子中心 = x + 0.5）。部件編號 0 = 身體（不動的部分），不是骨頭。
    /// </summary>
    public sealed class Bone
    {
        public int Id;
        public string Name = "";

        /// <summary>上一層部件的編號（0 = 沒有，直接掛在身體上）。上一層轉動時，這個部件跟著一起轉。</summary>
        public int ParentId;

        public BoneAxis Axis = BoneAxis.X;
        public double PivotX, PivotY, PivotZ;

        /// <summary>目前姿勢的轉動角度（度）。</summary>
        public double Angle;

        /// <summary>
        /// 目前姿勢的平移（體素；X 長、Y 寬、Z 高，Z 正 = 往上）。先繞樞軸轉、再平移；子部件跟著一起轉、一起移。
        /// 烘焙是最近鄰取樣，所以實際上平移會四捨五入成整數格。
        /// </summary>
        public double OffsetX, OffsetY, OffsetZ;

        public Bone Clone()
        {
            return (Bone)MemberwiseClone();
        }
    }

    /// <summary>整副骨架的快照（復原用）：全部部件＋身體的平移。</summary>
    public sealed class RigSnapshot
    {
        public Bone[] Bones;
        public double BodyX, BodyY, BodyZ;
    }

    /// <summary>一份動畫的骨架：一串 <see cref="Bone"/>（部件）。內容變了呼叫 <see cref="NotifyChanged"/>，<see cref="Version"/> 會 +1。</summary>
    public sealed class Rig
    {
        public const int MaxBones = 255;

        private readonly List<Bone> _bones = new List<Bone>();

        public IReadOnlyList<Bone> Bones => _bones;
        public int Count => _bones.Count;

        /// <summary>
        /// 身體（沒有標記部件的體素）的平移。直接掛在身體上的部件（上一層 =「（身體）」）跟著它一起移，
        /// 所以身體上下彈跳、整個人跳起來，只要改這三個數字。
        /// </summary>
        public double BodyX, BodyY, BodyZ;

        /// <summary>每次改動 +1，預覽、快取用它判斷要不要重算。</summary>
        public int Version { get; private set; }

        public event Action Changed;

        public void NotifyChanged()
        {
            Version++;
            Changed?.Invoke();
        }

        public Bone Find(int id)
        {
            for (int i = 0; i < _bones.Count; i++) { if (_bones[i].Id == id) { return _bones[i]; } }
            return null;
        }

        /// <summary>新增一個部件（編號取最小的空號），回傳它；滿了回傳 null。</summary>
        public Bone Add(string name, double pivotX, double pivotY, double pivotZ)
        {
            if (_bones.Count >= MaxBones) { return null; }

            int id = 1;
            while (Find(id) != null) { id++; }

            Bone b = new Bone { Id = id, Name = name, PivotX = pivotX, PivotY = pivotY, PivotZ = pivotZ };
            _bones.Add(b);
            NotifyChanged();
            return b;
        }

        /// <summary>刪除一個部件：它的子部件改掛到它的上一層。體素上的標記由呼叫端另外清掉。回傳有沒有刪。</summary>
        public bool Remove(int id)
        {
            Bone b = Find(id);
            if (b == null) { return false; }

            foreach (Bone c in _bones) { if (c.ParentId == id) { c.ParentId = b.ParentId; } }
            _bones.Remove(b);
            NotifyChanged();
            return true;
        }

        /// <summary>newParentId 是不是 id 自己或它的子孫（拿來擋掉「循環」：把部件掛到自己的子孫底下）。</summary>
        public bool WouldCycle(int id, int newParentId)
        {
            int guard = 0;
            for (int p = newParentId; p != 0 && guard++ <= _bones.Count; )
            {
                if (p == id) { return true; }

                Bone b = Find(p);
                if (b == null) { return false; }

                p = b.ParentId;
            }
            return false;
        }

        /// <summary>設定上一層；會造成循環或找不到那個部件時不改、回傳 false。</summary>
        public bool SetParent(int id, int parentId)
        {
            Bone b = Find(id);
            if (b == null || (parentId != 0 && Find(parentId) == null) || WouldCycle(id, parentId)) { return false; }

            if (b.ParentId == parentId) { return true; }

            b.ParentId = parentId;
            NotifyChanged();
            return true;
        }

        /// <summary>全部部件與身體平移的複本（復原用）。</summary>
        public RigSnapshot Snapshot()
        {
            Bone[] s = new Bone[_bones.Count];
            for (int i = 0; i < s.Length; i++) { s[i] = _bones[i].Clone(); }
            return new RigSnapshot { Bones = s, BodyX = BodyX, BodyY = BodyY, BodyZ = BodyZ };
        }

        /// <summary>把骨架整批換成快照的內容（快照本身不會被動到）。</summary>
        public void Restore(RigSnapshot snapshot)
        {
            _bones.Clear();
            foreach (Bone b in snapshot.Bones) { _bones.Add(b.Clone()); }
            BodyX = snapshot.BodyX; BodyY = snapshot.BodyY; BodyZ = snapshot.BodyZ;
            NotifyChanged();
        }

        /// <summary>全部姿勢歸零：每個部件的角度與平移、身體的平移。</summary>
        public void ResetPose()
        {
            bool any = BodyX != 0 || BodyY != 0 || BodyZ != 0;
            BodyX = BodyY = BodyZ = 0;
            foreach (Bone b in _bones)
            {
                if (b.Angle != 0 || b.OffsetX != 0 || b.OffsetY != 0 || b.OffsetZ != 0) { any = true; }
                b.Angle = 0; b.OffsetX = b.OffsetY = b.OffsetZ = 0;
            }
            if (any) { NotifyChanged(); }
        }

        /// <summary>目前是不是休息姿勢（所有角度、平移都是 0）。</summary>
        public bool IsRest
        {
            get
            {
                if (BodyX != 0 || BodyY != 0 || BodyZ != 0) { return false; }

                foreach (Bone b in _bones) { if (b.Angle != 0 || b.OffsetX != 0 || b.OffsetY != 0 || b.OffsetZ != 0) { return false; } }
                return true;
            }
        }

        /// <summary>部件的顏色（畫布上標示用）：依編號在色相環上均勻散開（黃金角），相鄰編號顏色差很多。回傳 0xRRGGBB。</summary>
        public static int PartColor(int id)
        {
            double h = (id * 137.508) % 360.0;
            double s = 0.75, v = 1.0;
            double c = v * s, x = c * (1 - Math.Abs((h / 60.0) % 2 - 1)), m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return ((int)Math.Round((r + m) * 255) << 16) | ((int)Math.Round((g + m) * 255) << 8) | (int)Math.Round((b + m) * 255);
        }
    }
}
