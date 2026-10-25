using SlabReinforcement.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SlabReinforcement
{
    /// <summary>One retained drawing of every actual background, framing and zone bar.</summary>
    public sealed class SlabPreviewCanvas : FrameworkElement
    {
        private sealed class ColorGeometry
        {
            public StreamGeometry Geometry;
            public Brush Brush;
            public double StrokeWidth = 1.25;
        }

        private readonly List<StreamGeometry> _slabs = new List<StreamGeometry>();
        private readonly List<StreamGeometry> _holes = new List<StreamGeometry>();
        private readonly List<ColorGeometry> _faces = new List<ColorGeometry>();
        private readonly List<ColorGeometry> _bars = new List<ColorGeometry>();
        private Rect _bounds = Rect.Empty;
        private double _scale = 1;
        private Vector _offset;
        private Point _lastMouse;
        private bool _panning;
        private bool _fitOnResize = true;
        private bool _hasPlan;
        private ReinforcementPlan _plan;
        private PreviewScope _scope = PreviewScope.All;
        public ReinforcementPlan DisplayedPlan { get; private set; }
        private static readonly Brush BackgroundBrush = FrozenBrush(Color.FromRgb(249, 250, 252));
        private static readonly Brush SourceBackgroundBrush = FrozenBrush(Color.FromRgb(232, 236, 242));
        private static readonly Brush OutlineBrush = FrozenBrush(Color.FromRgb(95, 108, 126));
        private static readonly Brush LabelBrush = FrozenBrush(Color.FromRgb(99, 113, 130));
        private static readonly Brush AxisXBrush = FrozenBrush(Color.FromRgb(219, 100, 48));
        private static readonly Brush AxisYBrush = FrozenBrush(Color.FromRgb(52, 130, 164));

        public SlabPreviewCanvas()
        {
            ClipToBounds = true;
            Cursor = Cursors.Hand;
            SizeChanged += (sender, args) => { if (_fitOnResize) Fit(); else InvalidateVisual(); };
        }

        public void SetContext(PreviewContext context)
        {
            _slabs.Clear();
            _holes.Clear();
            _faces.Clear();
            _bars.Clear();
            _hasPlan = false;
            _plan = null;
            DisplayedPlan = null;
            _bounds = Rect.Empty;
            foreach (PolygonInput polygon in context.Slabs ?? new List<PolygonInput>())
            {
                if (polygon == null) continue;
                _slabs.Add(PolygonGeometry(polygon));
                foreach (Point2 point in polygon.Shell) IncludePoint(point);
                foreach (List<Point2> hole in polygon.Holes ?? new List<List<Point2>>())
                    _holes.Add(RingsGeometry(new[] { hole }));
            }
            foreach (IGrouping<string, FaceInput> color in (context.Faces ?? new List<FaceInput>()).Where(f => f != null).GroupBy(f => f.ColorKey ?? string.Empty))
            {
                _faces.Add(new ColorGeometry
                {
                    Geometry = RingsGeometry(color.Select(f => f.Vertices), true),
                    // Without a Revit host, the source map is shown at its original RGB intensity.
                    // The neutral backdrop keeps white DXF faces visible without recoloring them.
                    Brush = ColorBrush(color.Key, _slabs.Count == 0 ? (byte)255 : (byte)60, false)
                });
            }
            if (_bounds.IsEmpty)
                foreach (FaceInput face in context.Faces ?? new List<FaceInput>())
                    foreach (Point2 point in face.Vertices) IncludePoint(point);
            Fit();
        }

        public void SetPlan(ReinforcementPlan plan)
        {
            _plan = plan;
            RebuildBars();
        }

        public void SetScope(PreviewScope scope)
        {
            if (_scope == scope) return;
            _scope = scope;
            RebuildBars();
        }

        private void RebuildBars()
        {
            var plan = PreviewProjection.Select(_plan, _scope);
            DisplayedPlan = plan;
            _bars.Clear();
            _hasPlan = false;
            if (plan != null)
            {
                var items = plan.ReferenceBars.Select(b => new { Bar = b, Existing = true })
                    .Concat(plan.Bars.Select(b => new { Bar = b, Existing = false })).ToList();
                var visible = SchematicPreviewBars.Select(items.Select(item => item.Bar));
                _hasPlan = visible.Count > 0;
                foreach (var color in items.Where(item => visible.Contains(item.Bar))
                    .GroupBy(item => new
                    {
                        Color = item.Bar.Placement?.Component == "Background" ? "RGB(52, 130, 164)" : item.Bar.ColorKey ?? string.Empty,
                        item.Existing, Component = item.Bar.Placement?.Component
                    }).OrderBy(group => group.Key.Component == "Background" ? 0 : group.Key.Component == "PerimeterU" ? 1 : 2))
                {
                    var geometry = new StreamGeometry();
                    using (StreamGeometryContext drawing = geometry.Open())
                    {
                        foreach (PlannedBar bar in color.Select(item => item.Bar))
                        {
                            if (bar.Centerline != null && bar.Centerline.Count > 0)
                            {
                                var points = PerimeterPlanner.Sample(bar.Centerline, 0.05).ToList();
                                if (points.Count > 0)
                                {
                                    drawing.BeginFigure(new Point(points[0].X, points[0].Y), false, false);
                                    foreach (var point in points.Skip(1)) drawing.LineTo(new Point(point.X, point.Y), true, false);
                                }
                                continue;
                            }
                            if (!Finite(bar.Start) || !Finite(bar.End)) continue;
                            drawing.BeginFigure(ToPoint(bar.Start), false, false);
                            drawing.LineTo(ToPoint(bar.End), true, false);
                        }
                    }
                    geometry.Freeze();
                    bool background = color.Key.Component == "Background";
                    byte alpha = color.Key.Existing ? (byte)70 : background ? (byte)115 : (byte)255;
                    _bars.Add(new ColorGeometry { Geometry = geometry, Brush = ColorBrush(color.Key.Color, alpha, true),
                        StrokeWidth = background ? 0.75 : 1.25 });
                }
            }
            InvalidateVisual();
        }

        public void Fit()
        {
            _fitOnResize = true;
            if (_bounds.IsEmpty || ActualWidth <= 0 || ActualHeight <= 0) { InvalidateVisual(); return; }
            _scale = Math.Min(Math.Max(ActualWidth - 88, 1) / Math.Max(_bounds.Width, 1),
                Math.Max(ActualHeight - 88, 1) / Math.Max(_bounds.Height, 1));
            _offset = new Vector(ActualWidth / 2 - (_bounds.Left + _bounds.Width / 2) * _scale,
                ActualHeight / 2 + (_bounds.Top + _bounds.Height / 2) * _scale);
            InvalidateVisual();
        }

        public void Zoom(double factor)
        {
            ZoomAt(factor, new Point(ActualWidth / 2, ActualHeight / 2));
        }

        private void ZoomAt(double factor, Point anchor)
        {
            if (factor <= 0 || _bounds.IsEmpty) return;
            double newScale = Math.Max(0.000001, Math.Min(10000, _scale * factor));
            double ratio = newScale / _scale;
            _offset = new Vector(anchor.X - (anchor.X - _offset.X) * ratio,
                anchor.Y - (anchor.Y - _offset.Y) * ratio);
            _scale = newScale;
            _fitOnResize = false;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            drawing.DrawRectangle(_slabs.Count == 0 ? SourceBackgroundBrush : BackgroundBrush, null, new Rect(RenderSize));
            if (_bounds.IsEmpty)
            {
                DrawText(drawing, "Контур плиты не загружен", new Point(24, 24), LabelBrush, 13);
                return;
            }
            drawing.PushClip(new RectangleGeometry(new Rect(RenderSize)));
            drawing.PushTransform(new MatrixTransform(new Matrix(_scale, 0, 0, -_scale, _offset.X, _offset.Y)));
            var outlinePen = new Pen(OutlineBrush, 1.4 / _scale);
            foreach (StreamGeometry slab in _slabs) drawing.DrawGeometry(Brushes.White, null, slab);
            if (_scope != PreviewScope.Background)
                foreach (ColorGeometry face in _faces) drawing.DrawGeometry(face.Brush, null, face.Geometry);
            // Openings remain explicit even where an untrimmed CAD face crosses them.
            foreach (StreamGeometry hole in _holes) drawing.DrawGeometry(BackgroundBrush, null, hole);
            foreach (ColorGeometry bars in _bars) drawing.DrawGeometry(null, new Pen(bars.Brush, bars.StrokeWidth / _scale), bars.Geometry);
            foreach (StreamGeometry slab in _slabs) drawing.DrawGeometry(null, outlinePen, slab);
            drawing.Pop();
            if (_hasPlan) DrawText(drawing, _scope == PreviewScope.Background ? "Фон · вид сверху · все ряды" :
                _scope == PreviewScope.Zones ? "Зоны · вид сверху · все ряды" : "Вид сверху · все ряды", new Point(20, 14), LabelBrush, 11);
            DrawAxes(drawing);
            DrawScale(drawing);
            drawing.Pop();
        }

        private void DrawAxes(DrawingContext drawing)
        {
            Point origin = new Point(30, Math.Max(70, ActualHeight - 28));
            drawing.DrawRoundedRectangle(FrozenBrush(Color.FromArgb(228, 255, 255, 255)), null,
                new Rect(origin.X - 16, origin.Y - 61, 82, 79), 5, 5);
            DrawArrow(drawing, origin, new Point(origin.X + 38, origin.Y), AxisXBrush);
            DrawArrow(drawing, origin, new Point(origin.X, origin.Y - 38), AxisYBrush);
            DrawText(drawing, "X", new Point(origin.X + 42, origin.Y - 8), AxisXBrush, 11);
            DrawText(drawing, "Y", new Point(origin.X - 4, origin.Y - 56), AxisYBrush, 11);
        }

        private static void DrawArrow(DrawingContext drawing, Point start, Point end, Brush brush)
        {
            var pen = new Pen(brush, 1.5);
            drawing.DrawLine(pen, start, end);
            Vector direction = start - end;
            direction.Normalize();
            Vector perpendicular = new Vector(-direction.Y, direction.X);
            drawing.DrawLine(pen, end, end + direction * 6 + perpendicular * 3);
            drawing.DrawLine(pen, end, end + direction * 6 - perpendicular * 3);
        }

        private void DrawScale(DrawingContext drawing)
        {
            double desired = 90 / _scale;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(desired)));
            double normalized = desired / magnitude;
            double length = (normalized >= 5 ? 5 : normalized >= 2 ? 2 : 1) * magnitude;
            double pixels = length * _scale;
            double right = ActualWidth - 24;
            double y = ActualHeight - 25;
            var pen = new Pen(LabelBrush, 1);
            drawing.DrawRoundedRectangle(FrozenBrush(Color.FromArgb(228, 255, 255, 255)), null,
                new Rect(right - pixels - 12, y - 29, pixels + 24, 43), 4, 4);
            drawing.DrawLine(pen, new Point(right - pixels, y), new Point(right, y));
            drawing.DrawLine(pen, new Point(right - pixels, y - 4), new Point(right - pixels, y + 4));
            drawing.DrawLine(pen, new Point(right, y - 4), new Point(right, y + 4));
            DrawText(drawing, length.ToString("0.###", CultureInfo.CurrentCulture) + " мм", new Point(right - pixels, y - 23), LabelBrush, 11);
        }

        private void DrawText(DrawingContext drawing, string text, Point location, Brush brush, double size)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(formatted, location);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            ZoomAt(e.Delta > 0 ? 1.18 : 1 / 1.18, e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Middle) return;
            if (e.ClickCount == 2) { Fit(); e.Handled = true; return; }
            Focus();
            _lastMouse = e.GetPosition(this);
            _panning = CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_panning) return;
            Point point = e.GetPosition(this);
            _offset += point - _lastMouse;
            _lastMouse = point;
            _fitOnResize = false;
            InvalidateVisual();
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_panning) return;
            _panning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Hand;
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            _panning = false;
            Cursor = Cursors.Hand;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Home) { Fit(); e.Handled = true; }
            else if (e.Key == Key.Add || e.Key == Key.OemPlus) { Zoom(1.25); e.Handled = true; }
            else if (e.Key == Key.Subtract || e.Key == Key.OemMinus) { Zoom(0.8); e.Handled = true; }
        }

        private void IncludePoint(Point2 point)
        {
            if (!Finite(point)) return;
            if (_bounds.IsEmpty) _bounds = new Rect(ToPoint(point), new Size(0, 0));
            else _bounds.Union(ToPoint(point));
        }

        private static StreamGeometry PolygonGeometry(PolygonInput polygon)
        {
            return RingsGeometry(new[] { polygon.Shell }.Concat(polygon.Holes ?? new List<List<Point2>>()));
        }

        private static StreamGeometry RingsGeometry(IEnumerable<List<Point2>> rings, bool independentShells = false)
        {
            var geometry = new StreamGeometry { FillRule = independentShells ? FillRule.Nonzero : FillRule.EvenOdd };
            using (StreamGeometryContext drawing = geometry.Open())
                foreach (List<Point2> ring in rings)
                {
                    if (ring == null || ring.Count < 3 || ring.Any(p => !Finite(p))) continue;
                    drawing.BeginFigure(ToPoint(ring[0]), true, true);
                    double signedArea = 0;
                    if (independentShells)
                        for (int index = 0; index < ring.Count; index++)
                        {
                            Point2 next = ring[(index + 1) % ring.Count];
                            signedArea += ring[index].X * next.Y - next.X * ring[index].Y;
                        }
                    for (int index = 1; index < ring.Count; index++)
                        drawing.LineTo(ToPoint(ring[independentShells && signedArea < 0 ? ring.Count - index : index]), true, false);
                }
            geometry.Freeze();
            return geometry;
        }

        private static Point ToPoint(Point2 point) { return new Point(point.X, point.Y); }
        private static bool Finite(Point2 p) { return !double.IsNaN(p.X) && !double.IsInfinity(p.X) && !double.IsNaN(p.Y) && !double.IsInfinity(p.Y); }
        private static Brush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Brush ColorBrush(string key, byte alpha, bool darkenLight)
        {
            byte red = 90, green = 110, blue = 140;
            string text = (key ?? string.Empty).Trim();
            if (text.StartsWith("RGB(", StringComparison.OrdinalIgnoreCase) && text.EndsWith(")", StringComparison.Ordinal))
            {
                string[] parts = text.Substring(4, text.Length - 5).Split(',');
                if (parts.Length == 3 && byte.TryParse(parts[0].Trim(), out byte r) && byte.TryParse(parts[1].Trim(), out byte g) && byte.TryParse(parts[2].Trim(), out byte b))
                { red = r; green = g; blue = b; }
            }
            if (darkenLight && 0.2126 * red + 0.7152 * green + 0.0722 * blue > 180)
            { red = (byte)(red * 0.65); green = (byte)(green * 0.65); blue = (byte)(blue * 0.65); }
            return FrozenBrush(Color.FromArgb(alpha, red, green, blue));
        }
    }
}
