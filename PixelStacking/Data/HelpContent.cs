using System.Collections.Generic;
using System.Text;

namespace PixelStacking.Data
{
    /// <summary>說明文件的一個區塊。</summary>
    public enum HelpKind
    {
        /// <summary>小標題。</summary>
        Heading,
        /// <summary>一般段落。</summary>
        Paragraph,
        /// <summary>項目符號。</summary>
        Bullet,
        /// <summary>編號步驟（同一頁裡連續的步驟自動編號，遇到別種區塊就從 1 重來）。</summary>
        Step,
        /// <summary>提醒、建議（用醒目的底色）。</summary>
        Tip,
    }

    public sealed class HelpBlock
    {
        public HelpKind Kind;
        public string Text;
    }

    public sealed class HelpPage
    {
        public string Title;
        public List<HelpBlock> Blocks = new List<HelpBlock>();

        private HelpPage Add(HelpKind kind, string text) { Blocks.Add(new HelpBlock { Kind = kind, Text = text }); return this; }
        public HelpPage H(string t) { return Add(HelpKind.Heading, t); }
        public HelpPage P(string t) { return Add(HelpKind.Paragraph, t); }
        public HelpPage B(string t) { return Add(HelpKind.Bullet, t); }
        public HelpPage S(string t) { return Add(HelpKind.Step, t); }
        public HelpPage Tip(string t) { return Add(HelpKind.Tip, t); }
    }

    /// <summary>功能清單的一項（功能總覽頁由這份清單產生，「目前有多少功能」就是這份清單的長度）。</summary>
    public sealed class HelpFeature
    {
        public string Category, Name, Description;
        public HelpFeature(string category, string name, string description) { Category = category; Name = name; Description = description; }
    }

    /// <summary>
    /// 「說明 → 使用教學」的全部內容：功能清單與各頁文字。內容跟程式本身一起維護——新增功能時，在這裡加一筆 <see cref="Features"/>，
    /// 總覽頁的功能數會自動更新（測試會檢查每個工具、筆刷形狀都有出現在說明裡）。
    /// </summary>
    public static class HelpContent
    {
        public static string OverviewTitle { get { return Loc.Get(TextKey.Help_FeatureOverview); } }

        private static HelpFeature F(string cat, string name, string desc) { return new HelpFeature(cat, name, desc); }

        private static IReadOnlyList<HelpFeature> _features;
        private static int _featuresGeneration = -1;

        /// <summary>功能清單（目前語言）：語言換了就重建，所以不會停在第一次建立時的語言。</summary>
        public static IReadOnlyList<HelpFeature> Features
        {
            get
            {
                if (_features == null || _featuresGeneration != Loc.Generation)
                {
                    _features = BuildFeatures();
                    _featuresGeneration = Loc.Generation;
                }
                return _features;
            }
        }

