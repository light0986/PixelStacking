using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PixelStacking.Controls;
using PixelStacking.Data;
using PixelStacking.Render;

namespace PixelStacking
{
    /// <summary>
    /// 三欄編輯器 + 底部時間軸：左 = 調色盤與筆刷、中 = 目前幀目前層的畫布 + 層數滑桿、右 = 即時偽 3D 預覽、
    /// 下 = 幀縮圖時間軸與播放控制。
    /// 預覽與播放都由 CompositionTarget.Rendering 驅動（跟螢幕同步）：角度、播放進度都依真實經過時間累加，
    /// 內容、角度、俯角、顯示範圍都沒變就不重繪。
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string FileFilter = "PixelStacking (*.pstk)|*.pstk";
        private const double AutoRotateSpeed = 0.8;   // 弧度/秒

        // Margin：預覽固定在模型四周留好最粗外輪廓的空間，開關輪廓或換線寬都不用重配緩衝與 Bitmap
        private readonly StackRenderer _renderer = new StackRenderer { Margin = OutlineOptions.MaxThickness };
        private readonly UndoStack _undo = new UndoStack();
        private readonly List<Border> _swatches = new List<Border>();

        private Animation _anim;
        private VoxelModel _current;   // 正在編輯的幀
        private int _selected;
        private string _filePath;
        private bool _ready;

        // 播放
        private bool _playing;
        private int _playIndex;
        private double _playAcc;   // 累積的「幀」數，滿 1 就前進一幀（改 fps 時不會跳）

        // 預覽視角與「上一次實際畫出來的狀態」（用來判斷要不要重繪）
        private double _yaw = 0.6;
        private double _elev = 50 * Math.PI / 180;
        private VoxelModel _drawnModel;
        private int _drawnVersion = -1;
        private double _drawnYaw = double.NaN, _drawnElev = double.NaN;
        private ClipRange _drawnClip;
        private RenderMode _drawnMode = RenderMode.Stacked;   // 初始值故意跟預設模式不同，第一幀一定會畫
        private bool _drawnShade;
        private int _drawnOutline = -1;   // 輪廓線設定的簽章（見 OutlineSignature）
        private TimeSpan _lastRenderingTime = TimeSpan.MinValue;

        private bool _dragging;
        private Point _dragStart;
        private double _dragYaw, _dragElev;

        public MainWindow()
        {
            LocHook.Install();
            LoadSavedLanguage();
            InitializeComponent();
            LocHook.Watch(this);
            MaximizeHook.Attach(this);
            BuildPalette();
            LayerView.StrokeEnded += OnStrokeEnded;
            LayerView.ZoomChanged += SyncCanvasZoomBox;
            PreviewCubeView.ViewPicked += SetPreviewView;
            LayerView.ScrollInfoChanged += SyncCanvasScrollBars;
            InitLayerUi();
            LayerView.SelectionChanged += UpdateSelectionText;
            LayerView.HoverVoxel += LayerView_HoverVoxel;
            Timeline.FrameClicked += index => SelectFrame(index);
            LayerStripView.SliceClicked += s => { LayerSlider.Value = s; };
            LayerStripView.SliceMoved += MoveSlice;
            _ready = true;

            SetAnimation(new Animation(32, 32, 32));
            Picker.Argb = _anim.Palette[1];
            UpdateSwatchHighlight();
            ApplyOptions();
            ViewHint.Text = ViewMapper.Describe(LayerView.View);
            RestoreExpanders();
            UpdateLanguageChecks();
            Closing += Window_Closing;

            CompositionTarget.Rendering += OnRendering;
            Closed += (s, e) => CompositionTarget.Rendering -= OnRendering;
        }

        // --- 左欄大類的收合狀態（記在 %AppData%\PixelStacking\ui.txt）---------------------------------------
        private Expander[] SectionExpanders() => new[] { ViewExpander, ColorExpander, ToolExpander, BrushExpander, DisplayExpander, LayerExpander, TimelineExpander };

        /// <summary>啟動時套用上次的收合狀態（沒記錄的大類維持預設＝展開），之後每次展開／收合立刻存檔。</summary>
        private void RestoreExpanders()
        {
            var saved = UiState.Load();
            foreach (Expander ex in SectionExpanders())
            {
                bool open;
                if (saved.TryGetValue(ex.Name, out open)) { ex.IsExpanded = open; }
                ex.Expanded += Expander_Toggled;
                ex.Collapsed += Expander_Toggled;
            }

            // 調色盤停在哪一頁（RGB / HSV）也一起記住
            bool hsv;
            if (saved.TryGetValue(ColorTabKey, out hsv)) { Picker.HsvMode = hsv; }
            Picker.ModeChanged += () => Expander_Toggled(null, null);

            // 透明棋盤的深淺（預設淺色）
            bool light;
            if (saved.TryGetValue(LightCheckerKey, out light)) { LightCheckerBox.IsChecked = light; }

            // 預覽的輪廓線設定（開關、線寬、深淺）也記住；沒記錄的維持預設（外輪廓與面折線開、線寬 1、深淺中）
            bool b;
            if (saved.TryGetValue("OutlineSilhouette", out b)) { OutlineSilhouetteBox.IsChecked = b; }
            if (saved.TryGetValue("OutlineCreases", out b)) { OutlineCreasesBox.IsChecked = b; }
            if (saved.TryGetValue("OutlineWidth2", out b) && b) { OutlineWidth2Radio.IsChecked = true; }
            if (saved.TryGetValue("OutlineWidth3", out b) && b) { OutlineWidth3Radio.IsChecked = true; }
            if (saved.TryGetValue("OutlineToneLight", out b) && b) { OutlineToneLightRadio.IsChecked = true; }
            if (saved.TryGetValue("OutlineToneDark", out b) && b) { OutlineToneDarkRadio.IsChecked = true; }
            foreach (CheckBox cb in new[] { OutlineSilhouetteBox, OutlineCreasesBox }) { cb.Checked += Expander_Toggled; cb.Unchecked += Expander_Toggled; }
            foreach (RadioButton rb in new[] { OutlineWidth1Radio, OutlineWidth2Radio, OutlineWidth3Radio, OutlineToneLightRadio, OutlineToneMediumRadio, OutlineToneDarkRadio }) { rb.Checked += Expander_Toggled; }
        }

        private const string ColorTabKey = "ColorTabHsv";
        private const string LightCheckerKey = "LightChecker";

        private void Expander_Toggled(object sender, RoutedEventArgs e)
        {
            var map = UiState.Load();   // 先讀舊的：保留未來可能新增、這版不認得的項目
            foreach (Expander ex in SectionExpanders()) { map[ex.Name] = ex.IsExpanded; }
            map[ColorTabKey] = Picker.HsvMode;
            map[LightCheckerKey] = LightCheckerBox.IsChecked == true;
            map["OutlineSilhouette"] = OutlineSilhouetteBox.IsChecked == true;
            map["OutlineCreases"] = OutlineCreasesBox.IsChecked == true;
            map["OutlineWidth2"] = OutlineWidth2Radio.IsChecked == true;
            map["OutlineWidth3"] = OutlineWidth3Radio.IsChecked == true;
            map["OutlineToneLight"] = OutlineToneLightRadio.IsChecked == true;
            map["OutlineToneDark"] = OutlineToneDarkRadio.IsChecked == true;
            UiState.Save(map);
        }

        // --- 輪廓線（預覽與匯出共用同一份設定）----------------------------------------------------------------
        /// <summary>預覽面板目前的輪廓線設定（兩種線都關掉 = null＝不畫）。</summary>
        private OutlineOptions CurrentOutline()
        {
            OutlineOptions o = OutlineSettingsFromControls();
            return o.Any ? o : null;
        }

        private OutlineOptions OutlineSettingsFromControls()
        {
            return new OutlineOptions
            {
                Silhouette = OutlineSilhouetteBox.IsChecked == true,
                Creases = OutlineCreasesBox.IsChecked == true,
                Thickness = OutlineWidth3Radio.IsChecked == true ? 3 : OutlineWidth2Radio.IsChecked == true ? 2 : 1,
                Darkness = OutlineToneDarkRadio.IsChecked == true ? 35 : OutlineToneLightRadio.IsChecked == true ? 70 : 55,
            };
        }

        /// <summary>
        /// 給匯出對話框的輪廓線設定：寬度、深淺沿用預覽，兩種線都關著的話就全開（對話框裡的「輪廓線」勾選決定要不要用）。
        /// 對話框的勾選預設＝預覽有沒有開。
        /// </summary>
        private OutlineOptions OutlineForExport(out bool onInPreview)
        {
            OutlineOptions o = OutlineSettingsFromControls();
            onInPreview = o.Any;
            if (!o.Any) { o.Silhouette = true; o.Creases = true; }
            return o;
        }

        private static int OutlineSignature(OutlineOptions o)
        {
            return o == null ? 0 : 1 | (o.Silhouette ? 2 : 0) | (o.Creases ? 4 : 0) | (o.Thickness << 4) | (o.Darkness << 8);
        }

        // --- 文件 / 幀選取 ---------------------------------------------------------------------------------
        private void SetAnimation(Animation anim, string path = null)
        {
            StopPlayback();
            if (_anim != null) { _anim.StructureChanged -= OnStructureChanged; }

            if (_anim != null) { _anim.LayersChanged -= OnLayersChanged; }
            _anim = anim;
            _filePath = path;
            _activeLayer = anim.IsLayered ? anim.LayerCount - 1 : 0;
            ClearWand();
            anim.LayersChanged += OnLayersChanged;
            _undo.Clear();
            _current = null;

            Timeline.Animation = anim;                 // 時間軸先訂閱，結構改變時先重建縮圖
            anim.StructureChanged += OnStructureChanged;

            RebuildPalette();

            _renderer.Configure(anim.Width, anim.Height, anim.Depth);
            PreviewImage.Source = _renderer.Bitmap;
            _drawnModel = null;
            _drawnVersion = -1;

            SelectFrame(0);
            LayerSlider.Maximum = LayerView.SliceCount - 1;
            UpdateDepthMax();
            LayerSlider.Value = LayerView.SliceCount / 2;   // 新文件／開檔／啟動時，層數滑桿預設停在正中間那一層（奇數層剛好是中間，偶數層取較靠前的中間）
            UpdateLayerText();

            FpsSlider.Value = anim.Fps;
            UpdateFpsText();
            _dirty = false;   // 新增／開啟／啟動：還沒有任何修改
            UpdateStatus();
        }

        private void SelectFrame(int index)
        {
            index = Math.Max(0, Math.Min(index, _anim.Count - 1));
            _selected = index;
            _current = _anim.Frames[index];

            // 上一幀（第一幀的上一幀是最後一幀，動畫是循環的）當作參考
            LayerView.PrevFrame = _anim.Count > 1 ? _anim.Frames[(index - 1 + _anim.Count) % _anim.Count] : null;
            ApplyEditModel();
            LayerListView.Set(_anim.Layers, _activeLayer);
            UpdateLayerButtons();
            Timeline.SelectedIndex = index;
            UpdateStatus();
        }

        private void OnStructureChanged()
        {
            int i = _anim.IndexOf(_current);
            SelectFrame(i >= 0 ? i : Math.Min(_selected, _anim.Count - 1));
        }

