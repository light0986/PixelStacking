using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PixelStacking.Data;

namespace PixelStacking.Controls
{
    /// <summary>
    /// 預覽右上角的視角方塊：一個跟著預覽轉動的小方塊，六個面分別寫著 頂／底／前／後／左／右。
    /// 點一個面，預覽就轉到從那一面看（<see cref="ViewPicked"/>）。黃色邊框的面 = 左邊編輯畫布現在看的方向。
    /// 方位跟編輯視角的定義一致：前＝從 +Y 看、後＝從 −Y、右＝從 +X、左＝從 −X、頂＝從 +Z、底＝從 −Z。
    /// </summary>
    public sealed class ViewCubeControl : FrameworkElement
    {
        private sealed class Face
        {
            public ViewDirection View;
            public double[] N, A, B;
            public string LabelKey;
            public Point[] Pts = new Point[4];
            public double Facing;   // 法線與「指向觀看者」的夾角餘弦：> 0 才看得到
            public bool Visible;
        }

        private static readonly Face[] Faces =
        {
            new Face { View = ViewDirection.Top,    N = new[] { 0.0, 0, 1 },  A = new[] { 1.0, 0, 0 }, B = new[] { 0.0, 1, 0 }, LabelKey = TextKey.Cube_Top },
            new Face { View = ViewDirection.Bottom, N = new[] { 0.0, 0, -1 }, A = new[] { 1.0, 0, 0 }, B = new[] { 0.0, 1, 0 }, LabelKey = TextKey.Cube_Bottom },
            new Face { View = ViewDirection.Front,  N = new[] { 0.0, 1, 0 },  A = new[] { 1.0, 0, 0 }, B = new[] { 0.0, 0, 1 }, LabelKey = TextKey.Cube_Front },
            new Face { View = ViewDirection.Back,   N = new[] { 0.0, -1, 0 }, A = new[] { 1.0, 0, 0 }, B = new[] { 0.0, 0, 1 }, LabelKey = TextKey.Cube_Back },
            new Face { View = ViewDirection.Right,  N = new[] { 1.0, 0, 0 },  A = new[] { 0.0, 1, 0 }, B = new[] { 0.0, 0, 1 }, LabelKey = TextKey.Cube_Right },
            new Face { View = ViewDirection.Left,   N = new[] { -1.0, 0, 0 }, A = new[] { 0.0, 1, 0 }, B = new[] { 0.0, 0, 1 }, LabelKey = TextKey.Cube_Left },
        };

        private static readonly Color EditColor = Color.FromRgb(255, 214, 64);

        private double _yaw, _elev;
        private ViewDirection _editView;
        private bool _have;
        private int _hover = -1;

        /// <summary>點了某一面：要把預覽轉到從那個方向看。</summary>
        public event Action<ViewDirection> ViewPicked;

        /// <summary>每個畫面都可以呼叫；參數沒變就不重畫。</summary>
        public void Update(double yaw, double elevation, ViewDirection editView)
        {
            if (_have && yaw == _yaw && elevation == _elev && editView == _editView) { return; }

            _yaw = yaw; _elev = elevation; _editView = editView; _have = true;
            InvalidateVisual();
        }