        private static IReadOnlyList<HelpFeature> BuildFeatures()
        {
            return new List<HelpFeature>
            {
            // --- 檔案、匯入與匯出 ---
            F(Loc.Get(TextKey.Help_FileImportAndExport), Loc.Get(TextKey.Help_NewDocument), Loc.Get(TextKey.Help_CustomLengthWidthAndHeight)),
            F(Loc.Get(TextKey.Help_FileImportAndExport), Loc.Get(TextKey.Help_OpenAndSave), Loc.Get(TextKey.Help_SavesAsPstkAllFrames)),
            F(Loc.Get(TextKey.Help_FileImportAndExport), Loc.Get(TextKey.Help_UnsavedChangesWarning), Loc.Get(TextKey.Help_WhenThereAreChangesA)),
            F(Loc.Get(TextKey.Help_FileImportAndExport), Loc.Get(TextKey.Common_ImportImage), Loc.Get(TextKey.Help_PutAPNGBMPGIF)),
            F(Loc.Get(TextKey.Help_FileImportAndExport), Loc.Get(TextKey.Common_InflateTo3D), Loc.Get(TextKey.Help_ReadA2DImage)),
            F(Loc.Get(TextKey.Help_FileImportAndExport), Loc.Get(TextKey.Help_ExportPNG), Loc.Get(TextKey.Help_PreRenderFixedAnglesInto)),
            F(Loc.Get(TextKey.Help_FileImportAndExport), Loc.Get(TextKey.Help_ExportGIF), Loc.Get(TextKey.Help_AnAnimatedGIFAtA)),
            F(Loc.Get(TextKey.Help_FileImportAndExport), Loc.Get(TextKey.Help_ExportStackStrip), Loc.Get(TextKey.Help_TheOriginalSpriteStackingFormat)),

            // --- 視角與畫布 ---
            F(Loc.Get(TextKey.Help_ViewAndCanvas), Loc.Get(TextKey.Help_SixEditingViews), Loc.Get(TextKey.Help_TopBottomFrontBackLeft)),
            F(Loc.Get(TextKey.Help_ViewAndCanvas), Loc.Get(TextKey.Help_ChangeSlice), Loc.Get(TextKey.Help_TheSliceSliderTheMouse)),
            F(Loc.Get(TextKey.Help_ViewAndCanvas), Loc.Get(TextKey.Help_SliceThumbnailStrip), Loc.Get(TextKey.Help_UnderTheSliceSliderThere)),
            F(Loc.Get(TextKey.Help_ViewAndCanvas), Loc.Get(TextKey.Help_SemiTransparentReference), Loc.Get(TextKey.Help_ShowTheNextSliceSemi)),
            F(Loc.Get(TextKey.Help_ViewAndCanvas), Loc.Get(TextKey.Help_GridAndTransparencyCheckerboard), Loc.Get(TextKey.Help_TheGridCanBeSwitched)),
            F(Loc.Get(TextKey.Help_ViewAndCanvas), Loc.Get(TextKey.Help_CoordinateDisplay), Loc.Get(TextKey.Help_TheVoxelCoordinatesUnderThe)),

            // --- 繪圖工具 ---
            F(Loc.Get(TextKey.Help_DrawingTools), Loc.Get(TextKey.Help_PencilAndEraser), Loc.Get(TextKey.Help_TheLeftButtonPaintsThe)),
            F(Loc.Get(TextKey.Help_DrawingTools), Loc.Get(TextKey.Opt_Fill), Loc.Get(TextKey.Help_AFlatFillReplacesThe)),
            F(Loc.Get(TextKey.Help_DrawingTools), Loc.Get(TextKey.Opt_Eyedropper), Loc.Get(TextKey.Help_ClickTheCanvasToPick)),
            F(Loc.Get(TextKey.Help_DrawingTools), Loc.Get(TextKey.Opt_Line), Loc.Get(TextKey.Help_DragToPreviewAndThe)),
            F(Loc.Get(TextKey.Help_DrawingTools), Loc.Get(TextKey.Opt_Rectangle), Loc.Get(TextKey.Help_DragTheCornersByDefault)),
            F(Loc.Get(TextKey.Help_DrawingTools), Loc.Get(TextKey.Opt_Ellipse), Loc.Get(TextKey.Help_DragTheBoundingRectangleShift)),
            F(Loc.Get(TextKey.Help_DrawingTools), Loc.Get(TextKey.Lbl_MirrorSymmetry), Loc.Get(TextKey.Help_AroundTheExactCentreOf)),
            F(Loc.Get(TextKey.Help_DrawingTools), Loc.Get(TextKey.Help_11BrushShapes), Loc.Get(TextKey.Help_SquareCircleCubeEllipsoidSpring)),
            F(Loc.Get(TextKey.Help_DrawingTools), Loc.Get(TextKey.Help_SizeAndDepth), Loc.Get(TextKey.Help_Size18DepthHow)),

            // --- 選取與編輯 ---
            F(Loc.Get(TextKey.Help_SelectionAndEditing), Loc.Get(TextKey.Help_SelectRectangleSelect), Loc.Get(TextKey.Help_DragOutABoxThickness)),
            F(Loc.Get(TextKey.Help_SelectionAndEditing), Loc.Get(TextKey.Opt_MagicWand), Loc.Get(TextKey.Help_SelectsTheConnectedRegionOf)),
            F(Loc.Get(TextKey.Help_SelectionAndEditing), Loc.Get(TextKey.Help_CutCopyPasteDeleteSelect), Loc.Get(TextKey.Help_CtrlXCVDelete)),
            F(Loc.Get(TextKey.Help_SelectionAndEditing), Loc.Get(TextKey.Menu_RotateSelection), Loc.Get(TextKey.Help_AnyAngleEditMenuOr)),
            F(Loc.Get(TextKey.Help_SelectionAndEditing), Loc.Get(TextKey.Menu_DuplicateSlice), Loc.Get(TextKey.Help_CopyTo1SliceCtrl)),
            F(Loc.Get(TextKey.Help_SelectionAndEditing), Loc.Get(TextKey.Menu_ClearThisSlice), Loc.Get(TextKey.Help_ClearsTheWholeSliceSeen)),
            F(Loc.Get(TextKey.Help_SelectionAndEditing), Loc.Get(TextKey.Help_WholeSliceWholeFrameTransforms), Loc.Get(TextKey.Help_FlipHorizontallyFlipVertically)),
            F(Loc.Get(TextKey.Help_SelectionAndEditing), Loc.Get(TextKey.Menu_Undo), Loc.Get(TextKey.Help_CtrlZUpTo200)),

            // --- 顏色與調色盤 ---
            F(Loc.Get(TextKey.Help_ColourAndPalette), Loc.Get(TextKey.Help_RGBAColourPicking), Loc.Get(TextKey.Help_PickWithTheSquareAnd)),
            F(Loc.Get(TextKey.Help_ColourAndPalette), Loc.Get(TextKey.Help_HSVTab), Loc.Get(TextKey.Help_AnotherWayToPickHue)),
            F(Loc.Get(TextKey.Help_ColourAndPalette), Loc.Get(TextKey.Help_SharedPalette), Loc.Get(TextKey.Help_UpTo255ColoursShared)),
            F(Loc.Get(TextKey.Help_ColourAndPalette), Loc.Get(TextKey.Menu_RemoveUnusedColours), Loc.Get(TextKey.Help_ToolsMenuPaletteRemovesColours)),
            F(Loc.Get(TextKey.Help_ColourAndPalette), Loc.Get(TextKey.Menu_MergeDuplicateColours), Loc.Get(TextKey.Help_ToolsMenuPaletteIdenticalColours)),
            F(Loc.Get(TextKey.Help_ColourAndPalette), Loc.Get(TextKey.Help_ImportExportPalette), Loc.Get(TextKey.Help_GIMPPaletteGpl)),

            // --- 圖層與部件拼接 ---
            F(Loc.Get(TextKey.Help_LayersAndPartAssembly), Loc.Get(TextKey.Help_MultipleLayers), Loc.Get(TextKey.Help_ToTheLeftOfThe)),
            F(Loc.Get(TextKey.Help_LayersAndPartAssembly), Loc.Get(TextKey.Help_LayerCommands), Loc.Get(TextKey.Help_NewCtrlShiftNAbove)),
            F(Loc.Get(TextKey.Help_LayersAndPartAssembly), Loc.Get(TextKey.Help_BackgroundReference), Loc.Get(TextKey.Help_WithSeveralLayersTheEdit)),
            F(Loc.Get(TextKey.Help_LayersAndPartAssembly), Loc.Get(TextKey.Common_ImportFileAsLayer), Loc.Get(TextKey.Help_LayerMenuReadsAnotherPstk)),
            F(Loc.Get(TextKey.Help_LayersAndPartAssembly), Loc.Get(TextKey.Common_MoveLayerContent), Loc.Get(TextKey.Help_LayerMenuShiftsAllVoxels)),
            F(Loc.Get(TextKey.Help_LayersAndPartAssembly), Loc.Get(TextKey.Opt_3DMagicWand), Loc.Get(TextKey.Help_Tool3DMagicWand)),
            F(Loc.Get(TextKey.Help_LayersAndPartAssembly), Loc.Get(TextKey.Help_MoveAndExtractParts), Loc.Get(TextKey.Help_AfterSelectingAPartMove)),

            // --- 動畫 ---
            F(Loc.Get(TextKey.Help_AnimationFrameByFrame), Loc.Get(TextKey.Help_MultiFrameTimeline), Loc.Get(TextKey.Help_AtTheBottomIsA)),
            F(Loc.Get(TextKey.Help_AnimationFrameByFrame), Loc.Get(TextKey.Help_FrameManagement), Loc.Get(TextKey.Help_AddBlankFrameDuplicateFrame)),
            F(Loc.Get(TextKey.Help_AnimationFrameByFrame), Loc.Get(TextKey.Help_PlaybackAndSpeed), Loc.Get(TextKey.Help_SpacePlaysPausesSpeed1)),
            F(Loc.Get(TextKey.Help_AnimationFrameByFrame), Loc.Get(TextKey.Help_ChangeFrame), Loc.Get(TextKey.Help_PressAndForThePrevious)),

            // --- 3D 預覽 ---
            F(Loc.Get(TextKey.Help_3DPreview), Loc.Get(TextKey.Help_LivePseudo3DPreview), Loc.Get(TextKey.Help_ShownLiveOnTheRight)),
            F(Loc.Get(TextKey.Help_3DPreview), Loc.Get(TextKey.Help_TwoPreviewModes), Loc.Get(TextKey.Help_SolidVoxelsShowsWhatYou)),
            F(Loc.Get(TextKey.Help_3DPreview), Loc.Get(TextKey.Help_AutoRotateAndShading), Loc.Get(TextKey.Help_AutoRotateCanBeSwitched)),
            F(Loc.Get(TextKey.Help_3DPreview), Loc.Get(TextKey.Help_CutOpenToSeeInside), Loc.Get(TextKey.Help_ShowsOnlyThePartBehind)),
            F(Loc.Get(TextKey.Help_3DPreview), Loc.Get(TextKey.Lbl_Outline), Loc.Get(TextKey.Help_OutlineDescription)),


            // --- 介面 ---
            F(Loc.Get(TextKey.Help_Interface), Loc.Get(TextKey.Help_CollapsibleFunctionBars), Loc.Get(TextKey.Help_TheLeftColumnIsSplit)),
            F(Loc.Get(TextKey.Help_Interface), Loc.Get(TextKey.Help_CollapsibleTimelineAndSlicePanels), Loc.Get(TextKey.Help_TheTimelineAndTheSlice)),
            F(Loc.Get(TextKey.Help_Interface), Loc.Get(TextKey.Help_WheelZones), Loc.Get(TextKey.Help_MouseOverTheLeftColumn)),
            F(Loc.Get(TextKey.Help_Interface), Loc.Get(TextKey.Help_MouseTips), Loc.Get(TextKey.Help_AlmostEveryButtonAndOption)),
            F(Loc.Get(TextKey.Help_Interface), Loc.Get(TextKey.Help_UserGuide), Loc.Get(TextKey.Help_TheUserGuideInThe)),
            F(Loc.Get(TextKey.Help_Interface), Loc.Get(TextKey.Help_FourLanguages), Loc.Get(TextKey.Help_TheToolsLanguageMenuSwitches)),
            F(Loc.Get(TextKey.Help_Interface), Loc.Get(TextKey.Help_ForAI), Loc.Get(TextKey.Help_HelpMenuCopyPromptFor)),
            };
        }

