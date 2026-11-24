using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PixelStacking.Data
{
    /// <summary>
    /// 一份逐幀動畫文件：共用的尺寸與調色盤 + 一串幀（每幀一個 <see cref="VoxelModel"/>）+ 播放速度。
    /// 至少保留一幀。幀的增減、搬移都會觸發 <see cref="StructureChanged"/>（時間軸據此重建）。
    /// </summary>
    public sealed class Animation
    {
        private const string Magic = "PSTK";
        private const int FileVersion = 5;
        public const int MaxLayers = 64;
        private const long MaxTotalCells = 256L * 1024 * 1024;

        public const int MinFps = 1;
        public const int MaxFps = 30;

        private readonly List<VoxelModel> _frames = new List<VoxelModel>();
        private readonly List<Layer> _layers = new List<Layer> { new Layer(Loc.Get(TextKey.Animation_Layer1)) };
        private int _fps = 8;

        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        public int[] Palette { get; }

        /// <summary>骨架：部件與關節（沒有任何部件時是空的）。所有幀共用同一副骨架，各幀各自標記哪些體素屬於哪個部件。</summary>
        public Rig Rig { get; } = new Rig();

        public IReadOnlyList<VoxelModel> Frames => _frames;
        public int Count => _frames.Count;

        /// <summary>圖層清單（第 0 個最底層）。永遠至少一個；只有一個時所有幀都是單層（Cels = null）。</summary>
        public IReadOnlyList<Layer> Layers => _layers;
        public int LayerCount => _layers.Count;
        public bool IsLayered => _layers.Count > 1;

        /// <summary>圖層被新增、刪除、搬移、合併、改名、顯示／隱藏後觸發。</summary>
        public event Action LayersChanged;

        /// <summary>每秒播放幾幀。</summary>
        public int Fps
        {
            get => _fps;
            set => _fps = Math.Max(MinFps, Math.Min(MaxFps, value));
        }

        /// <summary>幀被新增、刪除或重新排序後觸發（單一幀的內容變動不算，那是 VoxelModel.Changed）。</summary>
        public event Action StructureChanged;

        public Animation(int width, int height, int depth)
            : this(width, height, depth, DefaultPalette.Create())
        {
            _frames.Add(NewFrame());
        }

        private Animation(int width, int height, int depth, int[] palette)
        {
            if (width < 1 || height < 1 || depth < 1) { throw new ArgumentException(Loc.Get(TextKey.Common_TheSizeMustBeGreater)); }

            Width = width;
            Height = height;
            Depth = depth;
            Palette = palette;
        }

        /// <summary>
        /// 用一串現成的幀組成一份臨時動畫（匯出用，例如姿勢預設庫的預覽存檔）：不複製幀，調色盤沿用第一幀的（同一份陣列）。
        /// 幀的尺寸必須一致。
        /// </summary>
        public static Animation FromFrames(IList<VoxelModel> frames, int fps)
        {
            if (frames == null || frames.Count == 0) { throw new ArgumentException(Loc.Get(TextKey.Animation_NoFrames)); }

            VoxelModel first = frames[0];
            Animation a = new Animation(first.Width, first.Height, first.Depth, first.Palette) { Fps = fps };
            foreach (VoxelModel f in frames)
            {
                if (f.Width != first.Width || f.Height != first.Height || f.Depth != first.Depth) { throw new ArgumentException(Loc.Get(TextKey.Animation_TheFramesAreNotAll)); }

                a._frames.Add(f);
            }
            return a;
        }

        public VoxelModel NewFrame()
        {
            VoxelModel frame = new VoxelModel(Width, Height, Depth, Palette);
            if (IsLayered)
            {
                List<VoxelModel> cels = new List<VoxelModel>();
                for (int k = 0; k < _layers.Count; k++) { cels.Add(NewCel()); }
                frame.AttachLayers(_layers, cels);
            }
            return frame;
        }

        private VoxelModel NewCel() { return new VoxelModel(Width, Height, Depth, Palette); }

        /// <summary>把一幀「單層」的內容放進最底層（其他圖層空白），讓它跟目前的圖層結構一致。</summary>
        private void WrapIntoLayers(VoxelModel frame)
        {
            List<VoxelModel> cels = new List<VoxelModel>();
            for (int k = 0; k < _layers.Count; k++) { cels.Add(NewCel()); }
            Buffer.BlockCopy(frame.Data, 0, cels[0].Data, 0, frame.Data.Length);
            frame.AttachLayers(_layers, cels);
        }

        public int IndexOf(VoxelModel frame)
        {
            return _frames.IndexOf(frame);
        }

        public void Insert(int index, VoxelModel frame)
        {
            index = Math.Max(0, Math.Min(index, _frames.Count));
            if (IsLayered && !frame.IsLayered) { WrapIntoLayers(frame); }
            _frames.Insert(index, frame);
            StructureChanged?.Invoke();
        }

        /// <summary>移除一幀，回傳它原本的位置；只剩一幀或找不到時不移除、回傳 -1。</summary>
        public int Remove(VoxelModel frame)
        {
            int index = _frames.IndexOf(frame);
            if (index < 0 || _frames.Count <= 1) { return -1; }

            _frames.RemoveAt(index);
            StructureChanged?.Invoke();
            return index;
        }

        /// <summary>把第 from 幀搬到 to 的位置（其他幀依序遞補）。</summary>
        public void Move(int from, int to)
        {
            if ((uint)from >= (uint)_frames.Count) { return; }
            to = Math.Max(0, Math.Min(to, _frames.Count - 1));
            if (from == to) { return; }

            VoxelModel frame = _frames[from];
            _frames.RemoveAt(from);
            _frames.Insert(to, frame);
            StructureChanged?.Invoke();
        }

        // --- 圖層操作（每個操作都作用在所有幀）---------------------------------------------------------------
        private void TouchAllFrames()
        {
            foreach (VoxelModel f in _frames) { f.NotifyChanged(); }
            LayersChanged?.Invoke();
        }

        /// <summary>目前圖層結構的快照，給復原用（<see cref="RestoreLayers"/>）。</summary>
        public LayerSnapshot CaptureLayers()
        {
            LayerSnapshot s = new LayerSnapshot { Layers = new List<Layer>(_layers), Names = new string[_layers.Count], Visible = new bool[_layers.Count] };
            for (int k = 0; k < _layers.Count; k++) { s.Names[k] = _layers[k].Name; s.Visible[k] = _layers[k].Visible; }
            foreach (VoxelModel f in _frames)
            {
                if (f.Cels != null) { s.Cels[f] = new List<VoxelModel>(f.Cels); }
                else { s.SingleData[f] = (byte[])f.Data.Clone(); }
            }
            return s;
        }

        public void RestoreLayers(LayerSnapshot s)
        {
            _layers.Clear();
            _layers.AddRange(s.Layers);
            for (int k = 0; k < _layers.Count; k++) { _layers[k].Name = s.Names[k]; _layers[k].Visible = s.Visible[k]; }
            foreach (VoxelModel f in _frames)
            {
                List<VoxelModel> cels;
                byte[] single;
                if (s.Cels.TryGetValue(f, out cels)) { f.AttachLayers(_layers, new List<VoxelModel>(cels)); }
                else if (s.SingleData.TryGetValue(f, out single)) { f.DetachLayers(); Buffer.BlockCopy(single, 0, f.Data, 0, single.Length); }
            }
            TouchAllFrames();
        }

        /// <summary>在 index 的位置（0 = 最底層）新增一個空白圖層。第一次新增時，原本的內容變成「圖層 1」。回傳新圖層的索引。</summary>
        public int AddLayer(int index, string name)
        {
            return AddLayerWithData(index, name, null);
        }

        /// <summary>新增圖層並指定每一幀的內容（data[i] 長度 = W*H*D；null 表示每幀都空白）。</summary>
        public int AddLayerWithData(int index, string name, IList<byte[]> data)
        {
            if (_layers.Count >= MaxLayers) { throw new InvalidOperationException(Loc.Get(TextKey.Animation_TheMaximumNumberOfLayers)); }

            index = Math.Max(0, Math.Min(index, _layers.Count));
            bool wasLayered = IsLayered;
            _layers.Insert(index, new Layer(name));
            for (int i = 0; i < _frames.Count; i++)
            {
                VoxelModel f = _frames[i];
                VoxelModel cel = NewCel();
                if (data != null && data[i] != null) { Buffer.BlockCopy(data[i], 0, cel.Data, 0, cel.Data.Length); }
                if (!wasLayered)
                {
                    VoxelModel c0 = NewCel();
                    Buffer.BlockCopy(f.Data, 0, c0.Data, 0, f.Data.Length);
                    List<VoxelModel> cels = new List<VoxelModel> { c0 };
                    cels.Insert(index, cel);
                    f.AttachLayers(_layers, cels);
                }
                else
                {
                    f.Cels.Insert(index, cel);
                    f.AttachLayers(_layers, f.Cels);
                }
            }
            TouchAllFrames();
            return index;
        }

        /// <summary>刪除一個圖層（至少要留一個）。刪到只剩一個時，所有幀回到單層。</summary>
        public void RemoveLayer(int index)
        {
            if (_layers.Count <= 1 || (uint)index >= (uint)_layers.Count) { return; }

            _layers.RemoveAt(index);
            foreach (VoxelModel f in _frames) { f.Cels.RemoveAt(index); }
            FinishLayerEdit();
        }

        /// <summary>每個操作做完：只剩一層就全部回到單層（那一層的內容就是 Data），否則重新合成。</summary>
        private void FinishLayerEdit()
        {
            if (_layers.Count == 1)
            {
                _layers[0].Visible = true;
                foreach (VoxelModel f in _frames)
                {
                    Buffer.BlockCopy(f.Cels[0].Data, 0, f.Data, 0, f.Data.Length);
                    f.DetachLayers();
                }
            }
            else
            {
                foreach (VoxelModel f in _frames) { f.AttachLayers(_layers, f.Cels); }
            }
            TouchAllFrames();
        }

        /// <summary>把第 from 個圖層搬到 to 的位置（0 = 最底層）。</summary>
        public void MoveLayer(int from, int to)
        {
            if (!IsLayered || (uint)from >= (uint)_layers.Count) { return; }

            to = Math.Max(0, Math.Min(to, _layers.Count - 1));
            if (from == to) { return; }

            Layer l = _layers[from];
            _layers.RemoveAt(from); _layers.Insert(to, l);
            foreach (VoxelModel f in _frames)
            {
                VoxelModel c = f.Cels[from];
                f.Cels.RemoveAt(from); f.Cels.Insert(to, c);
            }
            FinishLayerEdit();
        }

        /// <summary>複製第 index 個圖層，放在它的正上方。回傳新圖層的索引。</summary>
        public int DuplicateLayer(int index, string name)
        {
            if (_layers.Count >= MaxLayers) { throw new InvalidOperationException(Loc.Get(TextKey.Animation_TheMaximumNumberOfLayers)); }

            if (!IsLayered)
            {
                List<byte[]> copies = new List<byte[]>();
                foreach (VoxelModel f in _frames) { copies.Add((byte[])f.Data.Clone()); }
                return AddLayerWithData(1, name, copies);
            }

            int at = index + 1;
            _layers.Insert(at, new Layer(name));
            foreach (VoxelModel f in _frames) { f.Cels.Insert(at, f.Cels[index].Clone()); }
            FinishLayerEdit();
            return at;
        }

        /// <summary>
        /// 向下合併：第 index 個圖層疊到它下面那一個（index − 1）上，產生一個新圖層（沿用下面那個的名字）。
        /// 直接合併兩層的內容，不管顯示／隱藏；合併後的圖層一定是顯示的。
        /// </summary>
        public void MergeDown(int index)
        {
            if (!IsLayered || index < 1 || index >= _layers.Count) { return; }

            foreach (VoxelModel f in _frames)
            {
                VoxelModel lower = f.Cels[index - 1], upper = f.Cels[index];
                VoxelModel merged = NewCel();
                Buffer.BlockCopy(lower.Data, 0, merged.Data, 0, merged.Data.Length);
                byte[] u = upper.Data, m = merged.Data;
                for (int i = 0; i < u.Length; i++) { if (u[i] != 0) { m[i] = u[i]; } }
                f.Cels[index - 1] = merged;
                f.Cels.RemoveAt(index);
            }
            _layers.RemoveAt(index);
            _layers[index - 1].Visible = true;
            FinishLayerEdit();
        }

        /// <summary>拼合：把所有看得見的圖層疊成一個圖層（名字沿用最底層），隱藏的圖層會被丟掉。</summary>
        public void Flatten()
        {
            if (!IsLayered) { return; }

            foreach (VoxelModel f in _frames) { f.Recompose(); f.DetachLayers(); }
            Layer keep = _layers[0];
            _layers.Clear();
            keep.Visible = true;
            _layers.Add(keep);
            TouchAllFrames();
        }

        public void SetLayerVisible(int index, bool visible)
        {
            if ((uint)index >= (uint)_layers.Count || _layers[index].Visible == visible) { return; }

            _layers[index].Visible = visible;
            foreach (VoxelModel f in _frames) { if (f.Cels != null) { f.Recompose(); } }
            TouchAllFrames();
        }

        public void RenameLayer(int index, string name)
        {
            if ((uint)index >= (uint)_layers.Count) { return; }

            _layers[index].Name = name;
            LayersChanged?.Invoke();
        }

        // --- 存讀檔 ----------------------------------------------------------------------------------------
        // v5：v4 的全部內容之後，接著圖層：int 圖層數；只有圖層數 > 1 時才有後面：每個圖層「int 名稱位元組數 | UTF-8 名稱 | byte 顯示(0/1)」，
        //     然後每一幀依圖層順序各 W*H*D bytes 的圖格內容。（每幀前面的 Data 是合成結果，舊版程式也能讀到看得見的樣子。）
        // v4：跟 v3 一樣，只是骨架多了平移：「int 部件數」之後先接 3 × double 身體平移，每個部件的「double 角度」之後再接 3 × double 部件平移。
        // v3：v2 的全部內容之後，接著骨架與部件標記：
        //     int 部件數 | 每個部件：int 編號 | int 名稱位元組數 | UTF-8 名稱 | int 上一層編號 | int 軸(0 X/1 Y/2 Z) | 3 × double 樞軸 | double 角度
        //     | 每幀：byte 有沒有標記(0/1)，有的話接 W*H*D bytes 部件編號
        // v2："PSTK" | int 2 | int W | int H | int D | int fps | int 幀數 | 256 × int 調色盤 | 每幀 W*H*D bytes
        // v1（第一版，單幀）："PSTK" | int 1 | int W | int H | int D | 256 × int 調色盤 | W*H*D bytes，讀進來視為一幀的動畫。

        public void Save(string path)
        {
            using (FileStream fs = File.Create(path))
            using (BinaryWriter bw = new BinaryWriter(fs, Encoding.ASCII))
            {
                bw.Write(Encoding.ASCII.GetBytes(Magic));
                bw.Write(FileVersion);
                bw.Write(Width);
                bw.Write(Height);
                bw.Write(Depth);
                bw.Write(_fps);
                bw.Write(_frames.Count);
                for (int i = 0; i < 256; i++) { bw.Write(Palette[i]); }
                foreach (VoxelModel frame in _frames) { bw.Write(frame.Data); }

                bw.Write(Rig.Count);
                bw.Write(Rig.BodyX);
                bw.Write(Rig.BodyY);
                bw.Write(Rig.BodyZ);
                foreach (Bone b in Rig.Bones)
                {
                    byte[] name = Encoding.UTF8.GetBytes(b.Name ?? "");
                    bw.Write(b.Id);
                    bw.Write(name.Length);
                    bw.Write(name);
                    bw.Write(b.ParentId);
                    bw.Write((int)b.Axis);
                    bw.Write(b.PivotX);
                    bw.Write(b.PivotY);
                    bw.Write(b.PivotZ);
                    bw.Write(b.Angle);
                    bw.Write(b.OffsetX);
                    bw.Write(b.OffsetY);
                    bw.Write(b.OffsetZ);
                }
                foreach (VoxelModel frame in _frames)
                {
                    if (frame.Parts == null) { bw.Write((byte)0); }
                    else { bw.Write((byte)1); bw.Write(frame.Parts); }
                }

                bw.Write(_layers.Count);
                if (_layers.Count > 1)
                {
                    foreach (Layer l in _layers)
                    {
                        byte[] name = Encoding.UTF8.GetBytes(l.Name ?? "");
                        bw.Write(name.Length);
                        bw.Write(name);
                        bw.Write((byte)(l.Visible ? 1 : 0));
                    }
                    foreach (VoxelModel frame in _frames)
                    {
                        foreach (VoxelModel cel in frame.Cels) { bw.Write(cel.Data); }
                    }
                }
            }
        }

        public static Animation Load(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (BinaryReader br = new BinaryReader(fs, Encoding.ASCII))
            {
                string magic = Encoding.ASCII.GetString(br.ReadBytes(4));
                if (magic != Magic) { throw new InvalidDataException(Loc.Get(TextKey.Animation_NotAPixelStackingFile)); }

                int version = br.ReadInt32();
                if (version < 1 || version > FileVersion) { throw new InvalidDataException(Loc.Format(TextKey.Animation_UnsupportedVersion, version)); }

                int w = br.ReadInt32(), h = br.ReadInt32(), d = br.ReadInt32();
                int fps = 8, frameCount = 1;
                if (version >= 2)
                {
                    fps = br.ReadInt32();
                    frameCount = br.ReadInt32();
                }

                if (w < 1 || h < 1 || d < 1 || frameCount < 1
                    || (long)w * h * d * frameCount > MaxTotalCells)
                {
                    throw new InvalidDataException(Loc.Get(TextKey.Animation_TheFileSizeIsNot));
                }

                int[] palette = new int[256];
                for (int i = 0; i < 256; i++) { palette[i] = br.ReadInt32(); }

                Animation anim = new Animation(w, h, d, palette) { Fps = fps };
                for (int f = 0; f < frameCount; f++)
                {
                    VoxelModel frame = anim.NewFrame();
                    int read = br.Read(frame.Data, 0, frame.Data.Length);
                    if (read != frame.Data.Length) { throw new InvalidDataException(Loc.Get(TextKey.Animation_TheFileDataIsIncomplete)); }
                    anim._frames.Add(frame);
                }

                if (version >= 3)
                {
                    try { anim.ReadRig(br, version); }
                    catch (EndOfStreamException) { throw new InvalidDataException(Loc.Get(TextKey.Animation_TheFileDataIsIncomplete)); }
                }
                if (version >= 5)
                {
                    try { anim.ReadLayers(br); }
                    catch (EndOfStreamException) { throw new InvalidDataException(Loc.Get(TextKey.Animation_TheFileDataIsIncomplete)); }
                }
                return anim;
            }
        }

        /// <summary>讀 v5 的圖層。圖層數 1 = 沒有圖層資料；否則每個圖層的名稱與顯示狀態，再每幀每層的圖格內容。</summary>
        private void ReadLayers(BinaryReader br)
        {
            int count = br.ReadInt32();
            if (count < 1 || count > MaxLayers) { throw new InvalidDataException(Loc.Get(TextKey.Animation_TheNumberOfLayersIs)); }
            if (count == 1) { return; }
            if ((long)Width * Height * Depth * _frames.Count * (count + 1) > MaxTotalCells) { throw new InvalidDataException(Loc.Get(TextKey.Animation_TheFileSizeIsNot)); }

            List<Layer> layers = new List<Layer>();
            for (int k = 0; k < count; k++)
            {
                int nameLen = br.ReadInt32();
                if (nameLen < 0 || nameLen > 4096) { throw new InvalidDataException(Loc.Get(TextKey.Animation_TheLayerNameIsNot)); }

                byte[] name = br.ReadBytes(nameLen);
                if (name.Length != nameLen) { throw new InvalidDataException(Loc.Get(TextKey.Animation_TheFileDataIsIncomplete)); }

                layers.Add(new Layer(Encoding.UTF8.GetString(name)) { Visible = br.ReadByte() != 0 });
            }
            List<List<VoxelModel>> celsPerFrame = new List<List<VoxelModel>>();
            foreach (VoxelModel frame in _frames)
            {
                List<VoxelModel> cels = new List<VoxelModel>();
                for (int k = 0; k < count; k++)
                {
                    VoxelModel cel = NewCel();
                    if (br.Read(cel.Data, 0, cel.Data.Length) != cel.Data.Length) { throw new InvalidDataException(Loc.Get(TextKey.Animation_TheFileDataIsIncomplete)); }

                    cels.Add(cel);
                }
                celsPerFrame.Add(cels);
            }
            _layers.Clear();
            _layers.AddRange(layers);
            for (int i = 0; i < _frames.Count; i++) { _frames[i].AttachLayers(_layers, celsPerFrame[i]); }
        }

        private static bool IsFinite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        /// <summary>讀 v3／v4 的骨架與各幀部件標記。資料不合理（編號重複、上一層不存在、名稱過長…）一律當成壞檔。</summary>
        private void ReadRig(BinaryReader br, int version)
        {
            int count = br.ReadInt32();
            if (count < 0 || count > Rig.MaxBones) { throw new InvalidDataException(Loc.Get(TextKey.Animation_TheNumberOfPartsIs)); }

            double bodyX = 0, bodyY = 0, bodyZ = 0;
            if (version >= 4)
            {
                bodyX = br.ReadDouble(); bodyY = br.ReadDouble(); bodyZ = br.ReadDouble();
                if (!IsFinite(bodyX + bodyY + bodyZ)) { throw new InvalidDataException(Loc.Get(TextKey.Animation_TheBodyTranslationIsNot)); }
            }

            Bone[] bones = new Bone[count];
            HashSet<int> ids = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                Bone b = new Bone();
                b.Id = br.ReadInt32();
                int nameLen = br.ReadInt32();
                if (b.Id < 1 || b.Id > 255 || !ids.Add(b.Id) || nameLen < 0 || nameLen > 4096) { throw new InvalidDataException(Loc.Get(TextKey.Animation_ThePartDataIsNot)); }

                byte[] name = br.ReadBytes(nameLen);
                if (name.Length != nameLen) { throw new InvalidDataException(Loc.Get(TextKey.Animation_TheFileDataIsIncomplete)); }

                b.Name = Encoding.UTF8.GetString(name);
                b.ParentId = br.ReadInt32();
                int axis = br.ReadInt32();
                if (axis < 0 || axis > 2) { throw new InvalidDataException(Loc.Get(TextKey.Animation_ThePartSAxisIs)); }

                b.Axis = (BoneAxis)axis;
                b.PivotX = br.ReadDouble();
                b.PivotY = br.ReadDouble();
                b.PivotZ = br.ReadDouble();
                b.Angle = br.ReadDouble();
                if (version >= 4)
                {
                    b.OffsetX = br.ReadDouble();
                    b.OffsetY = br.ReadDouble();
                    b.OffsetZ = br.ReadDouble();
                }
                if (!IsFinite(b.PivotX + b.PivotY + b.PivotZ + b.Angle + b.OffsetX + b.OffsetY + b.OffsetZ))
                {
                    throw new InvalidDataException(Loc.Get(TextKey.Animation_ThePartSPivotAngle));
                }
                bones[i] = b;
            }
            foreach (Bone b in bones)
            {
                if (b.ParentId != 0 && !ids.Contains(b.ParentId)) { throw new InvalidDataException(Loc.Get(TextKey.Animation_ThePartSParentDoes)); }
            }
            Rig.Restore(new RigSnapshot { Bones = bones, BodyX = bodyX, BodyY = bodyY, BodyZ = bodyZ });
            for (int i = 0; i < count; i++)
            {
                if (Rig.WouldCycle(bones[i].Id, bones[i].ParentId) && bones[i].ParentId != 0) { throw new InvalidDataException(Loc.Get(TextKey.Animation_TheParentChildRelationshipsOf)); }
            }

            foreach (VoxelModel frame in _frames)
            {
                byte flag = br.ReadByte();
                if (flag == 0) { continue; }

                byte[] parts = new byte[frame.Data.Length];
                if (br.Read(parts, 0, parts.Length) != parts.Length) { throw new InvalidDataException(Loc.Get(TextKey.Animation_TheFileDataIsIncomplete)); }

                frame.SetParts(parts);
            }
        }
    }
}