        private void Layout()
        {
            double elev = Math.Max(-Math.PI / 2, Math.Min(Math.PI / 2, _elev));
            double sinY = Math.Sin(_yaw), cosY = Math.Cos(_yaw), sinE = Math.Sin(elev), cosE = Math.Cos(elev);
            double h = Math.Min(ActualWidth, ActualHeight) * 0.27;
            Point c = new Point(ActualWidth / 2, ActualHeight / 2);
            double[] cam = { cosE * sinY, cosE * cosY, sinE };   // 從模型中心指向觀看者

            foreach (Face f in Faces)
            {
                f.Facing = f.N[0] * cam[0] + f.N[1] * cam[1] + f.N[2] * cam[2];
                f.Visible = f.Facing > 0.02;
                double[][] sgn = { new[] { -1.0, -1 }, new[] { 1.0, -1 }, new[] { 1.0, 1 }, new[] { -1.0, 1 } };
                for (int i = 0; i < 4; i++)
                {
                    double x = f.N[0] + f.A[0] * sgn[i][0] + f.B[0] * sgn[i][1];
                    double y = f.N[1] + f.A[1] * sgn[i][0] + f.B[1] * sgn[i][1];
                    double z = f.N[2] + f.A[2] * sgn[i][0] + f.B[2] * sgn[i][1];
                    double sx = x * cosY - y * sinY;
                    double sy = x * (-sinE * sinY) + y * (-sinE * cosY) + z * cosE;
                    f.Pts[i] = new Point(c.X + sx * h, c.Y - sy * h);
                }
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            if (!_have || ActualWidth <= 0) { return; }

            Layout();
            List<Face> visible = new List<Face>();
            foreach (Face f in Faces) { if (f.Visible) { visible.Add(f); } }
            visible.Sort((a, b) => a.Facing.CompareTo(b.Facing));   // 斜的先畫、正對的後畫

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (Face f in visible)
            {
                bool hover = _hover >= 0 && ReferenceEquals(Faces[_hover], f);
                bool edit = f.View == _editView;
                byte shade = (byte)(70 + 60 * Math.Min(1, f.Facing));
                Color fill = hover ? Color.FromArgb(235, 255, 214, 64) : Color.FromArgb(225, (byte)(shade - 12), (byte)(shade), (byte)(shade + 22));

                StreamGeometry g = new StreamGeometry();
                using (StreamGeometryContext ctx = g.Open())
                {
                    ctx.BeginFigure(f.Pts[0], true, true);
                    ctx.LineTo(f.Pts[1], true, false);
                    ctx.LineTo(f.Pts[2], true, false);
                    ctx.LineTo(f.Pts[3], true, false);
                }
                g.Freeze();
                Pen pen = edit ? new Pen(new SolidColorBrush(EditColor), 2.2) { LineJoin = PenLineJoin.Round }
                                : new Pen(new SolidColorBrush(Color.FromArgb(200, 20, 20, 24)), 1.2) { LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(new SolidColorBrush(fill), pen, g);

                if (f.Facing > 0.3)
                {
                    double cx = (f.Pts[0].X + f.Pts[1].X + f.Pts[2].X + f.Pts[3].X) / 4, cy = (f.Pts[0].Y + f.Pts[1].Y + f.Pts[2].Y + f.Pts[3].Y) / 4;
                    FormattedText ft = new FormattedText(Loc.Get(f.LabelKey), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI, Microsoft JhengHei"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 12,
                        hover ? Brushes.Black : Brushes.White, dpi);
                    dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
                }
            }
        }

        private int HitFace(Point p)
        {
            Layout();
            int best = -1;
            double bestFacing = -1;
            for (int i = 0; i < Faces.Length; i++)
            {
                Face f = Faces[i];
                if (f.Visible && f.Facing > bestFacing && Inside(f.Pts, p)) { best = i; bestFacing = f.Facing; }
            }
            return best;
        }

        private static bool Inside(Point[] q, Point p)
        {
            bool pos = false, neg = false;
            for (int i = 0; i < q.Length; i++)
            {
                Point a = q[i], b = q[(i + 1) % q.Length];
                double cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
                if (cross > 0) { pos = true; } else if (cross < 0) { neg = true; }
                if (pos && neg) { return false; }
            }
            return true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_have) { return; }

            int h = HitFace(e.GetPosition(this));
            if (h != _hover) { _hover = h; InvalidateVisual(); }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; InvalidateVisual(); }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (!_have) { return; }

            int h = HitFace(e.GetPosition(this));
            if (h < 0) { return; }   // 點在方塊外：交給底下的預覽（拖曳轉動）

            ViewPicked?.Invoke(Faces[h].View);
            e.Handled = true;
        }
    }
}