        /// <summary>功能清單的分類（依第一次出現的順序）。</summary>
        public static List<string> Categories()
        {
            List<string> cats = new List<string>();
            foreach (HelpFeature f in Features) { if (!cats.Contains(f.Category)) { cats.Add(f.Category); } }
            return cats;
        }

        /// <summary>所有頁（第一頁是由 <see cref="Features"/> 產生的功能總覽）。</summary>
        public static List<HelpPage> Pages()
        {
            List<HelpPage> pages = new List<HelpPage>();
            pages.Add(Overview());
            pages.Add(QuickStart());
            pages.Add(DrawingTips());
            pages.Add(AnimationPage());
            pages.Add(LayersPage());
            pages.Add(Recommendations());
            pages.Add(Shortcuts());
            pages.Add(Faq());
            return pages;
        }

        /// <summary>全部說明文字接成一個字串（測試用來檢查某個名稱有沒有出現在說明裡）。</summary>
        public static string AllText()
        {
            StringBuilder sb = new StringBuilder();
            foreach (HelpFeature f in Features) { sb.AppendLine(f.Category + " " + f.Name + " " + f.Description); }
            foreach (HelpPage p in Pages()) { sb.AppendLine(p.Title); foreach (HelpBlock b in p.Blocks) { sb.AppendLine(b.Text); } }
            return sb.ToString();
        }

