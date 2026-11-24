using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PixelStacking.Data;
using PixelStacking.Render;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 底部的時間軸：一排幀縮圖（每幀用 <see cref="StackRenderer"/> 以固定角度畫出偽 3D 小圖）。
    /// 藍框 = 正在編輯的幀；橘色底 = 播放中的幀。點縮圖觸發 <see cref="FrameClicked"/>。
    /// 縮圖只有在該幀內容變動（Version 改變）時才重畫，所以每個螢幕幀呼叫 <see cref="RefreshThumbnails"/> 很便宜。
    /// </summary>
    public sealed class TimelineStrip : ScrollViewer
    {
        private const int ThumbW = 72, ThumbH = 80;
        private const int ThumbTarget = 160;
        private const double ThumbYaw = 0.6;
        private static readonly double ThumbElev = 50 * Math.PI / 180;

        private static readonly Brush NormalBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)));
        private static readonly Brush SelectedBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x37, 0x94, 0xFF)));
        private static readonly Brush NormalBack = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
        private static readonly Brush PlayingBack = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0x40, 0x14)));
        private static readonly Brush NormalText = Freeze(new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)));
        private static readonly Brush PlayingText = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x4D)));

        private sealed class Item
        {
            public VoxelModel Frame;
            public Border Box;
            public TextBlock Label;
            public StackRenderer Renderer;
            public int DrawnVersion = -1;
        }

        private readonly StackPanel _panel = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly List<Item> _items = new List<Item>();
        private Animation _anim;
        private int _selected;
        private int _playing = -1;

        /// <summary>使用者點了某一幀的縮圖（參數 = 幀索引）。</summary>
        public event Action<int> FrameClicked;

        public TimelineStrip()
        {
            Content = _panel;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        public Animation Animation
        {
            get => _anim;
            set
            {
                if (_anim != null) { _anim.StructureChanged -= Rebuild; }

                _anim = value;
                _items.Clear();   // 換動畫：舊的縮圖渲染器作廢
                if (_anim != null) { _anim.StructureChanged += Rebuild; }
                Rebuild();
            }
        }

        public int SelectedIndex
        {
            get => _selected;
            set
            {
                if (value == _selected) { return; }

                _selected = value;
                UpdateHighlights();
                ScrollSelectedIntoView();
            }
        }

        /// <summary>播放中的幀索引（-1 = 沒在播放）。</summary>
        public int PlayingIndex
        {
            get => _playing;
            set
            {
                if (value == _playing) { return; }

                _playing = value;
                UpdateHighlights();
            }
        }

        private void Rebuild()
        {
            _panel.Children.Clear();
            if (_anim == null) { _items.Clear(); return; }

            // 已經有的幀沿用原本的縮圖渲染器，新的才建立
            Dictionary<VoxelModel, Item> old = new Dictionary<VoxelModel, Item>();
            foreach (Item item in _items) { old[item.Frame] = item; }
            _items.Clear();

            foreach (VoxelModel frame in _anim.Frames)
            {
                Item item;
                if (!old.TryGetValue(frame, out item)) { item = CreateItem(frame); }
                _items.Add(item);
                _panel.Children.Add(item.Box);
            }

            if (_selected >= _items.Count) { _selected = _items.Count - 1; }
            UpdateHighlights();
            RefreshThumbnails();
        }

        private Item CreateItem(VoxelModel frame)
        {
            Item item = new Item { Frame = frame, Renderer = new StackRenderer() };
            item.Renderer.Configure(frame.Width, frame.Height, frame.Depth, ThumbTarget);

            System.Windows.Controls.Image image = new System.Windows.Controls.Image
            {
                Width = ThumbW,
                Height = ThumbH,
                Stretch = Stretch.Uniform,
                Source = item.Renderer.Bitmap,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Fant);

            item.Label = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 };

            StackPanel stack = new StackPanel();
            stack.Children.Add(image);
            stack.Children.Add(item.Label);

            item.Box = new Border
            {
                Margin = new Thickness(3),
                Padding = new Thickness(2),
                BorderThickness = new Thickness(2),
                Child = stack,
            };
            item.Box.MouseLeftButtonDown += (s, e) =>
            {
                int index = _items.IndexOf(item);
                if (index >= 0) { FrameClicked?.Invoke(index); }
            };
            return item;
        }

        private void UpdateHighlights()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                bool selected = i == _selected;
                bool playing = i == _playing;

                item.Box.BorderBrush = selected ? SelectedBorder : NormalBorder;
                item.Box.Background = playing ? PlayingBack : NormalBack;
                item.Label.Foreground = playing ? PlayingText : NormalText;
                item.Label.Text = (i + 1).ToString();
            }
        }

        private void ScrollSelectedIntoView()
        {
            if (_selected >= 0 && _selected < _items.Count) { _items[_selected].Box.BringIntoView(); }
        }

        /// <summary>重畫內容有變動的縮圖。每個螢幕幀呼叫一次即可。</summary>
        public void RefreshThumbnails()
        {
            foreach (Item item in _items)
            {
                if (item.DrawnVersion == item.Frame.Version) { continue; }

                item.Renderer.Render(item.Frame, ThumbYaw, ThumbElev,
                    ClipRange.Full(item.Frame.Width, item.Frame.Height, item.Frame.Depth), true);
                item.DrawnVersion = item.Frame.Version;
            }
        }
    }
}
