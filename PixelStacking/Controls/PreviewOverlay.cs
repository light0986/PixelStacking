using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PixelStacking.Data;
using PixelStacking.Render;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 蓋在 3D 預覽上的輔助圖形（不吃滑鼠）：
    /// 1. 左下角的座標軸小圖：X 紅、Y 綠、Z 藍，朝向你的那一端用實線、背向你的用淡色，看得出現在從哪個方向看；
    ///    黃色圓圈標出左邊編輯畫布目前的觀看位置。
    /// 2. 一個半透明的黃色框：正在編輯的那一層（目前視角的目前切片）在模型裡的位置。
    /// 位置跟大小跟預覽圖片一致（Uniform 縮放 + 預覽的縮放倍率），所以跟模型是對齊的。
    /// </summary>
    public sealed class PreviewOverlay : FrameworkElement
    {
        private StackRenderer _renderer;
        private double _yaw, _elev, _zoom = 1;
        private ViewDirection _view;
        private int _slice, _w, _h, _d;
        private RenderMode _mode;
        private bool _valid, _showFrame = true;

        private static readonly Color AxisX = Color.FromRgb(255, 96, 96);
        private static readonly Color AxisY = Color.FromRgb(104, 220, 104);
        private static readonly Color AxisZ = Color.FromRgb(96, 150, 255);
        private static readonly Color EditColor = Color.FromRgb(255, 214, 64);

        /// <summary>每個畫面都可以呼叫；參數沒變就不重畫。</summary>
        public void Update(StackRenderer renderer, double yaw, double elevation, ViewDirection view, int slice, int w, int h, int d, double zoom, bool showFrame)
        {
            if (_valid && ReferenceEquals(renderer, _renderer) && yaw == _yaw && elevation == _elev && view == _view && slice == _slice
                && w == _w && h == _h && d == _d && zoom == _zoom && showFrame == _showFrame && renderer.Mode == _mode
                && renderer.OutputWidth == _outW && renderer.OutputHeight == _outH && renderer.Scale == _scale) { return; }

            _renderer = renderer; _yaw = yaw; _elev = elevation; _view = view; _slice = slice; _w = w; _h = h; _d = d; _zoom = zoom; _showFrame = showFrame;
            _mode = renderer.Mode; _outW = renderer.OutputWidth; _outH = renderer.OutputHeight; _scale = renderer.Scale;
            _valid = true;
            InvalidateVisual();
        }

        private int _outW, _outH, _scale;

        protected override void OnRender(DrawingContext dc)
        {
            if (!_valid || _renderer == null || _outW <= 0 || _outH <= 0 || ActualWidth <= 0) { return; }

            if (_showFrame) { DrawSliceFrame(dc); }
            DrawGizmo(dc);
        }

        /// <summary>輸出像素座標 → 這個控制項的座標（跟 Image 的 Uniform 縮放 + 預覽縮放一致）。</summary>
        private Point ToControl(Point outPx)
        {
            double f = Math.Min(ActualWidth / _outW, ActualHeight / _outH) * _zoom;
            return new Point(ActualWidth / 2 + (outPx.X - _outW / 2.0) * f, ActualHeight / 2 + (outPx.Y - _outH / 2.0) * f);
        }

        private Point ModelToControl(double x, double y, double z)
        {
            return ToControl(_renderer.Project(x, y, z, _yaw, _elev, _w, _h, _d));
        }

        // --- 編輯中的那一層 --------------------------------------------------------------------------------
        private void DrawSliceFrame(DrawingContext dc)
        {
            int cw = ViewMapper.CanvasWidth(_view, _w, _h, _d), ch = ViewMapper.CanvasHeight(_view, _w, _h, _d);
            int count = ViewMapper.SliceCount(_view, _w, _h, _d);
            if (cw <= 0 || ch <= 0 || _slice < 0 || _slice >= count) { return; }

            int origin, stepX, stepY, x0, y0, z0;
            ViewMapper.Plane(_view, _slice, _w, _h, _d, out origin, out stepX, out stepY);
            ViewMapper.ToVoxel(origin, _w, _h, out x0, out y0, out z0);
            int[] right, down, toward;
            ViewMapper.Axes(_view, out right, out down, out toward);

            // 畫布左上角那一格的中心；四個角 = 往右 / 往下走 0 或整個畫布的邊緣
            double bx = x0 + 0.5, by = y0 + 0.5, bz = z0 + 0.5;
            Point[] pts = new Point[4];
            double[,] corners = { { -0.5, -0.5 }, { cw - 0.5, -0.5 }, { cw - 0.5, ch - 0.5 }, { -0.5, ch - 0.5 } };
            for (int i = 0; i < 4; i++)
            {
                double a = corners[i, 0], b = corners[i, 1];
                pts[i] = ModelToControl(bx + right[0] * a + down[0] * b, by + right[1] * a + down[1] * b, bz + right[2] * a + down[2] * b);
            }

            StreamGeometry g = new StreamGeometry();
            using (StreamGeometryContext c = g.Open())
            {
                c.BeginFigure(pts[0], true, true);
                c.LineTo(pts[1], true, false);
                c.LineTo(pts[2], true, false);
                c.LineTo(pts[3], true, false);
            }
            g.Freeze();
            Brush fill = new SolidColorBrush(Color.FromArgb(16, EditColor.R, EditColor.G, EditColor.B));
            Pen pen = new Pen(new SolidColorBrush(Color.FromArgb(200, EditColor.R, EditColor.G, EditColor.B)), 1.25) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
            dc.DrawGeometry(fill, pen, g);
        }

        // --- 座標軸小圖 ------------------------------------------------------------------------------------
        private void DrawGizmo(DrawingContext dc)
        {
            const double R = 46, L = 30;
            Point c = new Point(R + 6, ActualHeight - R - 6);
            if (ActualHeight < R * 2 + 12 || ActualWidth < R * 2 + 12) { return; }

            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(120, 20, 20, 24)), new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1), c, R, R);

            double elev = Math.Max(-Math.PI / 2, Math.Min(Math.PI / 2, _elev));
            if (_mode == RenderMode.Stacked) { elev = Math.Max(0.17, elev); }
            double sinY = Math.Sin(_yaw), cosY = Math.Cos(_yaw), sinE = Math.Sin(elev), cosE = Math.Cos(elev);

            // 每條軸（正方向）在螢幕上的方向（右、上）與深度（越大越遠離觀看者）
            double[][] axes =
            {
                new[] { cosY, -sinE * sinY, -cosE * sinY },   // X
                new[] { -sinY, -sinE * cosY, -cosE * cosY },  // Y
                new[] { 0.0, cosE, -sinE },                    // Z
            };
            Color[] cols = { AxisX, AxisY, AxisZ };
            string[] names = { "X", "Y", "Z" };

            // 先畫遠的、再畫近的（近的壓在上面）
            int[] order = { 0, 1, 2 };
            Array.Sort(order, (a, b) => axes[b][2].CompareTo(axes[a][2]));

            foreach (int i in order)
            {
                double[] a = axes[i];
                bool near = a[2] <= 0.0001;   // 正端朝向觀看者
                byte alpha = near ? (byte)255 : (byte)120;
                Brush brush = new SolidColorBrush(Color.FromArgb(alpha, cols[i].R, cols[i].G, cols[i].B));
                Point pos = new Point(c.X + a[0] * L, c.Y - a[1] * L);
                Point neg = new Point(c.X - a[0] * L * 0.4, c.Y + a[1] * L * 0.4);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(90, cols[i].R, cols[i].G, cols[i].B)), 1.5), c, neg);   // 負端：短而淡
                dc.DrawLine(new Pen(brush, near ? 2.5 : 1.8), c, pos);
                dc.DrawEllipse(brush, null, pos, near ? 3.5 : 2.5, near ? 3.5 : 2.5);

                FormattedText ft = new FormattedText(names[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 12, brush,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                double len = Math.Sqrt(a[0] * a[0] + a[1] * a[1]);
                double ux = len > 0.05 ? a[0] / len : 0, uy = len > 0.05 ? -a[1] / len : -1;   // 軸頭外側的方向
                dc.DrawText(ft, new Point(pos.X + ux * 9 - ft.Width / 2, pos.Y + uy * 9 - ft.Height / 2));
            }

            // 左邊編輯畫布的觀看位置：在「朝向觀看者」那個軸的端點畫黃色圓圈
            int[] right, down, toward;
            ViewMapper.Axes(_view, out right, out down, out toward);
            double ex = toward[0] * axes[0][0] + toward[1] * axes[1][0] + toward[2] * axes[2][0];
            double ey = toward[0] * axes[0][1] + toward[1] * axes[1][1] + toward[2] * axes[2][1];
            Point e = new Point(c.X + ex * L, c.Y - ey * L);
            Pen ring = new Pen(new SolidColorBrush(EditColor), 2);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 4), e, 7, 7);
            dc.DrawEllipse(null, ring, e, 7, 7);

            FormattedText label = new FormattedText(Loc.Get(TextKey.Lbl_EditViewMarker), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, new SolidColorBrush(EditColor), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(c.X - label.Width / 2, c.Y - R - label.Height - 1));
        }
    }
}