        private static HelpPage Overview()
        {
            List<string> cats = Categories();
            HelpPage p = new HelpPage { Title = OverviewTitle };
            p.P(Loc.Get(TextKey.Help_PixelStackingIsAnEditorFor));
            p.Tip(Loc.Format(TextKey.Help_OverviewIntro, Features.Count, cats.Count));
            foreach (string cat in cats)
            {
                int n = 0;
                foreach (HelpFeature f in Features) { if (f.Category == cat) { n++; } }
                p.H(Loc.Format(TextKey.Help_CategoryTitle, cat, n));
                foreach (HelpFeature f in Features) { if (f.Category == cat) { p.B(Loc.Format(TextKey.Help_FeatureLine, f.Name, f.Description)); } }
            }
            return p;
        }

        private static HelpPage QuickStart()
        {
            HelpPage p = new HelpPage { Title = Loc.Get(TextKey.Help_QuickStart) };
            p.P(Loc.Get(TextKey.Help_MakeYourFirstModelIn));
            p.S(Loc.Get(TextKey.Help_PressCtrlNForA));
            p.S(Loc.Get(TextKey.Help_PickAColourInThe));
            p.S(Loc.Get(TextKey.Help_StartDrawingTheDefaultView));
            p.S(Loc.Get(TextKey.Help_ChangeSliceScrollTheWheel));
            p.S(Loc.Get(TextKey.Help_PaintIn3DWith));
            p.S(Loc.Get(TextKey.Help_SeeTheResultThe3));
            p.S(Loc.Get(TextKey.Help_AddDetailFromAnotherAngle));
            p.S(Loc.Get(TextKey.Help_SaveCtrlSSavesAs));
            p.S(Loc.Get(TextKey.Help_ForGamesFileExportPNG));
            p.Tip(Loc.Get(TextKey.Help_WhenYouCanTFind));
            return p;
        }