        // 有沒有「上次儲存（或新增／開啟）之後」的修改：每一筆復原紀錄（畫筆、填滿、選取搬移、複製圖層、幀增刪搬移）、復原本身、
        // 動畫速度（fps）都算修改；存檔成功、新增、開啟檔案後歸零。狀態列檔名後面會多一個「*」。
        private bool _dirty;

        private void SetDirty(bool dirty)
        {
            if (_dirty == dirty) { return; }

            _dirty = dirty;
            if (_anim != null) { UpdateStatus(); }
        }

        private string DocumentName { get { return _filePath == null ? LangResources.Get(TextKey.Main_Untitled) : System.IO.Path.GetFileName(_filePath); } }

        /// <summary>
        /// 要丟掉目前文件之前（新增、開啟、關閉視窗）呼叫：沒有未儲存的修改就直接放行；有的話跳出提示，
        /// 選「儲存」會先存檔（存檔被取消或失敗就不放行）、「不儲存」放行、「取消」不放行。
        /// </summary>
        private bool ConfirmDiscard()
        {
            if (!_dirty) { return true; }

            SavePromptDialog dlg = new SavePromptDialog(DocumentName) { Owner = this };
            dlg.ShowDialog();
            switch (dlg.Choice)
            {
                case SavePromptChoice.Save: return SaveDocument(false);
                case SavePromptChoice.Discard: return true;
                default: return false;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!ConfirmDiscard()) { e.Cancel = true; }
        }

        private void UpdateStatus()
        {
            string name = DocumentName + (_dirty ? " *" : "");
            StatusText.Text = LangResources.Format(TextKey.Status_Document, name, _anim.Width, _anim.Height, _anim.Depth, _anim.Count);
            FrameText.Text = LangResources.Format(TextKey.Status_Frame, _selected + 1, _anim.Count);
            TimelineHeaderText.Text = LangResources.Format(TextKey.Status_TimelineHeader, _selected + 1, _anim.Count, _anim.Fps);
        }

        private void UpdateLayerText()
        {
            ViewDirection v = LayerView.View;
            string kind = (v == ViewDirection.Top || v == ViewDirection.Bottom) ? LangResources.Get(TextKey.Unit_SliceHorizontal) : LangResources.Get(TextKey.Unit_SliceVertical);
            LayerText.Text = LangResources.Format(TextKey.Status_SliceWithAxis, LayerView.Slice + 1, LayerView.SliceCount, kind, ViewMapper.SliceAxis(v));
            LayerStripView.Slice = LayerView.Slice;
            UpdateLayerHeader();
        }

        /// <summary>層數標題列：第幾層，後面接「N 層空白」（縮圖列算出來的），收起面板時也看得到還有幾層沒畫。</summary>
        private void UpdateLayerHeader()
        {
            ViewDirection v = LayerView.View;
            string kind = (v == ViewDirection.Top || v == ViewDirection.Bottom) ? LangResources.Get(TextKey.Unit_SliceHorizontal) : LangResources.Get(TextKey.Unit_SliceVertical);
            int empty = LayerStripView.EmptyCount;
            string text = LangResources.Format(TextKey.Status_SliceLine, LayerView.Slice + 1, LayerView.SliceCount, kind, (empty > 0 ? LangResources.Format(TextKey.Status_EmptySlices, empty, kind) : ""), (_anim != null && _anim.IsLayered ? LangResources.Format(TextKey.Status_ActiveLayer, _anim.Layers[Math.Min(_activeLayer, _anim.LayerCount - 1)].Name) : ""));
            if (LayerHeaderText.Text != text) { LayerHeaderText.Text = text; }
        }

