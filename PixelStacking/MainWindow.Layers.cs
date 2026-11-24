using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PixelStacking.Controls;
using PixelStacking.Data;
using PixelStacking.Render;

namespace PixelStacking
{
    /// <summary>
    /// 多圖層與 3D 魔術棒（部件）：圖層清單、新增／複製／刪除／搬移／合併／拼合／匯入、移動圖層內容，
    /// 以及用 3D 魔術棒選出立體相連的一塊、整塊移動、抽出成新圖層。
    ///
    /// 有多個圖層時，這一幀的 <see cref="VoxelModel.Data"/> 是合成結果，真正編輯的是目前圖層的圖格（<see cref="EditModel"/>）：
    /// 畫筆、填滿、選取、匯入圖片、膨脹、變換都作用在它上面；預覽、時間軸縮圖、播放、匯出看的都是合成結果。
    /// </summary>
    public partial class MainWindow
    {
        private int _activeLayer;
        private VoxelModel _backdropModel;

        private bool[] _wandSel;            // 3D 魔術棒目前選到的體素（索引同 EditModel.Data）
        private VoxelModel _wandOwner;      // 這個選取屬於哪個圖格

        /// <summary>目前要編輯的模型：多圖層時是目前圖層在這一幀的圖格，單層時就是這一幀本身。</summary>
        private VoxelModel EditModel
        {
            get
            {
                if (_current == null) { return null; }
                if (!_current.IsLayered) { return _current; }

                return _current.Cels[Math.Max(0, Math.Min(_activeLayer, _current.Cels.Count - 1))];
            }
        }

        private void InitLayerUi()
        {
            LayerListView.LayerSelected += k => SetActiveLayer(k);
            LayerListView.VisibilityToggled += (k, v) => { _anim.SetLayerVisible(k, v); SetDirty(true); };
            LayerListView.RenameRequested += k => RenameLayer(k);
            LayerView.WandClicked += OnWandClicked;
        }

        // --- 目前圖層 / 畫布與背景 ------------------------------------------------------------------------------
        /// <summary>讓畫布、每層縮圖列都改編輯「目前圖層在這一幀的圖格」，並更新背景參考（其他圖層合成）。</summary>
        private void ApplyEditModel()
        {
            VoxelModel m = EditModel;
            if (_wandSel != null && !ReferenceEquals(_wandOwner, m)) { ClearWand(); }

            LayerView.Model = m;
            LayerStripView.Model = m;
            UpdateBackdrop();
            LayerView.VoxelSelection = _wandSel;
        }

        private void UpdateBackdrop()
        {
            if (_current == null || !_current.IsLayered)
            {
                LayerView.Backdrop = null;
                return;
            }

            if (_backdropModel == null || _backdropModel.Width != _current.Width || _backdropModel.Height != _current.Height || _backdropModel.Depth != _current.Depth
                || !ReferenceEquals(_backdropModel.Palette, _current.Palette))
            {
                _backdropModel = new VoxelModel(_current.Width, _current.Height, _current.Depth, _current.Palette);
            }
            _current.ComposeInto(_backdropModel.Data, _activeLayer);
            _backdropModel.NotifyChanged();
            LayerView.Backdrop = _backdropModel;
        }

        private void RefreshLayerUi()
        {
            if (_anim == null) { return; }

            _activeLayer = Math.Max(0, Math.Min(_activeLayer, _anim.LayerCount - 1));
            LayerListView.Set(_anim.Layers, _activeLayer);
            ApplyEditModel();
            UpdateLayerText();
            UpdateLayerButtons();
        }