        private static HelpPage DrawingTips()
        {
            HelpPage p = new HelpPage { Title = Loc.Get(TextKey.Help_DrawingAndSelectionTips) };
            p.H(Loc.Get(TextKey.Help_DrawFaster));
            p.B(Loc.Get(TextKey.Help_LeftRightSymmetricCharactersIn));
            p.B(Loc.Get(TextKey.Help_LargeAreasUseFillTick));
            p.B(Loc.Get(TextKey.Help_CirclesBoxesLinesUseLine));
            p.B(Loc.Get(TextKey.Help_AWholeWallOrA));
            p.B(Loc.Get(TextKey.Help_3DBrushesEllipsoidFor));
            p.H(Loc.Get(TextKey.Help_SelectingAndModifying));
            p.B(Loc.Get(TextKey.Help_SelectDragsOutARectangle));
            p.B(Loc.Get(TextKey.Help_TheMagicWandSTolerance));
            p.B(Loc.Get(TextKey.Help_AfterSelectingYouCanCtrl));
            p.B(Loc.Get(TextKey.Help_RotateSelectionEditMenuOr));
            p.B(Loc.Get(TextKey.Help_TransformAllSlicesOfThe));
            p.Tip(Loc.Get(TextKey.Help_WhenPastingTickDonT));
            p.H(Loc.Get(TextKey.Help_DisplayAids));
            p.B(Loc.Get(TextKey.Help_ShowNextSliceSemiTransparent));
            p.B(Loc.Get(TextKey.Help_TickCutOpenToSee));
            return p;
        }

        private static HelpPage AnimationPage()
        {
            HelpPage p = new HelpPage { Title = Loc.Get(TextKey.Help_AnimationFrameByFrame) };
            p.P(Loc.Get(TextKey.Help_ADocumentCanHaveMany));
            p.S(Loc.Get(TextKey.Help_FinishTheFirstFramePress));
            p.S(Loc.Get(TextKey.Help_FineTuneTheNewFrame));
            p.S(Loc.Get(TextKey.Help_Repeat1And2On));
            p.S(Loc.Get(TextKey.Help_PressSpaceOrPlayTo));
            p.S(Loc.Get(TextKey.Help_FileExportGIFToShare));
            return p;
        }

        private static HelpPage LayersPage()
        {
            HelpPage p = new HelpPage { Title = Loc.Get(TextKey.Help_LayersAndPartAssembly) };
            p.P(Loc.Get(TextKey.Help_MultipleLayersLetYouSplit));
            p.B(Loc.Get(TextKey.Help_UpperLayersCoverLowerOnes));
            p.B(Loc.Get(TextKey.Help_TheCanvasEditsTheCurrent));
            p.B(Loc.Get(TextKey.Help_NewAboveTheCurrentLayer));
            p.H(Loc.Get(TextKey.Help_ImportAComponentAndAssemble));
            p.S(Loc.Get(TextKey.Help_LayerMenuImportFileAs));
            p.S(Loc.Get(TextKey.Help_LayerMenuMoveLayerContent));
            p.S(Loc.Get(TextKey.Help_WhenItLooksRightPress));
            p.H(Loc.Get(TextKey.Help_UseThe3DMagic));
            p.S(Loc.Get(TextKey.Help_InTheLeftColumnChoose));
            p.S(Loc.Get(TextKey.Help_MoveItWithTheArrow));
            p.S(Loc.Get(TextKey.Help_PressMoveToNewLayer));
            p.Tip(Loc.Get(TextKey.Help_ImportedComponentsAndExtracted));
            return p;
        }