        // --- 視角 ------------------------------------------------------------------------------------------
        private void View_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) { return; }

            ViewDirection v = (ViewDirection)Enum.Parse(typeof(ViewDirection), (string)((RadioButton)sender).Tag);
            ApplyView(v);
        }

        /// <summary>切換編輯畫布的視角：重設切片滑桿範圍、更新提示文字。</summary>
        private void ApplyView(ViewDirection v)
        {
            LayerView.View = v;
            LayerStripView.View = v;
            LayerSlider.Maximum = LayerView.SliceCount - 1;
            UpdateDepthMax();
            LayerSlider.Value = LayerView.Slice;
            ViewHint.Text = ViewMapper.Describe(v);
            UpdateLayerText();
            UpdateMirrorHint();   // 換視角後，哪些對稱軸是畫布上的線、哪個是沿視線方向，說明要跟著變
        }

        /// <summary>程式端切換視角（快捷鍵、復原）：連同左側的選項一起切。</summary>
        private void SelectViewUi(ViewDirection v)
        {
            foreach (RadioButton rb in FindViewRadios())
            {
                if ((string)rb.Tag == v.ToString()) { rb.IsChecked = true; return; }
            }
        }

        private IEnumerable<RadioButton> FindViewRadios()
        {
            UniformGrid grid = (UniformGrid)ViewTopRadio.Parent;
            foreach (UIElement child in grid.Children)
            {
                RadioButton rb = child as RadioButton;
                if (rb != null) { yield return rb; }
            }
        }

        private void LayerView_HoverVoxel(int x, int y, int z)
        {
            CoordText.Text = x < 0 ? LangResources.Get(TextKey.Lbl_Coordinates) : LangResources.Format(TextKey.Status_Coordinates, x + 1, y + 1, z + 1);
        }

        private void UpdateFpsText()
        {
            FpsText.Text = $"{_anim.Fps} fps";
        }

        // --- 左側：調色盤 / 選項 ---------------------------------------------------------------------------
        // 顏色模型：RGBA 調色盤（Picker）是「目前畫筆顏色」，隨便拖；真正畫下去（起筆／填滿）時才用 ResolvePaintIndex
        // 在共用調色盤裡找一模一樣的色格，沒有就佔用第一個空格。所以拖滑桿不會浪費色格，已經畫好的體素也不會被改色。
        private static Brush SwatchChecker { get { return ColorPicker.CreateCheckerBrush(4); } }   // 淺色／深色會變，不快取

        /// <summary>透明棋盤的深淺選項改了：畫布、顏色預覽／A 條、調色盤色塊一起換，並記住（下次開啟沿用）。</summary>
        private void Checker_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) { return; }

            ApplyChecker();
            Expander_Toggled(null, null);   // 跟收合狀態一起存進 ui.txt
        }

        private void ApplyChecker()
        {
            bool light = LightCheckerBox.IsChecked == true;
            LayerView.LightChecker = light;
            if (ColorPicker.LightChecker != light) { ColorPicker.SetLightChecker(light); }
            if (_transparentSwatch != null) { _transparentSwatch.Background = SwatchChecker; }
            if (_anim != null) { RebuildPalette(); }   // 色塊底下的棋盤要換
        }

        private void BuildPalette()
        {
            Picker.ColorChanged += Picker_ColorChanged;
            LayerView.PaintIndexProvider = ResolvePaintIndex;
            LayerView.ColorIndexProvider = ResolveIndexFor;
            LayerView.ColorPicked += LayerView_ColorPicked;
        }

        /// <summary>依目前文件的調色盤重畫色塊（透明的空格不顯示；開檔後會是檔案裡的顏色）。</summary>
        private void RebuildPalette()
        {
            PalettePanel.Children.Clear();
            _swatches.Clear();
            PalettePanel.Children.Add(TransparentSwatch());   // 第一格永遠是透明色
            int[] pal = _anim.Palette;
            for (int i = 1; i < pal.Length; i++)
            {
                if (((uint)pal[i] >> 24) != 0) { AddSwatch(i, pal[i]); }
            }
            UpdateSwatchHighlight();
        }

        private void AddSwatch(int index, int argb)
        {
            Border color = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)),
            };
            Border swatch = new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(1),
                BorderThickness = new Thickness(2),
                Background = SwatchChecker,
                Child = color,
                Tag = index,
            };
            swatch.MouseLeftButtonDown += Swatch_MouseDown;
            swatch.ContextMenu = SwatchMenu();
            swatch.ToolTip = "";
            swatch.ToolTipOpening += Swatch_ToolTipOpening;   // 提示是滑過去才算用量（要掃過所有幀）
            PalettePanel.Children.Add(swatch);
            _swatches.Add(swatch);
        }

        // --- 調色盤管理：清掉沒用到的／合併重複／換色／刪色／匯入匯出 ------------------------------------------------
        private ContextMenu _swatchMenu;

        /// <summary>色塊的右鍵選單（所有色塊共用同一個，開啟時的 PlacementTarget 就是被點的那一格）。</summary>
        private ContextMenu SwatchMenu()
        {
            if (_swatchMenu != null) { return _swatchMenu; }

            _swatchMenu = new ContextMenu();
            MenuItem replace = new MenuItem { Header = LangResources.Get(TextKey.Main_ReplaceWithTheCurrentBrush) };
            replace.ToolTip = LangResources.Get(TextKey.Main_ReplaceThisColourInEvery);
            replace.Click += (s, e) => { int i = SwatchIndexOf(s); if (i > 0) { ReplaceColourAt(i); } };
            MenuItem delete = new MenuItem { Header = LangResources.Get(TextKey.Main_DeleteThisColourVoxelsUsing) };
            delete.Click += (s, e) => { int i = SwatchIndexOf(s); if (i > 0) { DeleteColourAt(i); } };
            _swatchMenu.Items.Add(replace);
            _swatchMenu.Items.Add(delete);
            return _swatchMenu;
        }

        private static int SwatchIndexOf(object menuItem)
        {
            MenuItem mi = menuItem as MenuItem;
            ContextMenu cm = mi == null ? null : mi.Parent as ContextMenu;
            Border b = cm == null ? null : cm.PlacementTarget as Border;
            return b != null && b.Tag is int ? (int)b.Tag : -1;
        }

        private void Swatch_ToolTipOpening(object sender, ToolTipEventArgs e)
        {
            Border b = sender as Border;
            if (b == null || !(b.Tag is int)) { return; }

            int index = (int)b.Tag;
            int argb = _anim.Palette[index];
            int n = PaletteOps.CountUsage(_anim)[index];
            b.ToolTip = LangResources.Format(TextKey.Palette_SwatchTip, index, (argb >> 16) & 255, (argb >> 8) & 255, argb & 255, (int)((uint)argb >> 24), (n == 0 ? LangResources.Get(TextKey.Main_NoVoxelUsesThisColour) : LangResources.Format(TextKey.Palette_SwatchUsage, n)));
        }

        /// <summary>跑一個調色盤操作：沒有變動就說一聲；有的話進復原堆疊（調色盤跟體素一起還原）、重建色塊、更新畫面。</summary>
        private bool RunPaletteOp(Func<Animation, PaletteChange> op, Func<PaletteChange, string> done, string none)
        {
            PaletteChange change = op(_anim);
            if (change == null) { ClipText.Text = none; return false; }

            foreach (VoxelModel f in _anim.Frames) { f.NotifyChanged(); }
            PushUndo(() => { change.Undo(_anim); RebuildPalette(); }, null);
            RebuildPalette();
            ClipText.Text = LangResources.Format(TextKey.Palette_ResultWithUndo, done(change));
            return true;
        }

        private void PaletteClearUnused_Click(object sender, RoutedEventArgs e) { ClearUnusedColours(); }
        private void PaletteMerge_Click(object sender, RoutedEventArgs e) { MergeDuplicateColours(); }

        internal bool ClearUnusedColours()
        {
            return RunPaletteOp(PaletteOps.ClearUnused, c => LangResources.Format(TextKey.Palette_RemovedUnused, c.Affected), LangResources.Get(TextKey.Main_NoColoursToRemoveEvery));
        }

        internal bool MergeDuplicateColours()
        {
            return RunPaletteOp(PaletteOps.MergeDuplicates, c => LangResources.Format(TextKey.Palette_MergedDuplicates, c.Affected, c.VoxelCount), LangResources.Get(TextKey.Main_ThereAreNoDuplicateColours));
        }

        internal bool ReplaceColourAt(int index)
        {
            if (_transparentMode)
            {
                ClipText.Text = LangResources.Get(TextKey.Palette_TransparentNoReplace);
                return false;
            }
            int argb = Picker.Argb;
            return RunPaletteOp(a => PaletteOps.ReplaceColour(a, index, argb),
                c => c.Voxels.Count > 0 ? LangResources.Format(TextKey.Palette_SlotMerged, index, c.VoxelCount) : LangResources.Format(TextKey.Palette_SlotReplaced, index),
                LangResources.Get(TextKey.Main_ThisSlotIsAlreadyThe));
        }

        internal bool DeleteColourAt(int index)
        {
            return RunPaletteOp(a => PaletteOps.DeleteColour(a, index),
                c => LangResources.Format(TextKey.Palette_SlotDeleted, index, c.VoxelCount), LangResources.Get(TextKey.Main_ThisSlotIsAlreadyEmpty));
        }

        private void PaletteExport_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dlg = new SaveFileDialog { Filter = LangResources.Get(TextKey.Main_GIMPPaletteGplGpl), DefaultExt = ".gpl", FileName = _filePath != null ? System.IO.Path.GetFileNameWithoutExtension(_filePath) + ".gpl" : "palette.gpl" };
            if (dlg.ShowDialog(this) != true) { return; }

            ExportPaletteTo(dlg.FileName);
        }

        private void PaletteImport_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog { Filter = LangResources.Get(TextKey.Main_GIMPPaletteGplGplAll) };
            if (dlg.ShowDialog(this) != true) { return; }

            ImportPaletteFrom(dlg.FileName);
        }

        internal bool ExportPaletteTo(string path)
        {
            try
            {
                System.IO.File.WriteAllText(path, PaletteOps.ExportGpl(_anim, System.IO.Path.GetFileNameWithoutExtension(path)), new System.Text.UTF8Encoding(false));
                int n = 0; for (int i = 1; i < 256; i++) { if (!PaletteOps.IsFree(_anim.Palette[i])) { n++; } }
                ClipText.Text = LangResources.Format(TextKey.Palette_Exported, n, System.IO.Path.GetFileName(path));
                return true;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, LangResources.Get(TextKey.Main_ExportFailed), MessageBoxButton.OK, MessageBoxImage.Error); return false; }
        }

        internal bool ImportPaletteFrom(string path)
        {
            List<int> colours;
            try { colours = PaletteOps.ParseGpl(System.IO.File.ReadAllText(path)); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, LangResources.Get(TextKey.Main_ImportFailed), MessageBoxButton.OK, MessageBoxImage.Error); return false; }

            if (colours.Count == 0)
            {
                ClipText.Text = LangResources.Get(TextKey.Main_NoColoursWereFoundIn);
                return false;
            }

            PaletteOps.ImportStats stats = null;
            bool ok = RunPaletteOp(a => { PaletteOps.ImportStats st; PaletteChange c = PaletteOps.ImportColours(a, colours, out st); stats = st; return c; },
                c => LangResources.Format(TextKey.Palette_Imported, c.Affected),
                LangResources.Format(TextKey.Palette_NothingAdded, colours.Count));
            if (stats != null && stats.NoRoom > 0) { ClipText.Text += LangResources.Format(TextKey.Palette_ImportFull, stats.NoRoom); }
            return ok;
        }

        // 調色盤第一格：必定存在的「透明色」。選了它，畫筆／填滿畫下去的「顏色」就是透明（索引 0）＝清成透明，等同橡皮擦；
        // 它不佔調色盤的色格，也不會出現在存檔的調色盤裡。選別的色塊、或動了 RGBA／HSV 調色盤就回到一般顏色。
        private Border _transparentSwatch;
        private bool _transparentMode;

        private Border TransparentSwatch()
        {
            if (_transparentSwatch != null) { return _transparentSwatch; }

            Grid g = new Grid();
            g.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = 16, X2 = 16, Y2 = 0,   // 左下到右上的紅色斜線：一眼看出是「沒有顏色」
                Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)),
                StrokeThickness = 1.5,
                Stretch = Stretch.Fill,
            });
            _transparentSwatch = new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(1),
                BorderThickness = new Thickness(2),
                Background = SwatchChecker,
                Child = g,
                Tag = 0,
                ToolTip = LangResources.Get(TextKey.Main_TransparentColourWhenSelectedPainting),
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_transparentSwatch, "TransparentSwatch");
            System.Windows.Automation.AutomationProperties.SetName(_transparentSwatch, LangResources.Get(TextKey.Main_TransparentColour));
            _transparentSwatch.MouseLeftButtonDown += (s, e) =>
            {
                SetTransparentMode(true);
                Keyboard.Focus(this);
            };
            return _transparentSwatch;
        }

        /// <summary>吸管取到一格：透明格 → 選透明色；有顏色 → 畫筆顏色（RGBA／HSV 調色盤）變成那個顏色。</summary>
        private void LayerView_ColorPicked(byte index)
        {
            if (index == 0) { SetTransparentMode(true); return; }

            SetTransparentMode(false);
            Picker.Argb = _anim.Palette[index];
            UpdateSwatchHighlight();
        }

        /// <summary>切換「目前畫筆顏色是透明色」。</summary>
        private void SetTransparentMode(bool on)
        {
            _transparentMode = on;
            if (on)
            {
                PaletteNote.Text = LangResources.Get(TextKey.Palette_TransparentNote);
                PaletteNote.Visibility = Visibility.Visible;
            }
            else { PaletteNote.Visibility = Visibility.Collapsed; }
            UpdateSwatchHighlight();
        }

        private void Swatch_MouseDown(object sender, MouseButtonEventArgs e)
        {
            SetTransparentMode(false);
            Picker.Argb = _anim.Palette[(int)((Border)sender).Tag];
            Keyboard.Focus(this);   // 離開數值輸入框，快捷鍵才會恢復
            UpdateSwatchHighlight();
        }

        private void Picker_ColorChanged()
        {
            SetTransparentMode(false);   // 動了調色盤（RGBA／HSV）＝要用一般顏色畫
        }

        /// <summary>目前顏色剛好等於調色盤某一格時，把那格框起來。</summary>
        private void UpdateSwatchHighlight()
        {
            int argb = Picker.Argb;
            int[] pal = _anim.Palette;
            bool found = false;
            if (_transparentSwatch != null)
            {
                _transparentSwatch.BorderBrush = _transparentMode ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
            }
            foreach (Border b in _swatches)
            {
                bool on = !_transparentMode && !found && pal[(int)b.Tag] == argb;
                if (on) { found = true; }
                b.BorderBrush = on ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
            }
        }

        /// <summary>
        /// 目前畫筆顏色對應的調色盤索引：有一模一樣的就用，沒有就佔用第一個空格；調色盤 255 格全滿時改用最接近的顏色並提示。
        /// </summary>
        private byte ResolvePaintIndex() { return _transparentMode ? (byte)0 : ResolveIndexFor(Picker.Argb); }

        /// <summary>任意顏色對應的調色盤索引（規則同上）；魔術棒羽化混色出新顏色時也走這裡。</summary>
        private byte ResolveIndexFor(int argb)
        {
            int[] pal = _anim.Palette;

            for (int i = 1; i < pal.Length; i++)
            {
                if (pal[i] == argb) { return (byte)i; }
            }
            for (int i = 1; i < pal.Length; i++)
            {
                if (((uint)pal[i] >> 24) == 0)
                {
                    pal[i] = argb;
                    AddSwatch(i, argb);
                    UpdateSwatchHighlight();
                    return (byte)i;
                }
            }

            // 全滿：挑最接近的
            int best = 1;
            long bestDist = long.MaxValue;
            for (int i = 1; i < pal.Length; i++)
            {
                long d = 0;
                for (int sh = 0; sh < 32; sh += 8)
                {
                    long diff = ((argb >> sh) & 255) - ((pal[i] >> sh) & 255);
                    d += diff * diff;
                }
                if (d < bestDist) { bestDist = d; best = i; }
            }
            PaletteNote.Text = LangResources.Get(TextKey.Main_All255PaletteSlotsAre);
            PaletteNote.Visibility = Visibility.Visible;
            return (byte)best;
        }

        private void ApplyOptions()
        {
            if (!_ready) { return; }

            LayerView.Tool = Wand3DRadio.IsChecked == true ? PaintTool.Wand3D
                : LineRadio.IsChecked == true ? PaintTool.Line
                : RectRadio.IsChecked == true ? PaintTool.Rectangle
                : EllipseRadio.IsChecked == true ? PaintTool.Ellipse
                : PickerRadio.IsChecked == true ? PaintTool.Picker
                : WandRadio.IsChecked == true ? PaintTool.Wand
                : SelectRadio.IsChecked == true ? PaintTool.Select
                : FillRadio.IsChecked == true ? PaintTool.Fill
                : EraserRadio.IsChecked == true ? PaintTool.Eraser
                : PaintTool.Pencil;
            Wand3DPanel.Visibility = LayerView.Tool == PaintTool.Wand3D ? Visibility.Visible : Visibility.Collapsed;
            if (LayerView.Tool != PaintTool.Wand3D) { ClearWand(); } else { UpdateWandText(); }
            LayerView.Fill3D = Fill3DBox.IsChecked == true;
            LayerView.ShapeFilled = ShapeFilledBox.IsChecked == true;
            LayerView.MirrorX = MirrorXBox.IsChecked == true;
            LayerView.MirrorY = MirrorYBox.IsChecked == true;
            LayerView.MirrorZ = MirrorZBox.IsChecked == true;
            UpdateMirrorHint();
            LayerView.Brush.Shape = CapsuleRadio.IsChecked == true ? BrushShape.Capsule
                : SpringRadio.IsChecked == true ? BrushShape.Spring
                : HelixRadio.IsChecked == true ? BrushShape.Helix
                : TorusRadio.IsChecked == true ? BrushShape.Torus
                : PyramidRadio.IsChecked == true ? BrushShape.Pyramid
                : ConeRadio.IsChecked == true ? BrushShape.Cone
                : CylinderRadio.IsChecked == true ? BrushShape.Cylinder
                : EllipsoidRadio.IsChecked == true ? BrushShape.Ellipsoid
                : CubeRadio.IsChecked == true ? BrushShape.Cube
                : CircleRadio.IsChecked == true ? BrushShape.Circle
                : BrushShape.Square;
            SizeTitle.Text = LayerView.Brush.Shape == BrushShape.Spring ? LangResources.Get(TextKey.Main_SizeSpringRadius)
                : LayerView.Brush.Shape == BrushShape.Helix ? LangResources.Get(TextKey.Main_SizeHelixRadius)
                : LayerView.Brush.Shape == BrushShape.Torus ? LangResources.Get(TextKey.Main_SizeRingRadius)
                : LayerView.Brush.Shape == BrushShape.Pyramid ? LangResources.Get(TextKey.Main_SizeBaseSideLength)
                : LayerView.Brush.Shape == BrushShape.Cube ? LangResources.Get(TextKey.Lbl_SizeSideLength)
                : (LayerView.Brush.Shape == BrushShape.Capsule || LayerView.Brush.Shape == BrushShape.Ellipsoid || LayerView.Brush.Shape == BrushShape.Cylinder || LayerView.Brush.Shape == BrushShape.Cone) ? LangResources.Get(TextKey.Main_SizeRadius)
                : LayerView.Brush.Shape == BrushShape.Circle ? LangResources.Get(TextKey.Main_SizeDiameter) : LangResources.Get(TextKey.Lbl_SizeSideLength);
            LayerView.Brush.Size = (int)SizeSlider.Value;
            SizeText.Text = LayerView.Brush.Size.ToString();
            LayerView.PasteKeepTransparent = KeepTransparentBox.IsChecked == true;
            LayerView.WandGlobal = WandGlobalBox.IsChecked == true;
            LayerView.WandTolerance = (int)ToleranceSlider.Value;
            ToleranceText.Text = LayerView.WandTolerance.ToString();
            LayerView.WandFeather = (int)FeatherSlider.Value;
            FeatherText.Text = LayerView.WandFeather.ToString();
            LayerView.Brush.Depth = (int)DepthSlider.Value;
            DepthText.Text = LayerView.Brush.Depth.ToString();
            bool fillTool = LayerView.Tool == PaintTool.Fill;
            DepthHint.Text = (fillTool && LayerView.Fill3D)
                ? LangResources.Get(TextKey.Main_3DFillAlreadyConnects)
                : LayerView.Brush.Depth <= 1
                    ? (fillTool ? LangResources.Get(TextKey.Main_FillOnlyTheCurrentSlice) : LangResources.Get(TextKey.Lbl_DrawOnlyTheCurrentSlice))
                    : fillTool
                        ? LangResources.Format(TextKey.Hint_FillDepth, LayerView.Brush.Depth - 1)
                        : LangResources.Format(TextKey.Hint_DrawDepth, LayerView.Brush.Depth - 1);
            if (LayerView.Tool == PaintTool.Select || LayerView.Tool == PaintTool.Wand)
            {
                DepthHint.Text = LayerView.Brush.Depth <= 1 ? LangResources.Get(TextKey.Main_SelectionOnlyTheCurrentSlice) : LangResources.Format(TextKey.Hint_SelectDepth, LayerView.Brush.Depth - 1);
            }

            if (LayerView.Tool == PaintTool.Wand3D) { DepthHint.Text = LangResources.Get(TextKey.Main_3DMagicWandClick); }
            if (LayerView.Tool == PaintTool.Picker) { DepthHint.Text = LangResources.Get(TextKey.Main_EyedropperTakesOnlyTheColour); }

            // 立體筆刷（只有畫筆／橡皮擦用得到；填滿與選取仍然用深度當厚度）

            bool brushTool = LayerView.Tool == PaintTool.Pencil || LayerView.Tool == PaintTool.Eraser
                || LayerView.Tool == PaintTool.Line || LayerView.Tool == PaintTool.Rectangle || LayerView.Tool == PaintTool.Ellipse;
            bool cubeActive = brushTool && LayerView.Brush.Shape == BrushShape.Cube;   // 不吃深度的立體筆刷
            if (brushTool && LayerView.Brush.Shape == BrushShape.Cube)
            {
                DepthHint.Text = LangResources.Format(TextKey.Hint_Cube, LayerView.Brush.Size);
            }
            else if (brushTool && LayerView.Brush.Shape == BrushShape.Spring)
            {
                int p = LayerView.Brush.SpringPitch, d = LayerView.Brush.Depth;
                DepthHint.Text = LangResources.Format(TextKey.Hint_Spring, LayerView.Brush.Size, d, p, Math.Round((double)d / p, 1));
            }
            else if (brushTool && LayerView.Brush.Shape == BrushShape.Helix)
            {
                int p = LayerView.Brush.HelixPitch, d = LayerView.Brush.Depth;
                DepthHint.Text = LangResources.Format(TextKey.Hint_Helix, LayerView.Brush.Size, d, p, Math.Round((double)d / p, 1));
            }
            else if (brushTool && LayerView.Brush.Shape == BrushShape.Torus)
            {
                DepthHint.Text = LayerView.Brush.Depth <= 1
                    ? LangResources.Format(TextKey.Hint_TorusFlat, LayerView.Brush.Size)
                    : LangResources.Format(TextKey.Hint_Torus, LayerView.Brush.Size, LayerView.Brush.Depth, LayerView.Brush.Depth);
            }
            else if (brushTool && LayerView.Brush.Shape == BrushShape.Pyramid)
            {
                DepthHint.Text = LangResources.Format(TextKey.Hint_Pyramid, LayerView.Brush.Size, LayerView.Brush.Depth);
            }
            else if (brushTool && LayerView.Brush.Shape == BrushShape.Cone)
            {
                DepthHint.Text = LangResources.Format(TextKey.Hint_Cone, LayerView.Brush.Size, LayerView.Brush.Depth);
            }
            else if (brushTool && LayerView.Brush.Shape == BrushShape.Cylinder)
            {
                DepthHint.Text = LangResources.Format(TextKey.Hint_Cylinder, LayerView.Brush.Size, LayerView.Brush.Depth);
            }
            else if (brushTool && LayerView.Brush.Shape == BrushShape.Ellipsoid)
            {
                int r = LayerView.Brush.Size, d = LayerView.Brush.Depth;
                DepthHint.Text = d == 2 * r + 1 ? LangResources.Format(TextKey.Hint_EllipsoidBall, r, d)
                    : d < 2 * r + 1 ? LangResources.Format(TextKey.Hint_EllipsoidFlat, r, d)
                    : LangResources.Format(TextKey.Hint_EllipsoidLong, r, d);
            }
            else if (brushTool && LayerView.Brush.Shape == BrushShape.Capsule)
            {
                int r = LayerView.Brush.Size, len = LayerView.Brush.CapsuleLength;
                DepthHint.Text = len > LayerView.Brush.Depth
                    ? LangResources.Format(TextKey.Hint_CapsuleBall, r, 2 * r + 1)
                    : LangResources.Format(TextKey.Hint_Capsule, r, len);
            }
            DepthSlider.IsEnabled = !cubeActive;
            DepthSlider.Opacity = cubeActive ? 0.45 : 1.0;
            UpdateSelectionText();
            LayerView.ShowBelow = BelowBox.IsChecked == true;
            LayerView.ShowPrevFrame = PrevFrameBox.IsChecked == true;
            LayerView.ShowGrid = GridBox.IsChecked == true;
        }

        private void Option_Changed(object sender, RoutedEventArgs e) { ApplyOptions(); }

        /// <summary>鏡像開著時，說明目前視角下每個軸是什麼樣子（沿視線方向的軸，對稱位置在另一層，畫布上看不到）。</summary>
        private void UpdateMirrorHint()
        {
            if (!LayerView.MirrorAny) { MirrorHint.Visibility = Visibility.Collapsed; return; }

            ViewDirection v = LayerView.View;
            string hx = (v == ViewDirection.Left || v == ViewDirection.Right) ? "Y" : "X";
            string hy = (v == ViewDirection.Top || v == ViewDirection.Bottom) ? "Y" : "Z";
            string sight = (v == ViewDirection.Top || v == ViewDirection.Bottom) ? "Z" : (v == ViewDirection.Front || v == ViewDirection.Back) ? "Y" : "X";
            var parts = new List<string>();
            if (LayerView.MirrorX || LayerView.MirrorY || LayerView.MirrorZ)
            {
                if (IsMirrored(hx)) { parts.Add(LangResources.Format(TextKey.Hint_MirrorLeftRight, hx)); }
                if (IsMirrored(hy)) { parts.Add(LangResources.Format(TextKey.Hint_MirrorUpDown, hy)); }
                if (IsMirrored(sight)) { parts.Add(LangResources.Format(TextKey.Hint_MirrorSight, sight)); }
            }
            MirrorHint.Text = LangResources.Format(TextKey.Hint_MirrorCurrentView, string.Join("、", parts));
            MirrorHint.Visibility = Visibility.Visible;
        }

        private bool IsMirrored(string axis) { return axis == "X" ? LayerView.MirrorX : axis == "Y" ? LayerView.MirrorY : LayerView.MirrorZ; }

        private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { ApplyOptions(); }

        private void DepthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { ApplyOptions(); }

        private void ToleranceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { ApplyOptions(); }

        private void FeatherSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { ApplyOptions(); }

        /// <summary>筆刷深度的上限 = 目前視角的切片總數（換視角、換文件大小時要更新；目前值超過上限會被夾回）。</summary>
        private void UpdateDepthMax()
        {
            DepthSlider.Maximum = Math.Max(1, Math.Min(BrushSettings.MaxDepth, LayerView.SliceCount));
        }

        // --- 層數 ------------------------------------------------------------------------------------------
        private void LayerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) { return; }

            LayerView.Slice = (int)LayerSlider.Value;
            UpdateLayerText();
            UpdateSelectionText();
        }

        // --- 滑鼠滾輪：依游標所在的欄分工 ----------------------------------------------------------------
        private const double ZoomMin = 0.25, ZoomMax = 8, ZoomStepPerNotch = 1.12;
        private double _previewZoom = 1;
        private int _wheelLayerAcc;   // 換層用的滾輪累計量：每累積 120（一格）才換一層，高解析度滾輪／觸控板的小幅度滾動不會一下跳好幾層

        /// <summary>
        /// 游標在中間三欄 Grid 的哪一欄，滾輪就做那一欄的事（整欄範圍都算，不管底下是什麼控制項）：
        /// 左 = 捲動左側功能列；中 = 游標在畫布上時上下捲動（Shift 左右捲動、Ctrl 縮放），在層數與複製工具面板上時切換層數（滾輪向上 = 層數 +1）；右 = 拉近／拉遠 3D 預覽（滾輪向上 = 拉近）。
        /// </summary>
        private void MainGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double x = e.GetPosition(MainGrid).X;
            double leftEdge = MainGrid.ColumnDefinitions[0].ActualWidth;
            double rightStart = MainGrid.ActualWidth - MainGrid.ColumnDefinitions[2].ActualWidth;

            if (x < leftEdge)
            {
                double step = SystemParameters.WheelScrollLines * 16;   // 跟一般捲動區一樣：每一格滾輪捲幾行
                LeftScroll.ScrollToVerticalOffset(LeftScroll.VerticalOffset - e.Delta / 120.0 * step);
            }
            else if (x >= rightStart) { ZoomPreview(e.Delta); }
            else if (CanvasArea.IsMouseOver)
            {
                // 游標在畫布上：滾輪＝上下捲動、Shift＋滾輪＝左右捲動、Ctrl＋滾輪＝縮放（以游標位置為中心）。
                // 放大到超出視窗時才有東西可捲；換層改用 ←／→、[ ]、PageUp／PageDown。
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    Point anchor = LayerView.IsMouseOver ? e.GetPosition(LayerView) : new Point(LayerView.ActualWidth / 2, LayerView.ActualHeight / 2);   // 游標在捲軸上時以畫布中心為準
                    LayerView.ZoomStep(e.Delta > 0 ? 1 : -1, anchor);
                }
                else
                {
                    double px = e.Delta / 120.0 * 64;
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) { LayerView.ScrollBy(-px, 0); } else { LayerView.ScrollBy(0, -px); }
                }
            }
            else
            {
                _wheelLayerAcc += e.Delta;
                int steps = _wheelLayerAcc / 120;   // 向零取整：往上為正、往下為負
                _wheelLayerAcc -= steps * 120;
                if (steps != 0) { ChangeLayer(steps); }
            }

            e.Handled = true;
        }

        private bool _syncingScroll;

        /// <summary>畫布的捲動狀態變了（放大、拖曳平移、視窗大小改變）：更新兩條捲軸。沒超出視窗的那一條停用。</summary>
        private void SyncCanvasScrollBars()
        {
            double maxX, maxY, vw, vh, px, py;
            LayerView.GetScrollInfo(out maxX, out maxY, out vw, out vh, out px, out py);
            _syncingScroll = true;
            try
            {
                CanvasHScroll.Maximum = maxX; CanvasHScroll.ViewportSize = vw; CanvasHScroll.LargeChange = Math.Max(16, vw * 0.9); CanvasHScroll.Value = px; CanvasHScroll.IsEnabled = maxX > 0;
                CanvasVScroll.Maximum = maxY; CanvasVScroll.ViewportSize = vh; CanvasVScroll.LargeChange = Math.Max(16, vh * 0.9); CanvasVScroll.Value = py; CanvasVScroll.IsEnabled = maxY > 0;
            }
            finally { _syncingScroll = false; }
        }

        private void CanvasScroll_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready || _syncingScroll) { return; }

            LayerView.ScrollTo(CanvasHScroll.Value, CanvasVScroll.Value);
        }

        private bool _syncingZoomBox;

        private void CanvasZoom_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready || _syncingZoomBox) { return; }

            ComboBoxItem item = CanvasZoomBox.SelectedItem as ComboBoxItem;
            int z;
            if (item != null && int.TryParse(item.Tag as string, out z)) { LayerView.Zoom = z; }
        }

        /// <summary>畫布倍率被別的方式改了（Ctrl+滾輪）：讓倍率選單跟著顯示。</summary>
        private void SyncCanvasZoomBox()
        {
            _syncingZoomBox = true;
            try
            {
                foreach (ComboBoxItem item in CanvasZoomBox.Items)
                {
                    if (int.Parse((string)item.Tag) == LayerView.Zoom) { CanvasZoomBox.SelectedItem = item; break; }
                }
            }
            finally { _syncingZoomBox = false; }
        }

        /// <summary>3D 預覽的拉近／拉遠（視線與模型的距離）：每一格滾輪 ×1.12，範圍 25%~800%，以預覽中心為準。</summary>
        private void ZoomPreview(int delta)
        {
            SetPreviewZoom(_previewZoom * Math.Pow(ZoomStepPerNotch, delta / 120.0));
        }

        private void SetPreviewZoom(double zoom)
        {
            _previewZoom = Math.Max(ZoomMin, Math.Min(ZoomMax, zoom));
            PreviewScale.ScaleX = _previewZoom;
            PreviewScale.ScaleY = _previewZoom;
            PreviewHint.Text = LangResources.Format(TextKey.Hint_Preview, Math.Round(_previewZoom * 100));
        }

        private void ChangeLayer(int delta)
        {
            LayerSlider.Value = Math.Max(0, Math.Min(LayerView.SliceCount - 1, LayerSlider.Value + delta));
        }

        // --- 鍵盤 ------------------------------------------------------------------------------------------
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            // 檔案選單的快捷鍵：在輸入框（RGBA 數值框等）裡也要能用，所以放在輸入框判斷之前
            if (ctrl && shift && key == Key.N) { AddLayerCommand(); e.Handled = true; return; }
            if (ctrl && key == Key.N) { New_Click(null, null); e.Handled = true; return; }
            if (ctrl && key == Key.O) { Open_Click(null, null); e.Handled = true; return; }
            if (ctrl && key == Key.S) { SaveDocument(shift); e.Handled = true; return; }
            if (ctrl && shift && key == Key.I) { Inflate_Click(null, null); e.Handled = true; return; }
            if (ctrl && key == Key.I) { ImportImage_Click(null, null); e.Handled = true; return; }
            if (key == Key.F1) { Help_Click(null, null); e.Handled = true; return; }

            // 在輸入框裡打字、或選單開著時，空白、逗號、Ctrl+Z 等都要留給它，不能當成快捷鍵
            if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase || e.OriginalSource is MenuItem) { return; }

            if (key == Key.Z && ctrl) { DoUndo(); e.Handled = true; }
            else if (key == Key.C && ctrl) { CopyCommand(); e.Handled = true; }
            else if (key == Key.X && ctrl) { CutCommand(); e.Handled = true; }
            else if (key == Key.V && ctrl) { PasteCommand(); e.Handled = true; }
            else if (key == Key.A && ctrl) { SelectAllCommand(); e.Handled = true; }
            else if (key == Key.I && !ctrl && !shift) { PickerRadio.IsChecked = true; e.Handled = true; }
            else if (key == Key.L && !ctrl && !shift) { LineRadio.IsChecked = true; e.Handled = true; }
            else if (key == Key.R && !ctrl && !shift) { RectRadio.IsChecked = true; e.Handled = true; }
            else if (key == Key.E && !ctrl && !shift) { EllipseRadio.IsChecked = true; e.Handled = true; }
            else if (HandleWandKey(key, ctrl)) { e.Handled = true; }
            else if (key == Key.Escape && LayerView.IsShapeDragging) { LayerView.CancelShape(); e.Handled = true; }
            else if (key == Key.Delete && LayerView.HasSelection) { DeleteSelectionCommand(); e.Handled = true; }
            else if (key == Key.Escape && LayerView.HasSelection) { LayerView.CancelSelectionOrFloat(); e.Handled = true; }
            else if ((key == Key.Return || key == Key.Enter) && LayerView.HasFloating) { LayerView.CommitFloating(); e.Handled = true; }
            else if (LayerView.HasFloating && (key == Key.Left || key == Key.Right || key == Key.Up || key == Key.Down))
            {
                LayerView.NudgeFloating(key == Key.Left ? -1 : key == Key.Right ? 1 : 0, key == Key.Up ? -1 : key == Key.Down ? 1 : 0);
                e.Handled = true;
            }
            else if (key == Key.D && ctrl) { DuplicateSlice(shift ? -1 : 1); e.Handled = true; }
            else if (ctrl && key == Key.OemCloseBrackets) { RotateSelectionBy(15); e.Handled = true; }
            else if (ctrl && key == Key.OemOpenBrackets) { RotateSelectionBy(-15); e.Handled = true; }
            else if (key == Key.OemCloseBrackets || key == Key.PageUp) { ChangeLayer(1); e.Handled = true; }
            else if (!ctrl && !shift && !(Keyboard.FocusedElement is ComboBox) && (key == Key.Left || key == Key.Right)) { ChangeLayer(key == Key.Right ? 1 : -1); e.Handled = true; }   // 有浮動選取或 3D 魔術棒選取時，前面的分支已經把方向鍵拿去移動選取了
            else if (key == Key.OemOpenBrackets || key == Key.PageDown) { ChangeLayer(-1); e.Handled = true; }
            else if (key == Key.OemComma) { SelectFrame((_selected - 1 + _anim.Count) % _anim.Count); e.Handled = true; }
            else if (key == Key.OemPeriod) { SelectFrame((_selected + 1) % _anim.Count); e.Handled = true; }
            else if (key == Key.Space) { TogglePlayback(); e.Handled = true; }
            else if (key >= Key.D1 && key <= Key.D6)
            {
                SelectViewUi((ViewDirection)(key - Key.D1));   // 1 頂 2 底 3 正 4 背 5 左 6 右
                e.Handled = true;
            }
        }

        // --- 復原 ------------------------------------------------------------------------------------------
        private void PushUndo(Action apply, VoxelModel frame, ViewDirection? view = null, int slice = -1)
        {
            _undo.Push(new UndoEntry { Apply = apply, Frame = frame, View = view, Slice = slice });
            SetDirty(true);
        }

        /// <summary>一筆畫完：記下被改格子的原值，復原時逐格還原，並切回當時的幀 / 視角 / 切片。</summary>
        private void OnStrokeEnded(VoxelModel frame, ViewDirection view, int slice, int[] indices, byte[] old)
        {
            PushUndo(() =>
            {
                for (int i = 0; i < indices.Length; i++) { frame.Data[indices[i]] = old[i]; }
                frame.NotifyChanged();
            }, frame, view, slice);
        }

        private void DoUndo()
        {
            // 貼上／搬移還沒放下時，Ctrl+Z = 取消它（還沒寫進模型，所以不用動復原紀錄）
            if (LayerView.HasFloating) { LayerView.CancelFloating(); return; }

            UndoEntry entry;
            if (!_undo.TryPop(out entry)) { return; }

            entry.Apply();
            SetDirty(true);

            // 切到被還原的那一幀與那一層，使用者才看得到發生了什麼
            if (entry.Frame != null)
            {
                VoxelModel owner = entry.Frame.ParentFrame ?? entry.Frame;   // 圖格的復原紀錄要切到它所屬的那一幀與那個圖層
                int celIndex = entry.Frame.ParentFrame != null ? entry.Frame.CelIndex : -1;
                if (celIndex >= 0) { _activeLayer = celIndex; }
                int index = _anim.IndexOf(owner);
                if (index >= 0) { SelectFrame(index); }
            }
            if (entry.View.HasValue)
            {
                SelectViewUi(entry.View.Value);
                if (entry.Slice >= 0) { LayerSlider.Value = entry.Slice; }
            }
        }

        private void Undo_Click(object sender, RoutedEventArgs e) { DoUndo(); }

        // --- 複製圖層：複製 / 貼上 / 複製到 ±1 / 複製到範圍 -------------------------------------------------
        // 一律以「目前視角看到的那一層」為單位（SliceOps 的格子順序跟畫布一致），每個動作算一次復原。
        private byte[] _clipCells;
        private int _clipW, _clipH;

        private bool KeepTransparent => KeepTransparentBox.IsChecked == true;

        private string SliceUnit()
        {
            ViewDirection v = LayerView.View;
            return (v == ViewDirection.Top || v == ViewDirection.Bottom) ? LangResources.Get(TextKey.Unit_SliceHorizontal) : LangResources.Get(TextKey.Unit_SliceVertical);
        }

        private void CopySlice()
        {
            _clipCells = SliceOps.Read(EditModel, LayerView.View, LayerView.Slice);
            _clipW = LayerView.CanvasWidth;
            _clipH = LayerView.CanvasHeight;
            _lastClipIsBlock = false;
            ClipText.Text = LangResources.Format(TextKey.Msg_ClipboardSlice, ViewMapper.Name(LayerView.View), LayerView.Slice + 1, SliceUnit(), _clipW, _clipH);
        }

        private void PasteSlice()
        {
            if (_clipCells == null)
            {
                ClipText.Text = LangResources.Get(TextKey.Main_TheClipboardIsEmptyCopy);
                return;
            }
            if (_clipW != LayerView.CanvasWidth || _clipH != LayerView.CanvasHeight)
            {
                ClipText.Text = LangResources.Format(TextKey.Msg_ClipboardSizeMismatch, _clipW, _clipH, LayerView.CanvasWidth, LayerView.CanvasHeight);
                return;
            }

            WriteSlices(EditModel, LayerView.View, new[] { LayerView.Slice }, _clipCells, LayerView.Slice);
        }

        /// <summary>
        /// 縮圖列拖曳：把第 from 層搬到第 to 層（中間的各挪一格），目前這一幀、目前視角；一筆復原，復原＝搬回去。
        /// 編輯焦點跟著被搬的那一層走。
        /// </summary>
        private void MoveSlice(int from, int to)
        {
            LayerView.ClearSelection();   // 還沒放下的貼上／搬移先放下，才不會被一起搬亂

            VoxelModel frame = EditModel;
            ViewDirection view = LayerView.View;
            if (!SliceOps.Move(frame, view, from, to)) { return; }

            frame.NotifyChanged();
            PushUndo(() =>
            {
                SliceOps.Move(frame, view, to, from);
                frame.NotifyChanged();
            }, frame, view, from);

            LayerSlider.Value = to;
            ClipText.Text = LangResources.Format(TextKey.Msg_SliceMoved, from + 1, SliceUnit(), to + 1, SliceUnit());
        }

        /// <summary>把目前這層複製到相鄰的一層（delta = +1 / −1）並跳過去；已經在邊界就什麼都不做。</summary>
        private void DuplicateSlice(int delta)
        {
            ViewDirection view = LayerView.View;
            int src = LayerView.Slice, dst = src + delta;
            if (dst < 0 || dst >= LayerView.SliceCount) { return; }

            byte[] cells = SliceOps.Read(EditModel, view, src);
            WriteSlices(EditModel, view, new[] { dst }, cells, dst);
            LayerSlider.Value = dst;
        }

        /// <summary>把目前這層複製到 [from0, to0]（含頭尾、從 0 開始）內的每一層（目前這層自己略過）。</summary>
        private void CopyToRange(int from0, int to0)
        {
            ViewDirection view = LayerView.View;
            int src = LayerView.Slice;
            List<int> targets = new List<int>();
            for (int s = Math.Max(0, Math.Min(from0, to0)); s <= Math.Min(LayerView.SliceCount - 1, Math.Max(from0, to0)); s++)
            {
                if (s != src) { targets.Add(s); }
            }
            if (targets.Count == 0) { return; }

            WriteSlices(EditModel, view, targets, SliceOps.Read(EditModel, view, src), src);
        }

        /// <summary>把 cells 寫進多個切片，合併成一筆復原（復原後會切回 navSlice）。回傳有沒有真的改到東西。</summary>
        private bool WriteSlices(VoxelModel frame, ViewDirection view, IEnumerable<int> slices, byte[] cells, int navSlice, bool? keepTransparentOverride = null)
        {
            bool keep = keepTransparentOverride ?? KeepTransparent;
            List<SliceChange> parts = new List<SliceChange>();
            foreach (int s in slices) { parts.Add(SliceOps.Write(frame, view, s, cells, keep)); }

            SliceChange all = SliceChange.Combine(parts);
            if (all.Count == 0) { return false; }

            frame.NotifyChanged();
            OnStrokeEnded(frame, view, navSlice, all.Indices, all.Old);
            return true;
        }

        // --- 旋轉選取區（任意角度）---------------------------------------------------------------------------
        private void RotateSel_Click(object sender, RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            string tag = item == null ? null : item.Tag as string;
            if (tag == null) { return; }

            if (tag == "dialog")
            {
                if (!LayerView.HasSelection) { ClipText.Text = LangResources.Get(TextKey.Main_SelectTheAreaToRotate); return; }

                RotateDialog dlg = new RotateDialog(LayerView.FloatingAngle) { Owner = this };
                if (dlg.ShowDialog() == true) { RotateSelectionBy(dlg.Degrees); }
                return;
            }
            double deg;
            if (double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out deg)) { RotateSelectionBy(deg); }
        }

        /// <summary>旋轉選取區（沿視線方向、繞選取區中心；每一層一起轉）。沒有選取就提示一聲。</summary>
        internal bool RotateSelectionBy(double degrees)
        {
            if (!LayerView.RotateSelection(degrees))
            {
                ClipText.Text = LangResources.Get(TextKey.Main_SelectTheAreaToRotate);
                return false;
            }

            ClipText.Text = LangResources.Format(TextKey.Msg_SelectionRotated, LayerView.FloatingAngle.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }

        // --- 變換：翻轉／旋轉／平移 ---------------------------------------------------------------------------
        private void Transform_Click(object sender, RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            if (item != null && item.Tag is string) { RunTransform((string)item.Tag); }
        }

        /// <summary>
        /// 執行一個變換。tag = "範圍:動作"，範圍 layer（目前這一層）／frame（目前這一幀的每一層一起變）；
        /// 動作 flipH／flipV／rotCW／rotCCW／rot180／shift（跳出平移視窗）。一律以目前視角的畫布為準，整個動作算一次復原。
        /// 變換前會先把還沒放下的貼上／搬移放下並取消選取（選取區的位置在變換後就沒意義了）。回傳有沒有真的改到東西。
        /// </summary>
        private bool RunTransform(string tag)
        {
            string[] parts = tag.Split(':');
            bool all = parts[0] == "frame";
            string op = parts[1];
            string scope = all ? LangResources.Get(TextKey.Main_AllSlicesOfThisFrame) : LangResources.Get(TextKey.Main_ThisSlice);
            int cw = LayerView.CanvasWidth, ch = LayerView.CanvasHeight;

            Func<byte[], byte[]> fn;
            string done;
            switch (op)
            {
                case "flipH": fn = c => LayerTransform.FlipHorizontal(c, cw, ch); done = LangResources.Get(TextKey.Menu_FlipHorizontally); break;
                case "flipV": fn = c => LayerTransform.FlipVertical(c, cw, ch); done = LangResources.Get(TextKey.Menu_FlipVertically); break;
                case "rot180": fn = c => LayerTransform.Rotate180(c, cw, ch); done = LangResources.Get(TextKey.Menu_Rotate180); break;
                case "rotCW":
                case "rotCCW":
                    if (cw != ch)
                    {
                        ClipText.Text = LangResources.Format(TextKey.Msg_Rotate90NeedsSquare, cw, ch);
                        return false;
                    }
                    if (op == "rotCW") { fn = c => LayerTransform.RotateClockwise(c, cw, ch); done = LangResources.Get(TextKey.Main_Rotate90Clockwise); }
                    else { fn = c => LayerTransform.RotateCounterClockwise(c, cw, ch); done = LangResources.Get(TextKey.Main_Rotate90CounterClockwise); }
                    break;
                case "shift":
                {
                    ShiftDialog dlg = new ShiftDialog(scope, cw, ch) { Owner = this };
                    if (dlg.ShowDialog() != true) { return false; }

                    int dx = dlg.Dx, dy = dlg.Dy;
                    bool wrap = dlg.Wrap;
                    if (dx == 0 && dy == 0) { return false; }

                    fn = c => LayerTransform.Shift(c, cw, ch, dx, dy, wrap);
                    done = LangResources.Format(TextKey.Msg_ShiftDescription, dx, dy, (wrap ? LangResources.Get(TextKey.Main_WrapAround) : ""));
                    break;
                }
                default: return false;
            }

            return ApplyTransform(fn, all, done + scope);
        }

        /// <summary>把 fn 套用在目前這層（或這一幀的每一層），合併成一筆復原；完成後在剪貼簿提示列說一聲。</summary>
        private bool ApplyTransform(Func<byte[], byte[]> fn, bool allLayers, string doneText)
        {
            LayerView.ClearSelection();   // 還沒放下的貼上／搬移先放下，並取消選取

            ViewDirection view = LayerView.View;
            int count = LayerView.SliceCount, cur = LayerView.Slice;
            List<SliceChange> changes = new List<SliceChange>();
            for (int s = allLayers ? 0 : cur; s <= (allLayers ? count - 1 : cur); s++)
            {
                byte[] cells = SliceOps.Read(EditModel, view, s);
                changes.Add(SliceOps.Write(EditModel, view, s, fn(cells), false));
            }

            SliceChange all = SliceChange.Combine(changes);
            if (all.Count == 0)
            {
                ClipText.Text = LangResources.Format(TextKey.Msg_NoCellsChanged, doneText);
                return false;
            }

            EditModel.NotifyChanged();
            OnStrokeEnded(EditModel, view, cur, all.Indices, all.Old);
            ClipText.Text = LangResources.Format(TextKey.Msg_TransformDone, doneText);
            return true;
        }

        /// <summary>把目前畫布顯示的這一層全部清成透明（一筆復原）。跟「透明不覆蓋」勾選無關——清除一定要真的清掉。</summary>
        private bool ClearSlice()
        {
            byte[] empty = new byte[LayerView.CanvasWidth * LayerView.CanvasHeight];
            return WriteSlices(EditModel, LayerView.View, new[] { LayerView.Slice }, empty, LayerView.Slice, false);
        }

        private void Clear_Click(object sender, RoutedEventArgs e) { ClearSlice(); }

        // --- 選取：剪下 / 複製 / 貼上 / 全選 / 刪除 ----------------------------------------------------------
        // 剪貼簿有兩種：選取區的「區塊」（寬×高×層數）和整層（既有的圖層複製）。Ctrl+C 有選取區就複製選取區，
        // 沒有就複製整層（整層其實就是全選的特例）；Ctrl+V 貼的是「最後一次複製的那一種」。
        private PixelBlock _blockClip;
        private bool _lastClipIsBlock;

        private void CopyCommand()
        {
            if (LayerView.HasSelection)
            {
                PixelBlock block = LayerView.CopyBlock();
                if (block == null) { return; }

                _blockClip = block;
                _lastClipIsBlock = true;
                ClipText.Text = LangResources.Format(TextKey.Msg_ClipboardSelection, block.Width, block.Height, block.Depth);
            }
            else { CopySlice(); }
        }

        private void CutCommand()
        {
            if (!LayerView.HasSelection) { return; }

            PixelBlock block = LayerView.CopyBlock();
            if (block == null) { return; }

            _blockClip = block;
            _lastClipIsBlock = true;
            ClipText.Text = LangResources.Format(TextKey.Msg_ClipboardSelectionCut, block.Width, block.Height, block.Depth);
            LayerView.DeleteSelection();
        }

        private void PasteCommand()
        {
            if (_lastClipIsBlock && _blockClip != null)
            {
                SelectRadio.IsChecked = true;   // 貼上後要能拖曳，所以切到選取工具
                LayerView.BeginPaste(_blockClip);
            }
            else { PasteSlice(); }
        }

        private void SelectAllCommand()
        {
            SelectRadio.IsChecked = true;
            LayerView.SelectAll();
        }

        private void DeleteSelectionCommand()
        {
            if (LayerView.HasSelection) { LayerView.DeleteSelection(); }
        }

        private void UpdateSelectionText()
        {
            SelectText.Text = LayerView.DescribeSelection();
        }

        private void Cut_Click(object sender, RoutedEventArgs e) { CutCommand(); }

        private void Copy_Click(object sender, RoutedEventArgs e) { CopyCommand(); }

        private void Paste_Click(object sender, RoutedEventArgs e) { PasteCommand(); }

        private void SelectAll_Click(object sender, RoutedEventArgs e) { SelectAllCommand(); }

        private void DupNext_Click(object sender, RoutedEventArgs e) { DuplicateSlice(1); }

        private void DupPrev_Click(object sender, RoutedEventArgs e) { DuplicateSlice(-1); }

        private void Range_Click(object sender, RoutedEventArgs e)
        {
            int count = LayerView.SliceCount, cur = LayerView.Slice;
            RangeDialog dlg = new RangeDialog(count, Math.Min(cur + 2, count), count, SliceUnit(), cur + 1) { Owner = this };
            if (dlg.ShowDialog() == true) { CopyToRange(dlg.From - 1, dlg.To - 1); }
        }

        // --- 選單：檔案 ------------------------------------------------------------------------------------
        /// <summary>新增：跳出視窗選長／寬／高（預設是目前的大小），按「建立」才真的換成新的空白動畫。</summary>
        private void New_Click(object sender, RoutedEventArgs e)
        {
            NewDialog dlg = new NewDialog(_anim.Width, _anim.Height, _anim.Depth) { Owner = this };
            if (dlg.ShowDialog() != true) { return; }
            if (!ConfirmDiscard()) { return; }   // 確定要建立新的了，才問舊的要不要存（取消新增視窗不會被問）

            SetAnimation(new Animation(dlg.SizeX, dlg.SizeY, dlg.SizeZ));
        }

        private void Exit_Click(object sender, RoutedEventArgs e) { Close(); }

        // --- 選單：說明 ------------------------------------------------------------------------------------
        private HelpDialog _help;

        /// <summary>說明 → 使用教學（F1）：不擋住主視窗，可以邊看邊操作；已經開著就切到它前面。</summary>
        private void Help_Click(object sender, RoutedEventArgs e)
        {
            if (_help != null && _help.IsLoaded) { _help.Activate(); return; }

            _help = new HelpDialog { Owner = this };
            _help.Closed += (s, a) => _help = null;
            _help.Show();
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            new AboutDialog { Owner = this }.ShowDialog();
        }

        // --- 自製標題列：最小化 / 最大化（還原）/ 關閉 -----------------------------------------------------
        private void Minimize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e) { Close(); }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            bool max = WindowState == WindowState.Maximized;
            MaxButton.Content = max ? "" : "";   // 還原 / 最大化圖示（Segoe MDL2 Assets）
            MaxButton.ToolTip = max ? LangResources.Get(TextKey.Main_Undo) : LangResources.Get(TextKey.Tip_Maximize);

            // 最大化時視窗由 MaximizeHook 限制成剛好等於工作區（不蓋工作列、沒有溢出），所以內容不用再內縮；
            // 最大化時邊緣不需要縮放，也不用畫外框。
            WindowBorder.BorderThickness = max ? new Thickness(0) : new Thickness(1);
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog { Filter = FileFilter };
            if (dlg.ShowDialog(this) != true) { return; }
            if (!ConfirmDiscard()) { return; }

            try { SetAnimation(Animation.Load(dlg.FileName), dlg.FileName); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, LangResources.Get(TextKey.Main_OpenFailed), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void Save_Click(object sender, RoutedEventArgs e) { SaveDocument(false); }

        private void SaveAs_Click(object sender, RoutedEventArgs e) { SaveDocument(true); }

        /// <summary>存檔；還沒有檔名、或 forceDialog（另存新檔）時先跳出選擇檔案的視窗。</summary>
        private bool SaveDocument(bool forceDialog)
        {
            string path = _filePath;
            if (path == null || forceDialog)
            {
                SaveFileDialog dlg = new SaveFileDialog { Filter = FileFilter, DefaultExt = ".pstk" };
                if (_filePath != null)
                {
                    dlg.FileName = System.IO.Path.GetFileName(_filePath);
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(_filePath);
                }
                if (dlg.ShowDialog(this) != true) { return false; }
                path = dlg.FileName;
            }

            try
            {
                _anim.Save(path);
                _filePath = path;
                _dirty = false;
                UpdateStatus();
                return true;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, LangResources.Get(TextKey.Main_SaveFailed), MessageBoxButton.OK, MessageBoxImage.Error); return false; }
        }

        // --- 時間軸：幀操作 --------------------------------------------------------------------------------
        private void AddBlank_Click(object sender, RoutedEventArgs e) { InsertAfterCurrent(_anim.NewFrame()); }

        private void Duplicate_Click(object sender, RoutedEventArgs e) { InsertAfterCurrent(_current.Clone()); }

        private void InsertAfterCurrent(VoxelModel frame)
        {
            VoxelModel before = _current;
            int index = _selected + 1;

            _anim.Insert(index, frame);
            PushUndo(() => _anim.Remove(frame), before);
            SelectFrame(index);
        }

        private void DeleteFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_anim.Count <= 1) { return; }   // 至少留一幀

            VoxelModel frame = _current;
            int index = _anim.Remove(frame);
            if (index < 0) { return; }

            PushUndo(() => _anim.Insert(index, frame), frame);
        }

        private void MoveLeft_Click(object sender, RoutedEventArgs e) { MoveCurrent(-1); }

        private void MoveRight_Click(object sender, RoutedEventArgs e) { MoveCurrent(1); }

        private void MoveCurrent(int delta)
        {
            int from = _selected, to = _selected + delta;
            if (to < 0 || to >= _anim.Count) { return; }

            VoxelModel frame = _current;
            _anim.Move(from, to);
            PushUndo(() => _anim.Move(to, from), frame);
        }

        // --- 匯出 ------------------------------------------------------------------------------------------
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            string suggested = _filePath != null
                ? System.IO.Path.ChangeExtension(_filePath, ".png")
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "sprite.png");

            // 預設角度 = 目前預覽的角度；預覽的俯角在「層疊」模式下被夾在 10° 以上，匯出一律用實心體素渲染
            bool outlineOn;
            OutlineOptions outlineForExport = OutlineForExport(out outlineOn);
            ExportDialog dlg = new ExportDialog(_anim, _yaw * 180 / Math.PI, _elev * 180 / Math.PI, ShadeBox.IsChecked == true, suggested, outlineForExport, outlineOn)
            {
                Owner = this,
            };
            dlg.ShowDialog();
        }

        // --- 匯入圖片 --------------------------------------------------------------------------------------
        private void ImportImage_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog { Filter = ImageImport.FileFilter, Multiselect = true, Title = LangResources.Get(TextKey.Common_ImportImage) };
            if (dlg.ShowDialog(this) != true || dlg.FileNames.Length == 0) { return; }

            string[] names = (string[])dlg.FileNames.Clone();
            Array.Sort(names, StringComparer.OrdinalIgnoreCase);   // 多選時依檔名順序放（001, 002, …）
            string[] info = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                try { LoadedImage im = ImageImport.Load(names[i]); info[i] = $"{System.IO.Path.GetFileName(names[i])}　{im.Width}×{im.Height}"; }
                catch (Exception ex) when (ex is System.IO.IOException) { MessageBox.Show(this, ex.Message, LangResources.Get(TextKey.Main_ImportFailed), MessageBoxButton.OK, MessageBoxImage.Error); return; }
            }

            ImportImageDialog dlg2 = new ImportImageDialog(info, LayerView.CanvasWidth, LayerView.CanvasHeight, LangResources.Format(TextKey.ImportImage_StartLabel, LayerView.Slice + 1, SliceUnit()), SliceUnit(), LayerView.CanvasWidth) { Owner = this };
            if (dlg2.ShowDialog() != true) { return; }

            ImportImages(names, dlg2.Strip, dlg2.LayerWidth, dlg2.Fit, dlg2.KeepTransparent);
        }

        /// <summary>
        /// 把圖片匯入成目前視角的畫布層：每個檔案一層（單張圖也可以切成條帶多層），從目前這一層開始往上放（切片編號變大），
        /// 超出模型的部分丟掉。一律先放下浮動內容、取消選取。整個匯入算一次復原（調色盤新增的顏色不回收，跟畫筆畫新顏色一樣）。
        /// 回傳有沒有真的改到東西。
        /// </summary>
        internal bool ImportImages(IList<string> paths, bool strip, int layerWidth, ImageFit fit, bool keepTransparent)
        {
            LayerView.ClearSelection();
            int cw = LayerView.CanvasWidth, ch = LayerView.CanvasHeight, cur = LayerView.Slice;
            ViewDirection view = LayerView.View;

            List<int[]> layers = new List<int[]>();
            try
            {
                if (strip && paths.Count == 1)
                {
                    LoadedImage im = ImageImport.Load(paths[0]);
                    foreach (int[] part in ImageImport.SplitStrip(im.Pixels, im.Width, im.Height, layerWidth, im.Height))
                    {
                        layers.Add(ImageImport.Fit(part, layerWidth, im.Height, cw, ch, fit));
                    }
                }
                else
                {
                    foreach (string p in paths)
                    {
                        LoadedImage im = ImageImport.Load(p);
                        layers.Add(ImageImport.Fit(im.Pixels, im.Width, im.Height, cw, ch, fit));
                    }
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is ArgumentException)
            {
                MessageBox.Show(this, ex.Message, LangResources.Get(TextKey.Main_ImportFailed), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            int room = LayerView.SliceCount - cur;
            int dropped = Math.Max(0, layers.Count - room);
            if (dropped > 0) { layers.RemoveRange(room, dropped); }

            ImageImport.MapStats stats;
            byte[][] indices = ImageImport.MapToPalette(_anim, layers, out stats);
            List<SliceChange> changes = new List<SliceChange>();
            for (int k = 0; k < indices.Length; k++) { changes.Add(SliceOps.Write(EditModel, view, cur + k, indices[k], keepTransparent)); }
            SliceChange all = SliceChange.Combine(changes);

            RebuildPalette();   // 新佔用的色格要出現在色塊面板
            string unit = SliceUnit();
            string tail = (stats.Quantized ? LangResources.Format(TextKey.Msg_ImportColoursQuantized, stats.Slots) : stats.Added > 0 ? LangResources.Format(TextKey.Layers_NewColoursAdded, stats.Added) : "")
                + (dropped > 0 ? LangResources.Format(TextKey.Msg_ImportDropped, dropped, unit) : "");
            if (all.Count == 0)
            {
                ClipText.Text = LangResources.Format(TextKey.Msg_ImportNoChange, tail);
                return false;
            }

            EditModel.NotifyChanged();
            OnStrokeEnded(EditModel, view, cur, all.Indices, all.Old);
            LayerSlider.Value = LayerView.Slice;   // 維持在原本那一層
            ClipText.Text = LangResources.Format(TextKey.Msg_ImageImported, layers.Count, unit, all.Count, cur + 1, unit, tail);
            return true;
        }

        // --- 膨脹：2D 圖片變 3D -----------------------------------------------------------------------------
        private void Inflate_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog { Filter = ImageImport.FileFilter, Multiselect = false, Title = LangResources.Get(TextKey.Main_InflateTo3DChoose) };
            if (dlg.ShowDialog(this) != true) { return; }

            LoadedImage im;
            try { im = ImageImport.Load(dlg.FileName); }
            catch (System.IO.IOException ex) { MessageBox.Show(this, ex.Message, LangResources.Get(TextKey.Main_ImportFailed), MessageBoxButton.OK, MessageBoxImage.Error); return; }

            InflateDialog dlg2 = new InflateDialog(im, $"{System.IO.Path.GetFileName(dlg.FileName)}　{im.Width}×{im.Height}", _anim.Width, _anim.Height, _anim.Depth, LayerView.View) { Owner = this };
            if (dlg2.ShowDialog() != true) { return; }

            InflateImage(im, dlg2.Options);
        }

        /// <summary>
        /// 把圖片膨脹成 3D 寫進目前這一幀（目前的圖層）：顏色對應到調色盤，依視角的切片方向拉出厚度。
        /// 一律先放下浮動內容、取消選取；整個動作算一次復原（調色盤新增的顏色不回收）。回傳有沒有寫出任何體素。
        /// </summary>
        internal bool InflateImage(LoadedImage im, InflateOptions o)
        {
            LayerView.ClearSelection();
            VoxelModel frame = EditModel;
            int w = frame.Width, h = frame.Height, d = frame.Depth;
            int cw = ViewMapper.CanvasWidth(o.View, w, h, d), ch = ViewMapper.CanvasHeight(o.View, w, h, d), count = ViewMapper.SliceCount(o.View, w, h, d);

            int[] canvas = Inflate.PrepareCanvas(im, cw, ch, o.Fit, o.AlphaThreshold);
            ImageImport.MapStats stats;
            byte[] indices = ImageImport.MapToPalette(_anim, new List<int[]> { canvas }, out stats)[0];
            int[] thickness = Inflate.Thickness(indices, cw, ch, count, o);

            byte[] before = (byte[])frame.Data.Clone();
            byte[] beforeParts = frame.Parts == null ? null : (byte[])frame.Parts.Clone();
            int written = Inflate.Fill(frame, o.View, indices, thickness, o.Side, o.Clear);
            RebuildPalette();
            string tail = stats.Quantized ? LangResources.Format(TextKey.Layers_ColoursQuantized, stats.Slots) : stats.Added > 0 ? LangResources.Format(TextKey.Layers_NewColoursAdded, stats.Added) : "";
            if (written == 0)
            {
                ClipText.Text = LangResources.Format(TextKey.Msg_InflateNothing, tail);
                Array.Copy(before, frame.Data, before.Length);
                if (beforeParts != null) { Array.Copy(beforeParts, frame.Parts, beforeParts.Length); }
                return false;
            }

            frame.NotifyChanged();
            PushUndo(() =>
            {
                Array.Copy(before, frame.Data, before.Length);
                if (beforeParts != null && frame.Parts != null) { Array.Copy(beforeParts, frame.Parts, beforeParts.Length); }
                frame.NotifyChanged();
            }, frame, o.View, LayerView.Slice);

            if (LayerView.View != o.View) { SelectViewUi(o.View); }
            LayerSlider.Value = LayerView.SliceCount / 2;
            ClipText.Text = LangResources.Format(TextKey.Msg_Inflated, written, ViewMapper.Name(o.View), tail);
            return true;
        }

        private void ExportGif_Click(object sender, RoutedEventArgs e) { OpenExtraExport(ExtraExportMode.Gif); }

        private void ExportStrip_Click(object sender, RoutedEventArgs e) { OpenExtraExport(ExtraExportMode.Strip); }

        /// <summary>匯出 GIF／疊層條帶：預設檔名跟著目前檔案，預設角度與光影跟著目前預覽。</summary>
        private void OpenExtraExport(ExtraExportMode mode)
        {
            string baseName = _filePath != null
                ? System.IO.Path.ChangeExtension(_filePath, null)
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "sprite");
            string suggested = mode == ExtraExportMode.Gif ? baseName + ".gif" : baseName + "_strip.png";
            bool outlineOn; OutlineOptions outlineForExport = OutlineForExport(out outlineOn);
            new ExportExtraDialog(_anim, mode, _yaw * 180 / Math.PI, _elev * 180 / Math.PI, ShadeBox.IsChecked == true, suggested, outlineForExport, outlineOn) { Owner = this }.ShowDialog();
        }

        // --- 時間軸：播放 ----------------------------------------------------------------------------------
        private void Play_Click(object sender, RoutedEventArgs e) { TogglePlayback(); }

        private void TogglePlayback()
        {
            if (_playing) { StopPlayback(); }
            else { StartPlayback(); }
        }

        private void StartPlayback()
        {
            _playing = true;
            _playIndex = _selected;
            _playAcc = 0;
            PlayButton.Content = LangResources.Get(TextKey.Main_StopSpace);
        }

        private void StopPlayback()
        {
            _playing = false;
            Timeline.PlayingIndex = -1;
            PlayButton.Content = LangResources.Get(TextKey.Btn_PlaySpace);
        }

        private void FpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready || _anim == null) { return; }

            if (_anim.Fps != (int)FpsSlider.Value)
            {
                _anim.Fps = (int)FpsSlider.Value;
                SetDirty(true);
            }
            UpdateFpsText();
        }

        // --- 右側預覽 --------------------------------------------------------------------------------------
        private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) { SetPreviewZoom(1); return; }   // 雙擊：還原預覽距離

            _dragging = true;
            _dragStart = e.GetPosition(PreviewBorder);
            _dragYaw = _yaw;
            _dragElev = _elev;
            PreviewBorder.CaptureMouse();
        }

        private void Preview_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) { return; }

            Point p = e.GetPosition(PreviewBorder);
            _yaw = _dragYaw + (p.X - _dragStart.X) * 0.01;
            _elev = ClampElevation(_dragElev + (p.Y - _dragStart.Y) * 0.01);
        }

        /// <summary>實心體素可以從 -90°（正下方往上看）到 +90°（正上方）；層疊模式最低 10°（再低層疊就壓成一條線）。</summary>
        private double ClampElevation(double elev)
        {
            double min = ModeStackedRadio.IsChecked == true ? 10 * Math.PI / 180 : -Math.PI / 2;
            return Math.Max(min, Math.Min(Math.PI / 2, elev));
        }

        private void Preview_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            PreviewBorder.ReleaseMouseCapture();
        }

        private void OnRendering(object sender, EventArgs e)
        {
            RenderingEventArgs args = e as RenderingEventArgs;
            if (args == null || _anim == null || _current == null) { return; }

            // Rendering 事件偶爾同一幀會來兩次，用 RenderingTime 去重
            if (args.RenderingTime == _lastRenderingTime) { return; }
            double dt = _lastRenderingTime == TimeSpan.MinValue
                ? 0
                : Math.Min(0.1, (args.RenderingTime - _lastRenderingTime).TotalSeconds);
            _lastRenderingTime = args.RenderingTime;

            // 轉速依經過時間算，不管螢幕是 60 或 144 Hz 速度都一樣
            if (AutoRotateBox.IsChecked == true && !_dragging)
            {
                _yaw = (_yaw + dt * AutoRotateSpeed) % (Math.PI * 2);
            }

            // 播放：累積 dt * fps，滿 1 就前進一幀
            if (_playing)
            {
                _playIndex %= _anim.Count;
                _playAcc += dt * _anim.Fps;
                int guard = 0;
                while (_playAcc >= 1 && guard++ < 64)
                {
                    _playAcc -= 1;
                    _playIndex = (_playIndex + 1) % _anim.Count;
                }
                if (_playAcc >= 1) { _playAcc = 0; }
            }
            Timeline.PlayingIndex = _playing ? _playIndex : -1;
            Timeline.RefreshThumbnails();
            LayerStripView.Refresh();
            UpdateLayerHeader();

            VoxelModel shown = _playing ? _anim.Frames[_playIndex] : _current;
            // 「切開看內部」：只畫目前視角、目前切片以後面的部分
            ClipRange clip = UpToLayerBox.IsChecked == true
                ? ViewMapper.Clip(LayerView.View, LayerView.Slice, _anim.Width, _anim.Height, _anim.Depth)
                : ClipRange.Full(_anim.Width, _anim.Height, _anim.Depth);
            bool shade = ShadeBox.IsChecked == true;
            RenderMode mode = ModeStackedRadio.IsChecked == true ? RenderMode.Stacked : RenderMode.Voxel;
            _elev = ClampElevation(_elev);   // 切到層疊模式時，太低的俯角要拉回來

            OutlineOptions outline = CurrentOutline();
            int outlineSig = OutlineSignature(outline);

            if (ReferenceEquals(shown, _drawnModel) && shown.Version == _drawnVersion
                && _yaw == _drawnYaw && _elev == _drawnElev
                && clip.Equals(_drawnClip) && shade == _drawnShade && mode == _drawnMode && outlineSig == _drawnOutline) { UpdatePreviewOverlay(); return; }

            _renderer.Mode = mode;
            _renderer.Outline = outline;
            _renderer.Render(shown, _yaw, _elev, clip, shade);
            _drawnOutline = outlineSig;
            _drawnMode = mode;
            _drawnModel = shown;
            _drawnVersion = shown.Version;
            _drawnYaw = _yaw;
            _drawnElev = _elev;
            _drawnClip = clip;
            _drawnShade = shade;
            UpdatePreviewOverlay();
        }

        /// <summary>
        /// 把預覽轉到從指定方向看（跟編輯視角的定義一致：前＝從 +Y、後＝從 −Y、右＝從 +X、左＝從 −X、頂＝從 +Z 往下、底＝從 −Z 往上）。
        /// 會順便停止自動旋轉，不然一放開又轉走了。層疊模式的俯角下限是 10°，「底」會被拉回去。
        /// </summary>
        private void SetPreviewView(ViewDirection v)
        {
            switch (v)
            {
                case ViewDirection.Top: _yaw = 0; _elev = Math.PI / 2; break;
                case ViewDirection.Bottom: _yaw = 0; _elev = -Math.PI / 2; break;
                case ViewDirection.Front: _yaw = 0; _elev = 0; break;
                case ViewDirection.Back: _yaw = Math.PI; _elev = 0; break;
                case ViewDirection.Right: _yaw = Math.PI / 2; _elev = 0; break;
                default: _yaw = -Math.PI / 2; _elev = 0; break;
            }
            _elev = ClampElevation(_elev);
            AutoRotateBox.IsChecked = false;
        }

        private void PreviewView_Click(object sender, RoutedEventArgs e)
        {
            ViewDirection v;
            if (sender is FrameworkElement fe && Enum.TryParse(fe.Tag as string, out v)) { SetPreviewView(v); }
        }

        /// <summary>預覽上的座標軸小圖與「正在編輯的那一層」框：每個畫面都檢查一次，沒變就不重畫。</summary>
        private void UpdatePreviewOverlay()
        {
            PreviewCubeView.Update(_yaw, _elev, LayerView.View);
            PreviewOverlayView.Update(_renderer, _yaw, _elev, LayerView.View, LayerView.Slice, _anim.Width, _anim.Height, _anim.Depth, _previewZoom, ShowSliceFrameBox.IsChecked == true);
        }
    }
}
