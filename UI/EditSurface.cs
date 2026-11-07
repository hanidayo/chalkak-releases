using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Chalkak
{
    public enum EditTool { Select, Crop, Marquee, Pen, Line, Arrow, Ellipse, Rect, Text, Mosaic, Blur, Eraser }

    internal enum ObjKind { Pen, Line, Arrow, Ellipse, Rect, Text, Mosaic, Blur }

    internal enum Hnd { None, TL, T, TR, R, BR, B, BL, L, P1, P2 }

    /// <summary>편집 객체. 원본 이미지와 분리되어 있어 언제든 이동·수정할 수 있다.</summary>
    internal class EditObj
    {
        public ObjKind Kind;
        public List<Point> Pts = new List<Point>();
        public Point A, B;
        public Color Color = Colors.Red;
        public double Width = 4;
        public bool Fill;
        public string Text = "";
        public BitmapSource Cache;
        public string CacheKey;

        public double FontSize { get { return 10 + Width * 4; } }
        public Rect Box { get { return new Rect(A, B); } }

        public EditObj Clone()
        {
            var o = (EditObj)MemberwiseClone();
            o.Pts = new List<Point>(Pts);
            return o;
        }

        public void CopyFrom(EditObj s)
        {
            Kind = s.Kind; Pts = new List<Point>(s.Pts); A = s.A; B = s.B; Color = s.Color;
            Width = s.Width; Fill = s.Fill; Text = s.Text; Cache = null; CacheKey = null;
        }

        public void Move(Vector d)
        {
            A += d; B += d;
            for (int i = 0; i < Pts.Count; i++) Pts[i] += d;
        }

        public bool SameAs(EditObj o)
        {
            if (o.Kind != Kind || o.A != A || o.B != B || o.Color != Color || o.Width != Width || o.Fill != Fill || o.Text != Text || o.Pts.Count != Pts.Count) return false;
            return true;
        }
    }

    internal class UndoAct
    {
        public Action Undo, Redo;
    }

    /// <summary>원본 이미지 + 편집 객체를 그리고 마우스 편집을 처리하는 화면. 좌표는 모두 원본 이미지 픽셀 기준.</summary>
    internal class EditSurface : FrameworkElement
    {
        public BitmapSource Base { get; private set; }
        public readonly List<EditObj> Objects = new List<EditObj>();
        public Rect CropRect;
        public Rect? PendingCrop;
        public Rect? Marquee;
        public EditObj Selected;
        public EditObj Hidden;                 // 글자 입력 중인 객체는 잠시 숨김
        public EditTool Tool = EditTool.Select;
        public Color CurColor = Color.FromRgb(0xFF, 0x3B, 0x30);
        public double CurWidth = 4;
        public bool CurFill;
        public double ViewScale = 1;
        public int Version;

        public event Action StateChanged;
        public event Action SelectionChanged;
        public event Action<EditObj, bool> TextEditRequested;

        readonly List<UndoAct> undo = new List<UndoAct>();
        readonly List<UndoAct> redo = new List<UndoAct>();
        public bool CanUndo { get { return undo.Count > 0; } }
        public bool CanRedo { get { return redo.Count > 0; } }

        enum DragMode { None, Create, Move, Resize, Marquee, Crop, Erase }
        DragMode drag = DragMode.None;
        EditObj dragObj, before, creating;
        Point startPt, lastPt;
        Hnd activeHandle;
        Rect origBox;
        List<KeyValuePair<EditObj, int>> erased;
        EditObj propBefore;

        static readonly Typeface TextFace = new Typeface(new FontFamily("Pretendard, Segoe UI Variable Text, Segoe UI, Malgun Gothic"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        public EditSurface(BitmapSource baseImage)
        {
            Base = baseImage;
            CropRect = new Rect(0, 0, W, H);
            ClipToBounds = true;
            Focusable = false;
            Cursor = Cursors.Arrow;
        }

        public double W { get { return Base == null ? 0 : Base.PixelWidth; } }
        public double H { get { return Base == null ? 0 : Base.PixelHeight; } }
        Rect Full { get { return new Rect(0, 0, W, H); } }
        public bool IsCropped { get { return CropRect != Full; } }

        /// <summary>화면에 보이는 영역: 자르기를 적용했으면 남은 부분만 (자르기 도구를 쓰는 동안에는 전체)</summary>
        public Rect ViewRect { get { return (IsCropped && Tool != EditTool.Crop && !PendingCrop.HasValue) ? CropRect : Full; } }
        public Point ViewOrigin { get { return ViewRect.TopLeft; } }

        protected override Size MeasureOverride(Size availableSize) { var v = ViewRect; return new Size(v.Width, v.Height); }

        /// <summary>창을 닫을 때 큰 이미지와 실행 취소 기록을 놓아 준다.</summary>
        public void Release()
        {
            undo.Clear(); redo.Clear(); Objects.Clear();
            Selected = null; Hidden = null; creating = null; dragObj = null; before = null; propBefore = null;
            StateChanged = null; SelectionChanged = null; TextEditRequested = null;
            Base = null;
        }

        // =============================================================== 도구
        public void SetTool(EditTool t)
        {
            Tool = t;
            if (t != EditTool.Crop) PendingCrop = null;
            if (t != EditTool.Select) SetSelected(null);
            Cursor = t == EditTool.Select ? Cursors.Arrow : (t == EditTool.Text ? Cursors.IBeam : Cursors.Cross);
            Notify();
        }

        void SetSelected(EditObj o)
        {
            if (Selected == o) return;
            Selected = o;
            var h = SelectionChanged; if (h != null) h();
        }

        void Notify()
        {
            var h = StateChanged; if (h != null) h();
            InvalidateMeasure();
            InvalidateVisual();
        }

        // =============================================================== 실행 취소
        void Push(UndoAct a)
        {
            undo.Add(a);
            redo.Clear();
            if (undo.Count > 300) undo.RemoveAt(0);
            Version++;
            Notify();
        }

        public void DoUndo()
        {
            if (undo.Count == 0) return;
            var a = undo[undo.Count - 1]; undo.RemoveAt(undo.Count - 1);
            a.Undo(); redo.Add(a); Version++;
            if (Selected != null && !Objects.Contains(Selected)) SetSelected(null);
            Notify();
        }

        public void DoRedo()
        {
            if (redo.Count == 0) return;
            var a = redo[redo.Count - 1]; redo.RemoveAt(redo.Count - 1);
            a.Redo(); undo.Add(a); Version++;
            if (Selected != null && !Objects.Contains(Selected)) SetSelected(null);
            Notify();
        }

        public void AddObject(EditObj o)
        {
            Objects.Add(o);
            Push(new UndoAct
            {
                Undo = () => { Objects.Remove(o); },
                Redo = () => { if (!Objects.Contains(o)) Objects.Add(o); }
            });
        }

        void RemoveObjects(List<EditObj> list)
        {
            var items = list.Where(o => Objects.Contains(o)).Select(o => new KeyValuePair<EditObj, int>(o, Objects.IndexOf(o))).OrderBy(k => k.Value).ToList();
            if (items.Count == 0) return;
            foreach (var k in items) Objects.Remove(k.Key);
            PushRemoved(items);
        }

        void PushRemoved(List<KeyValuePair<EditObj, int>> items)
        {
            Push(new UndoAct
            {
                Undo = () => { foreach (var k in items.OrderBy(x => x.Value)) Objects.Insert(Math.Min(k.Value, Objects.Count), k.Key); },
                Redo = () => { foreach (var k in items) Objects.Remove(k.Key); }
            });
        }

        void PushModify(EditObj o, EditObj b, EditObj a)
        {
            Push(new UndoAct { Undo = () => o.CopyFrom(b), Redo = () => o.CopyFrom(a) });
        }

        public void Modify(EditObj o, Action change)
        {
            var b = o.Clone();
            change();
            o.Cache = null; o.CacheKey = null;
            PushModify(o, b, o.Clone());
        }

        public void DeleteSelected()
        {
            if (Selected == null) return;
            var o = Selected;
            SetSelected(null);
            RemoveObjects(new List<EditObj> { o });
        }

        // ----- 속성 -----
        public void SetColor(Color c)
        {
            CurColor = c;
            if (Selected != null && Selected.Color != c) Modify(Selected, () => Selected.Color = c);
        }

        public void SetFill(bool f)
        {
            CurFill = f;
            if (Selected != null && (Selected.Kind == ObjKind.Rect || Selected.Kind == ObjKind.Ellipse) && Selected.Fill != f)
                Modify(Selected, () => Selected.Fill = f);
        }

        public void BeginPropEdit() { propBefore = Selected != null ? Selected.Clone() : null; }

        public void SetWidthLive(double w)
        {
            CurWidth = w;
            if (Selected != null) { Selected.Width = w; Selected.Cache = null; Selected.CacheKey = null; InvalidateVisual(); }
        }

        public void EndPropEdit()
        {
            if (Selected != null && propBefore != null && propBefore.Width != Selected.Width)
                PushModify(Selected, propBefore, Selected.Clone());
            propBefore = null;
        }

        // ----- 글자 -----
        public void CommitText(EditObj o, bool isNew, string text)
        {
            text = (text ?? "").TrimEnd();
            Hidden = null;
            if (isNew)
            {
                if (text.Length == 0) { InvalidateVisual(); return; }
                o.Text = text;
                AddObject(o);
                SetSelected(o);
            }
            else
            {
                if (text == o.Text) { InvalidateVisual(); return; }
                if (text.Length == 0) { SetSelected(null); RemoveObjects(new List<EditObj> { o }); }
                else Modify(o, () => o.Text = text);
            }
        }

        // ----- 자르기 -----
        public void ApplyPendingCrop()
        {
            if (PendingCrop == null) return;
            var n = Rect.Intersect(PendingCrop.Value, Full);
            PendingCrop = null;
            if (n.IsEmpty || n.Width < 4 || n.Height < 4) { Notify(); return; }
            n = new Rect(Math.Round(n.X), Math.Round(n.Y), Math.Max(1, Math.Round(n.Width)), Math.Max(1, Math.Round(n.Height)));
            var old = CropRect;
            CropRect = n;
            Push(new UndoAct { Undo = () => CropRect = old, Redo = () => CropRect = n });
        }

        public void CancelPendingCrop() { PendingCrop = null; Notify(); }

        public void Deselect() { SetSelected(null); Notify(); }

        public void ClearMarquee() { Marquee = null; Notify(); }

        // =============================================================== 그리기
        void DrawObj(DrawingContext dc, EditObj o)
        {
            var brush = new SolidColorBrush(o.Color); brush.Freeze();
            var pen = new Pen(brush, o.Width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            switch (o.Kind)
            {
                case ObjKind.Pen:
                    if (o.Pts.Count == 1) dc.DrawEllipse(brush, null, o.Pts[0], o.Width / 2, o.Width / 2);
                    else if (o.Pts.Count > 1)
                    {
                        var g = new StreamGeometry();
                        using (var ctx = g.Open())
                        {
                            ctx.BeginFigure(o.Pts[0], false, false);
                            ctx.PolyLineTo(o.Pts.Skip(1).ToList(), true, true);
                        }
                        g.Freeze();
                        dc.DrawGeometry(null, pen, g);
                    }
                    break;
                case ObjKind.Line:
                    dc.DrawLine(pen, o.A, o.B);
                    break;
                case ObjKind.Arrow:
                    DrawArrow(dc, o, brush, pen);
                    break;
                case ObjKind.Ellipse:
                    {
                        var r = o.Box;
                        if (r.Width < 1 || r.Height < 1) break;
                        dc.DrawEllipse(o.Fill ? FillBrush(o.Color) : null, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                        break;
                    }
                case ObjKind.Rect:
                    {
                        var r = o.Box;
                        if (r.Width < 1 || r.Height < 1) break;
                        dc.DrawRoundedRectangle(o.Fill ? FillBrush(o.Color) : null, pen, r, 3, 3);
                        break;
                    }
                case ObjKind.Text:
                    {
                        var ft = MakeText(o);
                        dc.DrawText(ft, o.A);
                        break;
                    }
                case ObjKind.Mosaic:
                case ObjKind.Blur:
                    DrawFx(dc, o);
                    break;
            }
        }

        static Brush FillBrush(Color c)
        {
            var b = new SolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B)); b.Freeze(); return b;
        }

        void DrawArrow(DrawingContext dc, EditObj o, Brush brush, Pen pen)
        {
            Vector d = o.B - o.A;
            double len = d.Length;
            if (len < 1) return;
            Vector u = d / len;
            Vector n = new Vector(-u.Y, u.X);
            double head = Math.Min(len, Math.Max(14, o.Width * 4.5));
            Point baseP = o.B - u * (head * 0.8);
            dc.DrawLine(pen, o.A, baseP);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(o.B, true, true);
                ctx.LineTo(o.B - u * head + n * (head * 0.46), true, false);
                ctx.LineTo(o.B - u * head - n * (head * 0.46), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(brush, new Pen(brush, 1) { LineJoin = PenLineJoin.Round }, g);
        }

        FormattedText MakeText(EditObj o)
        {
            string t = string.IsNullOrEmpty(o.Text) ? " " : o.Text;
            var b = new SolidColorBrush(o.Color); b.Freeze();
            return new FormattedText(t, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextFace, o.FontSize, b, 1.0);
        }

        Rect TextBounds(EditObj o)
        {
            var ft = MakeText(o);
            return new Rect(o.A.X - 2, o.A.Y - 2, Math.Max(20, ft.Width) + 4, Math.Max(10, ft.Height) + 4);
        }

        void DrawFx(DrawingContext dc, EditObj o)
        {
            var r = Rect.Intersect(o.Box, Full);
            if (r.IsEmpty || r.Width < 2 || r.Height < 2) return;
            int x = (int)Math.Floor(r.X), y = (int)Math.Floor(r.Y);
            int w = Math.Min((int)Base.PixelWidth - x, (int)Math.Ceiling(r.Width));
            int h = Math.Min((int)Base.PixelHeight - y, (int)Math.Ceiling(r.Height));
            if (w < 1 || h < 1) return;
            int level = o.Kind == ObjKind.Mosaic ? Math.Max(4, (int)Math.Round(o.Width * 2.5)) : Math.Max(2, (int)Math.Round(o.Width * 1.6));
            string key = o.Kind + "|" + x + "," + y + "," + w + "," + h + "|" + level;
            if (o.Cache == null || o.CacheKey != key)
            {
                var ir = new Int32Rect(x, y, w, h);
                o.Cache = o.Kind == ObjKind.Mosaic ? ImageUtil.Mosaic(Base, ir, level) : ImageUtil.Blur(Base, ir, level);
                o.CacheKey = key;
            }
            dc.DrawImage(o.Cache, new Rect(x, y, w, h));
        }

        void RenderContent(DrawingContext dc, bool export)
        {
            dc.DrawImage(Base, Full);
            foreach (var o in Objects)
            {
                if (o == Hidden) continue;
                DrawObj(dc, o);
            }
            if (!export && creating != null) DrawObj(dc, creating);
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (Base == null) return;
            var vr = ViewRect;
            dc.PushTransform(new TranslateTransform(-vr.X, -vr.Y));
            RenderContent(dc, false);
            double px = 1.0 / Math.Max(0.01, ViewScale);       // 화면 1픽셀에 해당하는 이미지 좌표 길이

            // 자르기 표시
            Rect? show = PendingCrop.HasValue ? PendingCrop : (IsCropped ? (Rect?)CropRect : null);
            if (show.HasValue)
            {
                var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
                g.Children.Add(new RectangleGeometry(Full));
                g.Children.Add(new RectangleGeometry(show.Value));
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(PendingCrop.HasValue ? 150 : 120), 0, 0, 0)), null, g);
                if (PendingCrop.HasValue)
                    dc.DrawRectangle(null, new Pen(Brushes.White, 2 * px), show.Value);
            }

            // 영역 복사용 선택 영역
            if (Marquee.HasValue)
            {
                var accent = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(34, 10, 132, 255)),
                    new Pen(accent, 1.5 * px) { DashStyle = DashStyles.Dash }, Marquee.Value);
            }

            // 선택 핸들
            if (Selected != null && Tool == EditTool.Select && Selected != Hidden)
                DrawHandles(dc, Selected, px);
            dc.Pop();
        }

        void DrawHandles(DrawingContext dc, EditObj o, double px)
        {
            var accent = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
            var pen = new Pen(accent, 1.5 * px);
            Rect b;
            if (o.Kind == ObjKind.Pen)
            {
                double x1 = o.Pts.Min(p => p.X), y1 = o.Pts.Min(p => p.Y), x2 = o.Pts.Max(p => p.X), y2 = o.Pts.Max(p => p.Y);
                b = new Rect(new Point(x1, y1), new Point(x2, y2)); b.Inflate(o.Width, o.Width);
                dc.DrawRectangle(null, new Pen(accent, px) { DashStyle = DashStyles.Dash }, b);
                return;
            }
            if (o.Kind == ObjKind.Text)
            {
                dc.DrawRectangle(null, new Pen(accent, px) { DashStyle = DashStyles.Dash }, TextBounds(o));
                return;
            }
            if (o.Kind == ObjKind.Line || o.Kind == ObjKind.Arrow)
            {
                DrawHandle(dc, o.A, px, pen);
                DrawHandle(dc, o.B, px, pen);
                return;
            }
            b = o.Box;
            dc.DrawRectangle(null, new Pen(accent, px) { DashStyle = DashStyles.Dash }, b);
            foreach (Hnd h in new[] { Hnd.TL, Hnd.T, Hnd.TR, Hnd.R, Hnd.BR, Hnd.B, Hnd.BL, Hnd.L })
                DrawHandle(dc, HandlePoint(b, h), px, pen);
        }

        void DrawHandle(DrawingContext dc, Point p, double px, Pen pen)
        {
            dc.DrawEllipse(Brushes.White, pen, p, 5.5 * px, 5.5 * px);
        }

        static Point HandlePoint(Rect b, Hnd h)
        {
            double cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
            switch (h)
            {
                case Hnd.TL: return new Point(b.Left, b.Top);
                case Hnd.T: return new Point(cx, b.Top);
                case Hnd.TR: return new Point(b.Right, b.Top);
                case Hnd.R: return new Point(b.Right, cy);
                case Hnd.BR: return new Point(b.Right, b.Bottom);
                case Hnd.B: return new Point(cx, b.Bottom);
                case Hnd.BL: return new Point(b.Left, b.Bottom);
                default: return new Point(b.Left, cy);
            }
        }

        // =============================================================== 내보내기
        /// <summary>편집 결과를 원본 해상도로 합성한다. region이 있으면 그 부분만 (자르기 영역과 교차).</summary>
        public BitmapSource Render(Rect? region)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) RenderContent(dc, true);
            var rtb = new RenderTargetBitmap((int)W, (int)H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            Rect r = CropRect;
            if (region.HasValue)
            {
                var inter = Rect.Intersect(r, region.Value);
                if (!inter.IsEmpty && inter.Width >= 1 && inter.Height >= 1) r = inter;
            }
            int x = (int)Math.Max(0, Math.Round(r.X)), y = (int)Math.Max(0, Math.Round(r.Y));
            int w = (int)Math.Min(W - x, Math.Max(1, Math.Round(r.Width)));
            int h = (int)Math.Min(H - y, Math.Max(1, Math.Round(r.Height)));
            BitmapSource result = (x == 0 && y == 0 && w == (int)W && h == (int)H) ? (BitmapSource)rtb : new CroppedBitmap(rtb, new Int32Rect(x, y, w, h));
            result.Freeze();
            return result;
        }

        // =============================================================== 충돌 검사
        static double DistToSeg(Point p, Point a, Point b)
        {
            Vector ab = b - a, ap = p - a;
            double l2 = ab.LengthSquared;
            if (l2 < 1e-9) return ap.Length;
            double t = Math.Max(0, Math.Min(1, (ap.X * ab.X + ap.Y * ab.Y) / l2));
            return (p - (a + ab * t)).Length;
        }

        bool Hit(EditObj o, Point p, double tol)
        {
            switch (o.Kind)
            {
                case ObjKind.Pen:
                    {
                        double t = Math.Max(tol, o.Width / 2 + 2);
                        if (o.Pts.Count == 1) return (p - o.Pts[0]).Length <= t;
                        for (int i = 1; i < o.Pts.Count; i++) if (DistToSeg(p, o.Pts[i - 1], o.Pts[i]) <= t) return true;
                        return false;
                    }
                case ObjKind.Line:
                case ObjKind.Arrow:
                    return DistToSeg(p, o.A, o.B) <= Math.Max(tol, o.Width / 2 + 2);
                case ObjKind.Rect:
                    {
                        var r = o.Box;
                        if (o.Fill && r.Contains(p)) return true;
                        var outer = r; outer.Inflate(tol + o.Width / 2, tol + o.Width / 2);
                        var inner = r; inner.Inflate(-(tol + o.Width / 2), -(tol + o.Width / 2));
                        return outer.Contains(p) && !(!inner.IsEmpty && inner.Contains(p));
                    }
                case ObjKind.Ellipse:
                    {
                        var r = o.Box;
                        double rx = r.Width / 2, ry = r.Height / 2;
                        if (rx < 1 || ry < 1) return r.Contains(p);
                        double dx = (p.X - (r.X + rx)) / rx, dy = (p.Y - (r.Y + ry)) / ry;
                        double d = Math.Sqrt(dx * dx + dy * dy);
                        if (o.Fill && d <= 1) return true;
                        return Math.Abs(d - 1) * Math.Min(rx, ry) <= tol + o.Width / 2;
                    }
                case ObjKind.Text:
                    return TextBounds(o).Contains(p);
                default:
                    return o.Box.Contains(p);
            }
        }

        EditObj HitObject(Point p)
        {
            double tol = 8 / Math.Max(0.01, ViewScale);
            for (int i = Objects.Count - 1; i >= 0; i--)
                if (Hit(Objects[i], p, tol)) return Objects[i];
            return null;
        }

        Hnd HitHandle(EditObj o, Point p)
        {
            double r = 9 / Math.Max(0.01, ViewScale);
            if (o.Kind == ObjKind.Line || o.Kind == ObjKind.Arrow)
            {
                if ((p - o.A).Length <= r) return Hnd.P1;
                if ((p - o.B).Length <= r) return Hnd.P2;
                return Hnd.None;
            }
            if (o.Kind == ObjKind.Rect || o.Kind == ObjKind.Ellipse || o.Kind == ObjKind.Mosaic || o.Kind == ObjKind.Blur)
            {
                var b = o.Box;
                foreach (Hnd h in new[] { Hnd.TL, Hnd.TR, Hnd.BR, Hnd.BL, Hnd.T, Hnd.R, Hnd.B, Hnd.L })
                    if ((p - HandlePoint(b, h)).Length <= r) return h;
            }
            return Hnd.None;
        }

        Point Clamp(Point p)
        {
            var v = ViewRect;
            return new Point(Math.Max(v.Left, Math.Min(v.Right, p.X)), Math.Max(v.Top, Math.Min(v.Bottom, p.Y)));
        }

        // 화면(보이는 영역) 좌표 → 이미지 좌표
        Point ToImage(Point viewPt) { var v = ViewRect; return new Point(viewPt.X + v.X, viewPt.Y + v.Y); }

        // =============================================================== 마우스
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            var raw = ToImage(e.GetPosition(this));
            var p = Clamp(raw);
            startPt = lastPt = (Tool == EditTool.Select) ? raw : p;
            switch (Tool)
            {
                case EditTool.Select:
                    {
                        if (Selected != null)
                        {
                            var h = HitHandle(Selected, raw);
                            if (h != Hnd.None)
                            {
                                drag = DragMode.Resize; dragObj = Selected; before = Selected.Clone();
                                activeHandle = h; origBox = Selected.Box;
                                CaptureMouse(); return;
                            }
                        }
                        var hit = HitObject(raw);
                        SetSelected(hit);
                        if (hit != null)
                        {
                            if (e.ClickCount >= 2 && hit.Kind == ObjKind.Text)
                            {
                                Hidden = hit;
                                var h = TextEditRequested; if (h != null) h(hit, false);
                                InvalidateVisual();
                                return;
                            }
                            drag = DragMode.Move; dragObj = hit; before = hit.Clone();
                            CaptureMouse();
                        }
                        InvalidateVisual();
                        break;
                    }
                case EditTool.Pen:
                case EditTool.Line:
                case EditTool.Arrow:
                case EditTool.Ellipse:
                case EditTool.Rect:
                case EditTool.Mosaic:
                case EditTool.Blur:
                    {
                        SetSelected(null);
                        creating = new EditObj
                        {
                            Kind = ToolKind(Tool), A = p, B = p, Color = CurColor, Width = CurWidth,
                            Fill = CurFill && (Tool == EditTool.Rect || Tool == EditTool.Ellipse)
                        };
                        if (Tool == EditTool.Pen) creating.Pts.Add(p);
                        drag = DragMode.Create;
                        CaptureMouse();
                        InvalidateVisual();
                        break;
                    }
                case EditTool.Text:
                    {
                        var hit = HitObject(raw);
                        if (hit != null && hit.Kind == ObjKind.Text)
                        {
                            SetSelected(hit);
                            Hidden = hit;
                            var h = TextEditRequested; if (h != null) h(hit, false);
                        }
                        else
                        {
                            var o = new EditObj { Kind = ObjKind.Text, A = p, B = p, Color = CurColor, Width = CurWidth };
                            var h = TextEditRequested; if (h != null) h(o, true);
                        }
                        break;
                    }
                case EditTool.Crop:
                    drag = DragMode.Crop; PendingCrop = new Rect(p, p); CaptureMouse(); Notify(); break;
                case EditTool.Marquee:
                    drag = DragMode.Marquee; Marquee = new Rect(p, p); CaptureMouse(); Notify(); break;
                case EditTool.Eraser:
                    drag = DragMode.Erase; erased = new List<KeyValuePair<EditObj, int>>(); CaptureMouse();
                    EraseAt(raw); break;
            }
        }

        static ObjKind ToolKind(EditTool t)
        {
            switch (t)
            {
                case EditTool.Pen: return ObjKind.Pen;
                case EditTool.Line: return ObjKind.Line;
                case EditTool.Arrow: return ObjKind.Arrow;
                case EditTool.Ellipse: return ObjKind.Ellipse;
                case EditTool.Rect: return ObjKind.Rect;
                case EditTool.Mosaic: return ObjKind.Mosaic;
                default: return ObjKind.Blur;
            }
        }

        void EraseAt(Point p)
        {
            var hit = HitObject(p);
            if (hit == null) return;
            erased.Add(new KeyValuePair<EditObj, int>(hit, Objects.IndexOf(hit)));
            Objects.Remove(hit);
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var raw = ToImage(e.GetPosition(this));
            var p = Clamp(raw);
            if (!IsMouseCaptured)
            {
                if (Tool == EditTool.Select)
                {
                    Cursor c = Cursors.Arrow;
                    if (Selected != null)
                    {
                        var h = HitHandle(Selected, raw);
                        if (h != Hnd.None) c = HandleCursor(h);
                        else if (HitObject(raw) != null) c = Cursors.SizeAll;
                    }
                    else if (HitObject(raw) != null) c = Cursors.SizeAll;
                    Cursor = c;
                }
                return;
            }
            switch (drag)
            {
                case DragMode.Create:
                    if (creating.Kind == ObjKind.Pen)
                    {
                        if ((p - lastPt).Length >= 1.0 / Math.Max(0.01, ViewScale)) { creating.Pts.Add(p); lastPt = p; }
                    }
                    else
                    {
                        var q = p;
                        if (Keyboard.Modifiers == ModifierKeys.Shift) q = Constrain(creating, p);
                        creating.B = q;
                    }
                    InvalidateVisual();
                    break;
                case DragMode.Move:
                    {
                        var d = raw - startPt;
                        var tmp = before.Clone();
                        tmp.Move(d);
                        dragObj.CopyFrom(tmp);
                        InvalidateVisual();
                        break;
                    }
                case DragMode.Resize:
                    ResizeTo(raw);
                    InvalidateVisual();
                    break;
                case DragMode.Marquee:
                    Marquee = new Rect(startPt, p); InvalidateVisual(); break;
                case DragMode.Crop:
                    PendingCrop = new Rect(startPt, p); Notify(); break;
                case DragMode.Erase:
                    EraseAt(raw); break;
            }
        }

        static Point Constrain(EditObj o, Point p)
        {
            // Shift: 직선/화살표는 15° 단위, 도형은 정사각/정원
            Vector d = p - o.A;
            if (o.Kind == ObjKind.Line || o.Kind == ObjKind.Arrow)
            {
                double ang = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 12)) * (Math.PI / 12);
                double len = d.Length;
                return new Point(o.A.X + Math.Cos(ang) * len, o.A.Y + Math.Sin(ang) * len);
            }
            double m = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
            return new Point(o.A.X + Math.Sign(d.X == 0 ? 1 : d.X) * m, o.A.Y + Math.Sign(d.Y == 0 ? 1 : d.Y) * m);
        }

        static Cursor HandleCursor(Hnd h)
        {
            switch (h)
            {
                case Hnd.TL: case Hnd.BR: return Cursors.SizeNWSE;
                case Hnd.TR: case Hnd.BL: return Cursors.SizeNESW;
                case Hnd.T: case Hnd.B: return Cursors.SizeNS;
                case Hnd.L: case Hnd.R: return Cursors.SizeWE;
                default: return Cursors.Cross;
            }
        }

        void ResizeTo(Point p)
        {
            var o = dragObj;
            if (activeHandle == Hnd.P1) { o.A = p; return; }
            if (activeHandle == Hnd.P2) { o.B = p; return; }
            double l = origBox.Left, t = origBox.Top, r = origBox.Right, b = origBox.Bottom;
            switch (activeHandle)
            {
                case Hnd.TL: l = p.X; t = p.Y; break;
                case Hnd.T: t = p.Y; break;
                case Hnd.TR: r = p.X; t = p.Y; break;
                case Hnd.R: r = p.X; break;
                case Hnd.BR: r = p.X; b = p.Y; break;
                case Hnd.B: b = p.Y; break;
                case Hnd.BL: l = p.X; b = p.Y; break;
                case Hnd.L: l = p.X; break;
            }
            o.A = new Point(l, t); o.B = new Point(r, b);
            o.Cache = null; o.CacheKey = null;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!IsMouseCaptured) return;
            ReleaseMouseCapture();
            var mode = drag;
            drag = DragMode.None;
            switch (mode)
            {
                case DragMode.Create:
                    {
                        var o = creating; creating = null;
                        bool ok = o.Kind == ObjKind.Pen ||
                                  (o.Kind == ObjKind.Line || o.Kind == ObjKind.Arrow ? (o.B - o.A).Length >= 3 : (o.Box.Width >= 3 && o.Box.Height >= 3));
                        if (ok) { AddObject(o); SetSelected(o); } else InvalidateVisual();
                        break;
                    }
                case DragMode.Move:
                case DragMode.Resize:
                    {
                        var o = dragObj;
                        if (o != null && before != null && !o.SameAs(before))
                        {
                            var after = o.Clone();
                            PushModify(o, before, after);
                        }
                        dragObj = null; before = null;
                        break;
                    }
                case DragMode.Marquee:
                    if (Marquee.HasValue && (Marquee.Value.Width < 3 || Marquee.Value.Height < 3)) Marquee = null;
                    else if (Marquee.HasValue)
                    {
                        var m = Rect.Intersect(Marquee.Value, Full);
                        Marquee = m.IsEmpty ? (Rect?)null : new Rect(Math.Round(m.X), Math.Round(m.Y), Math.Max(1, Math.Round(m.Width)), Math.Max(1, Math.Round(m.Height)));
                    }
                    Notify();
                    break;
                case DragMode.Crop:
                    if (PendingCrop.HasValue && (PendingCrop.Value.Width < 8 || PendingCrop.Value.Height < 8)) PendingCrop = null;
                    Notify();
                    break;
                case DragMode.Erase:
                    if (erased != null && erased.Count > 0) { PushRemoved(erased); SetSelected(null); }
                    erased = null;
                    break;
            }
        }
    }
}