        private static HelpPage Recommendations()
        {
            HelpPage p = new HelpPage { Title = Loc.Get(TextKey.Help_RecommendedWorkflow) };
            p.H(Loc.Get(TextKey.Help_Size));
            p.B(Loc.Get(TextKey.Help_StartSmall1632To));
            p.B(Loc.Get(TextKey.Help_TheSizeIsDecidedWhen));
            p.H(Loc.Get(TextKey.Help_DrawingOrder));
            p.B(Loc.Get(TextKey.Help_DecideThePaletteBeforeDrawing));
            p.H(Loc.Get(TextKey.Help_SavingAndSafety));
            p.B(Loc.Get(TextKey.Help_PressCtrlSOftenBefore));

            p.H(Loc.Get(TextKey.Help_OutputForGames));
            p.B(Loc.Get(TextKey.Help_IfTheGameOnlyNeeds));
            p.B(Loc.Get(TextKey.Help_IfTheGameWantsTo));
            p.B(Loc.Get(TextKey.Help_JustSharingAnAnimationExport));
            return p;
        }

        private static HelpPage Shortcuts()
        {
            HelpPage p = new HelpPage { Title = Loc.Get(TextKey.Help_ShortcutsAndMouse) };
            p.H(Loc.Get(TextKey.Help_FileAndEdit));
            p.B(Loc.Get(TextKey.Help_CtrlNNewCtrlO));
            p.B(Loc.Get(TextKey.Help_CtrlZUndoCtrlX));
            p.B(Loc.Get(TextKey.Help_CtrlDCopyToSlice));
            p.B(Loc.Get(TextKey.Help_CtrlRotatesTheSelection15));
            p.B(Loc.Get(TextKey.Help_EnterDropsFloatingContentEsc));
            p.H(Loc.Get(TextKey.Help_ToolsAndViews));
            p.B(Loc.Get(TextKey.Help_IEyedropperLLineR));
            p.B(Loc.Get(TextKey.Help_1Top2Bottom3));
            p.B(Loc.Get(TextKey.Help_AndPageDownAndPageUpChange));
            p.B(Loc.Get(TextKey.Help_AndPreviousAndNextFrame));
            p.H(Loc.Get(TextKey.Help_Mouse));
            p.B(Loc.Get(TextKey.Help_CanvasLeftButtonPaintsOr));
            p.B(Loc.Get(TextKey.Help_TheWheelDependsOnWhere));
            p.B(Loc.Get(TextKey.Help_CanvasZoom));
            p.B(Loc.Get(TextKey.Help_CanvasScroll));
            p.B(Loc.Get(TextKey.Help_3DPreviewDragTo));
            p.B(Loc.Get(TextKey.Help_LineRectangleEllipseHoldShift));
            return p;
        }

        private static HelpPage Faq()
        {
            HelpPage p = new HelpPage { Title = Loc.Get(TextKey.Help_FAQ) };
            p.H(Loc.Get(TextKey.Help_Drawing));
            p.B(Loc.Get(TextKey.Help_CanTSeeWhatYou));
            p.B(Loc.Get(TextKey.Help_OneStrokePaintsSeveralSlices));
            p.B(Loc.Get(TextKey.Help_TheWheelIsnTDoing));
            p.B(Loc.Get(TextKey.Help_EdgesHardToSee));
            p.H(Loc.Get(TextKey.Help_Files));
            p.B(Loc.Get(TextKey.Help_ImportedColoursReducedOrTurned));
            p.B(Loc.Get(TextKey.Help_OpeningOldFilesEveryOld));
            p.B(Loc.Get(TextKey.Help_KeepsAskingWhetherToSave));
            p.H(Loc.Get(TextKey.Help_LanguageAndAI));
            p.B(Loc.Get(TextKey.Help_ChangeTheInterfaceLanguageTools));
            p.B(Loc.Get(TextKey.Help_WantClaudeOrAnotherAI));
            p.B(Loc.Get(TextKey.Help_TheCommandLinePstkExe));
            return p;
        }
    }
}