        private void UpdateLayerButtons()
        {
            bool layered = _anim != null && _anim.IsLayered;
            LayerDeleteButton.IsEnabled = layered;
            LayerUpButton.IsEnabled = layered && _activeLayer < _anim.LayerCount - 1;
            LayerDownButton.IsEnabled = layered && _activeLayer > 0;
            LayerMergeButton.IsEnabled = layered && _activeLayer > 0;
            LayerAddButton.IsEnabled = _anim != null && _anim.LayerCount < Animation.MaxLayers;
            LayerDupButton.IsEnabled = _anim != null && _anim.LayerCount < Animation.MaxLayers;
            LayerHeader.Text = _anim == null ? LangResources.Get(TextKey.Lbl_Layers) : LangResources.Format(TextKey.Layers_Header, _anim.LayerCount);
        }

        private void SetActiveLayer(int k)
        {
            if (_anim == null) { return; }

            k = Math.Max(0, Math.Min(k, _anim.LayerCount - 1));
            if (k == _activeLayer) { return; }

            _activeLayer = k;
            LayerView.ClearSelection();
            RefreshLayerUi();
        }

        private void OnLayersChanged() { RefreshLayerUi(); }

        // --- 圖層指令（全部算一次復原）--------------------------------------------------------------------------------
        /// <summary>
        /// 做一個圖層結構的操作：先存快照，op 回傳操作後要編輯的圖層索引。整個操作算一次復原（復原＝還原快照，連圖層內容都回來）。
        /// 回傳有沒有成功（圖層數超過上限等例外會顯示訊息、不進復原）。
        /// </summary>
        private bool RunLayerOp(Func<int> op)
        {
            LayerView.ClearSelection();
            ClearWand();
            LayerSnapshot snap = _anim.CaptureLayers();
            int oldActive = _activeLayer;
            VoxelModel frame = _current;
            int newActive;
            try { newActive = op(); }
            catch (InvalidOperationException ex) { ClipText.Text = ex.Message; return false; }

            _activeLayer = Math.Max(0, Math.Min(newActive, _anim.LayerCount - 1));
            PushUndo(() =>
            {
                _anim.RestoreLayers(snap);
                _activeLayer = Math.Max(0, Math.Min(oldActive, _anim.LayerCount - 1));
                RefreshLayerUi();
            }, frame);
            RefreshLayerUi();
            return true;
        }

        private string UniqueLayerName(string baseName)
        {
            HashSet<string> used = new HashSet<string>();
            foreach (Layer l in _anim.Layers) { used.Add(l.Name); }

            if (!used.Contains(baseName)) { return baseName; }

            for (int n = 2; ; n++)
            {
                string name = $"{baseName} {n}";
                if (!used.Contains(name)) { return name; }
            }
        }

        private string NextLayerName()
        {
            for (int n = _anim.LayerCount + 1; ; n++)
            {
                string name = LangResources.Format(TextKey.Layers_DefaultName, n);
                bool taken = false;
                foreach (Layer l in _anim.Layers) { if (l.Name == name) { taken = true; break; } }
                if (!taken) { return name; }
            }
        }

        private void LayerAdd_Click(object sender, RoutedEventArgs e) { AddLayerCommand(); }
        private void LayerDup_Click(object sender, RoutedEventArgs e) { DuplicateLayerCommand(); }
        private void LayerDelete_Click(object sender, RoutedEventArgs e) { DeleteLayerCommand(); }
        private void LayerUp_Click(object sender, RoutedEventArgs e) { MoveLayerCommand(1); }
        private void LayerDown_Click(object sender, RoutedEventArgs e) { MoveLayerCommand(-1); }
        private void LayerMerge_Click(object sender, RoutedEventArgs e) { MergeDownCommand(); }
        private void LayerFlatten_Click(object sender, RoutedEventArgs e) { FlattenCommand(); }

        internal bool AddLayerCommand()
        {
            return RunLayerOp(() => _anim.AddLayer(_activeLayer + 1, NextLayerName()));
        }

        internal bool DuplicateLayerCommand()
        {
            string name = UniqueLayerName(LangResources.Format(TextKey.Layers_CopyName, _anim.Layers[_activeLayer].Name));
            return RunLayerOp(() => _anim.DuplicateLayer(_activeLayer, name));
        }

        internal bool DeleteLayerCommand()
        {
            if (!_anim.IsLayered) { ClipText.Text = LangResources.Get(TextKey.Main_OnlyOneLayerIsLeft); return false; }

            int at = _activeLayer;
            return RunLayerOp(() => { _anim.RemoveLayer(at); return Math.Min(at, _anim.LayerCount - 1); });
        }

        internal bool MoveLayerCommand(int delta)
        {
            int from = _activeLayer, to = from + delta;
            if (!_anim.IsLayered || to < 0 || to >= _anim.LayerCount) { return false; }

            return RunLayerOp(() => { _anim.MoveLayer(from, to); return to; });
        }

        internal bool MergeDownCommand()
        {
            int at = _activeLayer;
            if (!_anim.IsLayered || at < 1) { ClipText.Text = LangResources.Get(TextKey.Main_TheBottomLayerHasNo); return false; }

            return RunLayerOp(() => { _anim.MergeDown(at); return at - 1; });
        }

        internal bool FlattenCommand()
        {
            if (!_anim.IsLayered) { return false; }

            bool anyHidden = false;
            foreach (Layer l in _anim.Layers) { if (!l.Visible) { anyHidden = true; } }
            if (anyHidden && MessageBox.Show(this, LangResources.Get(TextKey.Main_SomeLayersAreHiddenAnd), LangResources.Get(TextKey.Menu_FlattenAllLayers), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) { return false; }

            return RunLayerOp(() => { _anim.Flatten(); return 0; });
        }

        private void RenameLayer(int k)
        {
            Layer layer = _anim.Layers[k];
            NamePromptDialog dlg = new NamePromptDialog(LangResources.Get(TextKey.Main_LayerName), LangResources.Get(TextKey.Main_NameOfThisLayer), layer.Name,
                text => string.IsNullOrWhiteSpace(text) ? LangResources.Get(TextKey.Main_TheNameCannotBeEmpty) : null) { Owner = this };
            if (dlg.ShowDialog() != true) { return; }

            _anim.RenameLayer(k, dlg.Value.Trim());
            SetDirty(true);
        }

        // --- 匯入組件成圖層 -------------------------------------------------------------------------------------------
        private void LayerImport_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog { Filter = FileFilter, Title = LangResources.Get(TextKey.Common_ImportFileAsLayer) };
            if (dlg.ShowDialog(this) != true) { return; }

            ImportLayerFile(dlg.FileName);
        }

        /// <summary>
        /// 把另一個 .pstk（組件）讀進來，放在目前圖層的正上方成為新圖層：尺寸不同以中心對齊，顏色對應到目前的調色盤。
        /// 組件只有一幀＝每一幀都放同一份，有好幾幀就循環。整個動作算一次復原。回傳有沒有成功。
        /// </summary>
        internal bool ImportLayerFile(string path)
        {
            Animation src;
            try { src = Animation.Load(path); }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(this, ex.Message, LangResources.Get(TextKey.Main_ImportFailed), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (_anim.LayerCount >= Animation.MaxLayers) { ClipText.Text = LangResources.Get(TextKey.Main_TheMaximumNumberOfLayers); return false; }

            ImageImport.MapStats stats;
            List<byte[]> data = LayerImport.FromAnimation(_anim, src, out stats);
            string name = UniqueLayerName(System.IO.Path.GetFileNameWithoutExtension(path));
            bool ok = RunLayerOp(() => _anim.AddLayerWithData(_activeLayer + 1, name, data));
            if (ok)
            {
                RebuildPalette();
                string tail = stats.Quantized ? LangResources.Format(TextKey.Layers_ColoursQuantized, stats.Slots) : stats.Added > 0 ? LangResources.Format(TextKey.Layers_NewColoursAdded, stats.Added) : "";
                string size = (src.Width != _anim.Width || src.Height != _anim.Height || src.Depth != _anim.Depth) ? LangResources.Format(TextKey.Layers_SizeCentred, src.Width, src.Height, src.Depth) : "";
                ClipText.Text = LangResources.Format(TextKey.Layers_Imported, name, size, tail);
            }
            return ok;
        }

        // --- 移動圖層內容 ---------------------------------------------------------------------------------------------
        private void MoveLayerContent_Click(object sender, RoutedEventArgs e)
        {
            MoveLayerDialog dlg = new MoveLayerDialog(_anim.Layers[_activeLayer].Name, Math.Max(_anim.Width, Math.Max(_anim.Height, _anim.Depth))) { Owner = this };
            if (dlg.ShowDialog() != true) { return; }

            MoveLayerContent(dlg.DX, dlg.DY, dlg.DZ, dlg.AllFrames);
        }

        /// <summary>把目前圖層的所有體素平移 (dx, dy, dz)（只動這一幀或所有幀）；超出模型的丟掉。整個動作算一次復原。回傳有沒有動到東西。</summary>
        internal bool MoveLayerContent(int dx, int dy, int dz, bool allFrames)
        {
            LayerView.ClearSelection();
            ClearWand();
            int layer = _activeLayer;
            List<VoxelModel> targets = new List<VoxelModel>();
            if (allFrames)
            {
                foreach (VoxelModel f in _anim.Frames) { targets.Add(f.IsLayered ? f.Cels[Math.Min(layer, f.Cels.Count - 1)] : f); }
            }
            else { targets.Add(EditModel); }

            List<KeyValuePair<VoxelModel, VoxelMove>> moves = new List<KeyValuePair<VoxelModel, VoxelMove>>();
            int clipped = 0, moved = 0;
            foreach (VoxelModel m in targets)
            {
                VoxelMove mv = VoxelOps.Shift(m.Data, m.Width, m.Height, m.Depth, null, dx, dy, dz, true);
                if (mv == null || mv.Indices.Length == 0) { continue; }

                m.NotifyChanged();
                moves.Add(new KeyValuePair<VoxelModel, VoxelMove>(m, mv));
                clipped += mv.Clipped; moved += mv.Moved;
            }
            if (moves.Count == 0) { ClipText.Text = LangResources.Get(TextKey.Main_ThisLayerHasNoVoxels); return false; }

            VoxelModel current = _current;
            PushUndo(() =>
            {
                foreach (KeyValuePair<VoxelModel, VoxelMove> kv in moves)
                {
                    for (int i = 0; i < kv.Value.Indices.Length; i++) { kv.Key.Data[kv.Value.Indices[i]] = kv.Value.Old[i]; }
                    kv.Key.NotifyChanged();
                }
            }, current, LayerView.View, LayerView.Slice);
            ClipText.Text = LangResources.Format(TextKey.Layers_Shifted, _anim.Layers[layer].Name, dx, dy, dz, moved, (allFrames ? LangResources.Format(TextKey.Layers_ShiftedFrames, moves.Count) : ""), (clipped > 0 ? LangResources.Format(TextKey.Layers_ShiftedDropped, clipped) : ""));
            return true;
        }

        // --- 3D 魔術棒（部件）-----------------------------------------------------------------------------------------
        private void ClearWand()
        {
            if (_wandSel == null) { return; }

            _wandSel = null;
            _wandOwner = null;
            if (LayerView != null) { LayerView.VoxelSelection = null; }
            UpdateWandText();
        }

        private void SetWand(bool[] sel, VoxelModel owner)
        {
            _wandSel = sel;
            _wandOwner = sel == null ? null : owner;
            LayerView.VoxelSelection = sel;
            UpdateWandText();
        }

        private void UpdateWandText()
        {
            if (Wand3DText == null) { return; }

            int n = VoxelOps.Count(_wandSel);
            Wand3DText.Text = n == 0
                ? LangResources.Get(TextKey.Main_ClickAVoxelToSelect)
                : LangResources.Format(TextKey.Layers_PartSelected, n);
        }

        internal void OnWandClicked(int index, bool add, bool subtract)
        {
            VoxelModel m = EditModel;
            bool[] comp = VoxelOps.Component(m.Data, m.Width, m.Height, m.Depth, index, Wand3DSameColorBox.IsChecked == true,
                Wand3DCornersBox.IsChecked == true ? WandConnectivity.Corners : WandConnectivity.Faces);
            if (comp == null)
            {
                if (!add && !subtract) { ClearWand(); }   // 點到空白：取消選取
                return;
            }

            bool[] result;
            if ((add || subtract) && _wandSel != null && ReferenceEquals(_wandOwner, m))
            {
                result = (bool[])_wandSel.Clone();
                for (int i = 0; i < result.Length; i++)
                {
                    if (comp[i]) { result[i] = add; }
                }
            }
            else { result = comp; }

            SetWand(VoxelOps.Count(result) == 0 ? null : result, m);
        }

        private void WandMove_Click(object sender, RoutedEventArgs e)
        {
            string[] parts = ((string)((Button)sender).Tag).Split(',');
            MoveWandSelection(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        }

        /// <summary>
        /// 把 3D 魔術棒選到的體素整塊搬動：right／down 是畫布上往右／往下幾格，toward 是往觀看者幾片（會依目前視角換成體素座標）。
        /// 搬出模型邊界的話整個不動。蓋到的體素會被取代。選取框跟著搬；切片滑桿也跟著，才看得到搬過去的東西。算一次復原。
        /// </summary>
        internal bool MoveWandSelection(int right, int down, int toward)
        {
            VoxelModel m = EditModel;
            if (_wandSel == null || !ReferenceEquals(_wandOwner, m)) { return false; }

            int[] ex, ey, es;
            ViewMapper.Axes(LayerView.View, out ex, out ey, out es);
            int dx = right * ex[0] + down * ey[0] + toward * es[0];
            int dy = right * ex[1] + down * ey[1] + toward * es[1];
            int dz = right * ex[2] + down * ey[2] + toward * es[2];

            bool[] oldSel = _wandSel;
            VoxelMove mv = VoxelOps.Shift(m.Data, m.Width, m.Height, m.Depth, _wandSel, dx, dy, dz, false);
            if (mv == null) { ClipText.Text = LangResources.Get(TextKey.Main_MovingFurtherWouldLeaveThe); return false; }
            if (mv.Indices.Length == 0) { return false; }

            m.NotifyChanged();
            ViewDirection view = LayerView.View;
            int slice = LayerView.Slice;
            SetWand(mv.NewMask, m);
            PushUndo(() =>
            {
                for (int i = 0; i < mv.Indices.Length; i++) { m.Data[mv.Indices[i]] = mv.Old[i]; }
                m.NotifyChanged();
                _wandSel = oldSel; _wandOwner = m;
                LayerView.VoxelSelection = oldSel;
                UpdateWandText();
            }, _current, view, slice);
            if (toward != 0) { LayerSlider.Value = Math.Max(0, Math.Min(LayerView.SliceCount - 1, LayerSlider.Value + toward)); }
            ClipText.Text = LangResources.Format(TextKey.Layers_PartMoved, mv.Moved);
            return true;
        }

        private void WandDelete_Click(object sender, RoutedEventArgs e) { DeleteWandSelection(); }

        /// <summary>刪除 3D 魔術棒選到的體素（算一次復原）。</summary>
        internal bool DeleteWandSelection()
        {
            VoxelModel m = EditModel;
            if (_wandSel == null || !ReferenceEquals(_wandOwner, m)) { return false; }

            bool[] oldSel = _wandSel;
            VoxelMove er = VoxelOps.Erase(m.Data, _wandSel);
            if (er.Indices.Length == 0) { return false; }

            m.NotifyChanged();
            SetWand(null, null);
            PushUndo(() =>
            {
                for (int i = 0; i < er.Indices.Length; i++) { m.Data[er.Indices[i]] = er.Old[i]; }
                m.NotifyChanged();
                _wandSel = oldSel; _wandOwner = m;
                LayerView.VoxelSelection = oldSel;
                UpdateWandText();
            }, _current, LayerView.View, LayerView.Slice);
            ClipText.Text = LangResources.Format(TextKey.Layers_PartDeleted, er.Indices.Length);
            return true;
        }

        private void WandSplit_Click(object sender, RoutedEventArgs e) { SplitSelectionToLayer(); }

        /// <summary>
        /// 把 3D 魔術棒選到的部件抽出來，放到目前圖層上方的新圖層（只有這一幀有內容，其他幀的新圖層是空的）。
        /// 選取跟著到新圖層，可以接著移動、拼到別的地方，最後再向下合併。整個動作算一次復原。
        /// </summary>
        internal bool SplitSelectionToLayer()
        {
            VoxelModel src = EditModel;
            if (_wandSel == null || !ReferenceEquals(_wandOwner, src)) { ClipText.Text = LangResources.Get(TextKey.Main_SelectAPartWithThe); return false; }
            if (_anim.LayerCount >= Animation.MaxLayers) { ClipText.Text = LangResources.Get(TextKey.Main_TheMaximumNumberOfLayers); return false; }

            LayerView.ClearSelection();
            bool[] sel = _wandSel;
            LayerSnapshot snap = _anim.CaptureLayers();
            int oldActive = _activeLayer;
            VoxelModel frame = _current;
            int frameIndex = _anim.IndexOf(frame);

            byte[] extracted = VoxelOps.Extract(src.Data, sel);
            VoxelMove er = VoxelOps.Erase(src.Data, sel);
            List<byte[]> data = new List<byte[]>();
            for (int i = 0; i < _anim.Count; i++) { data.Add(i == frameIndex ? extracted : null); }
            string name = UniqueLayerName(LangResources.Get(TextKey.Lbl_Part));
            int newIndex = _anim.AddLayerWithData(oldActive + 1, name, data);
            _activeLayer = newIndex;
            ClearWand();
            RefreshLayerUi();
            SetWand((bool[])sel.Clone(), EditModel);

            PushUndo(() =>
            {
                _anim.RestoreLayers(snap);
                for (int i = 0; i < er.Indices.Length; i++) { src.Data[er.Indices[i]] = er.Old[i]; }
                src.NotifyChanged();
                _activeLayer = Math.Max(0, Math.Min(oldActive, _anim.LayerCount - 1));
                _wandSel = sel; _wandOwner = src;
                RefreshLayerUi();
                LayerView.VoxelSelection = sel;
                UpdateWandText();
            }, frame);
            ClipText.Text = LangResources.Format(TextKey.Layers_PartExtracted, er.Indices.Length, name);
            return true;
        }

        private void Wand3DOption_Changed(object sender, RoutedEventArgs e) { }

        /// <summary>鍵盤：3D 魔術棒有選取時的方向鍵、Delete、Esc。回傳有沒有處理。</summary>
        private bool HandleWandKey(System.Windows.Input.Key key, bool ctrl)
        {
            if (LayerView.Tool != PaintTool.Wand3D || _wandSel == null) { return false; }

            switch (key)
            {
                case System.Windows.Input.Key.Left: MoveWandSelection(-1, 0, 0); return true;
                case System.Windows.Input.Key.Right: MoveWandSelection(1, 0, 0); return true;
                case System.Windows.Input.Key.Up: if (ctrl) { MoveWandSelection(0, 0, 1); } else { MoveWandSelection(0, -1, 0); } return true;
                case System.Windows.Input.Key.Down: if (ctrl) { MoveWandSelection(0, 0, -1); } else { MoveWandSelection(0, 1, 0); } return true;
                case System.Windows.Input.Key.Delete: DeleteWandSelection(); return true;
                case System.Windows.Input.Key.Escape: ClearWand(); return true;
            }
            return false;
        }
    }
}
