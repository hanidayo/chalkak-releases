using System;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Chalkak
{
    public partial class EditorWindow : ChromeWindow
    {
        readonly string srcPath;
        readonly EditSurface surface;
        double zoom = 1;
        int savedVersion;
        bool suppress, sliderDown, panning;
        Point panStart;
        double panH, panV;
        TextBox textBox;
        EditObj textObj;
        bool textIsNew;

        static readonly System.Collections.Generic.List<EditorWindow> opened = new System.Collections.Generic.List<EditorWindow>();
        const int MaxOpenWindows = 3;

        bool IsDirty { get { return surface != null && surface.Version != savedVersion; } }

        internal static void Open(string path)
        {
            try
            {
                var w = new EditorWindow(path);
                opened.Add(w);
                w.Show();
                w.Activate();
                TrimOldWindows(w);
            }
            catch (Exception ex) { Toast.Show("편집기를 열지 못했어요", ex.Message); }
        }

        /// <summary>캡처를 계속하면 편집 창이 쌓여 메모리를 먹으므로, 수정하지 않은 오래된 창부터 닫는다 (수정 중인 창은 그대로 둠).</summary>
        static void TrimOldWindows(EditorWindow keep)
        {
            try
            {
                while (opened.Count > MaxOpenWindows)
                {
                    EditorWindow victim = null;
                    foreach (var w in opened) if (w != keep && !w.IsDirty) { victim = w; break; }
                    if (victim == null) break;
                    opened.Remove(victim);
                    victim.Close();
                }
            }
            catch { }
        }

        EditorWindow(string path)
        {
            InitializeComponent();
            srcPath = path;
            Title = Path.GetFileName(path);
            var wa = SystemParameters.WorkArea;
            Width = Math.Min(Width, wa.Width - 40);
            Height = Math.Min(Height, wa.Height - 40);
            MinWidth = Math.Min(MinWidth, Width);
            MinHeight = Math.Min(MinHeight, Height);
            var baseImg = ImageUtil.Load(path);          // 원본 파일은 읽기만 한다
            surface = new EditSurface(baseImg);
            Host.Children.Insert(0, surface);
            surface.StateChanged += OnStateChanged;
            surface.SelectionChanged += OnSelectionChanged;
            surface.TextEditRequested += OnTextEdit;
            surface.CurWidth = WidthSlider.Value;
            PreviewKeyDown += OnKey;
            DpiChanged += (s, e) => ApplyZoom();
            Closed += (s, e) =>
            {
                opened.Remove(this);
                try { Host.Children.Remove(surface); surface.Release(); } catch { }
                MemoryTrim.Schedule();
            };
            Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(FitZoom), DispatcherPriority.Loaded);
            OnStateChanged();
            UpdateInfo();
        }

        // ================================================================ 상태 → UI
        void OnStateChanged()
        {
            UndoBtn.IsEnabled = surface.CanUndo;
            RedoBtn.IsEnabled = surface.CanRedo;
            if (surface.PendingCrop.HasValue)
            {
                var r = surface.PendingCrop.Value;
                CropText.Text = "자를 크기  " + (int)r.Width + " × " + (int)r.Height + " px";
                CropBar.Visibility = Visibility.Visible;
            }
            else CropBar.Visibility = Visibility.Collapsed;
            UpdateInfo();
        }

        void UpdateInfo()
        {
            if (surface == null || surface.Base == null) return;
            var c = surface.CropRect;
            string s = surface.Base.PixelWidth + " × " + surface.Base.PixelHeight + " px";
            if (surface.IsCropped) s += "   →   자르기 " + (int)c.Width + " × " + (int)c.Height;
            if (surface.Marquee.HasValue) s += "   ·   선택 영역 " + (int)surface.Marquee.Value.Width + " × " + (int)surface.Marquee.Value.Height;
            InfoText.Text = s;
        }

        void OnSelectionChanged()
        {
            var o = surface.Selected;
            UpdateWidthLabel(o != null ? o.Kind : (ObjKind?)null);
            if (o == null) return;
            suppress = true;
            try
            {
                bool matched = false;
                foreach (var child in ColorPanel.Children)
                {
                    var rb = child as RadioButton;
                    if (rb == null) continue;
                    var c = (Color)ColorConverter.ConvertFromString((string)rb.Tag);
                    if (c == o.Color) { rb.IsChecked = true; matched = true; }
                }
                if (!matched)
                    foreach (var child in ColorPanel.Children) { var rb = child as RadioButton; if (rb != null) rb.IsChecked = false; }
                WidthSlider.Value = Math.Max(WidthSlider.Minimum, Math.Min(WidthSlider.Maximum, o.Width));
                FillSwitch.IsChecked = o.Fill;
            }
            finally { suppress = false; }
            WidthValue.Text = ((int)Math.Round(WidthSlider.Value)).ToString();
        }

        void UpdateWidthLabel(ObjKind? kind)
        {
            string l = "굵기";
            ObjKind? k = kind;
            if (k == null)
            {
                switch (surface.Tool)
                {
                    case EditTool.Text: k = ObjKind.Text; break;
                    case EditTool.Mosaic: k = ObjKind.Mosaic; break;
                    case EditTool.Blur: k = ObjKind.Blur; break;
                }
            }
            if (k == ObjKind.Text) l = "크기";
            else if (k == ObjKind.Mosaic || k == ObjKind.Blur) l = "강도";
            WidthLabel.Text = l;
        }

        // ================================================================ 도구
        void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (surface == null) return;
            FinishText(true);
            var rb = sender as RadioButton;
            EditTool t;
            if (rb == null || !Enum.TryParse((string)rb.Tag, out t)) return;
            surface.SetTool(t);
            UpdateWidthLabel(null);
        }

        void SelectTool(EditTool t)
        {
            var rb = FindName("Tool_" + t) as RadioButton;
            if (rb != null) rb.IsChecked = true;
        }

        void Color_Checked(object sender, RoutedEventArgs e)
        {
            if (suppress || surface == null) return;
            var rb = sender as RadioButton;
            if (rb == null) return;
            surface.SetColor((Color)ColorConverter.ConvertFromString((string)rb.Tag));
        }

        void CustomColor_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true })
            {
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var c = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
                suppress = true;
                foreach (var child in ColorPanel.Children) { var rb = child as RadioButton; if (rb != null) rb.IsChecked = false; }
                suppress = false;
                surface.SetColor(c);
            }
        }

        void Slider_Down(object sender, MouseButtonEventArgs e) { sliderDown = true; if (surface != null) surface.BeginPropEdit(); }
        void Slider_Up(object sender, MouseButtonEventArgs e) { sliderDown = false; if (surface != null) surface.EndPropEdit(); }

        void Width_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (WidthValue != null) WidthValue.Text = ((int)Math.Round(e.NewValue)).ToString();
            if (suppress || surface == null) return;
            if (sliderDown) surface.SetWidthLive(e.NewValue);
            else { surface.BeginPropEdit(); surface.SetWidthLive(e.NewValue); surface.EndPropEdit(); }
        }

        void Fill_Click(object sender, RoutedEventArgs e) { surface.SetFill(FillSwitch.IsChecked == true); }

        void Undo_Click(object sender, RoutedEventArgs e) { FinishText(true); surface.DoUndo(); }
        void Redo_Click(object sender, RoutedEventArgs e) { FinishText(true); surface.DoRedo(); }
        void CropApply_Click(object sender, RoutedEventArgs e) { surface.ApplyPendingCrop(); SelectTool(EditTool.Select); Dispatcher.BeginInvoke(new Action(FitZoom), DispatcherPriority.Loaded); }
        void CropCancel_Click(object sender, RoutedEventArgs e) { surface.CancelPendingCrop(); }

        // ================================================================ 글자 입력
        void OnTextEdit(EditObj o, bool isNew)
        {
            FinishText(true);
            textObj = o; textIsNew = isNew;
            var brush = new SolidColorBrush(o.Color);
            var tb = new TextBox
            {
                Text = o.Text, FontSize = o.FontSize, FontWeight = FontWeights.SemiBold,
                FontFamily = (FontFamily)FindResource("UiFont"),
                Foreground = brush, CaretBrush = brush,
                Background = new SolidColorBrush(Color.FromArgb(46, 128, 128, 128)),
                BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new Thickness(1),
                AcceptsReturn = true, Padding = new Thickness(2), MinWidth = 40
            };
            var vo = surface.ViewOrigin;
            Canvas.SetLeft(tb, o.A.X - 3 - vo.X);
            Canvas.SetTop(tb, o.A.Y - 3 - vo.Y);
            textBox = tb;
            Overlay.Children.Add(tb);
            tb.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { FinishText(false); e.Handled = true; }
                else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { FinishText(true); e.Handled = true; }
            };
            tb.LostKeyboardFocus += (s, e) => FinishText(true);
            Dispatcher.BeginInvoke(new Action(() => { tb.Focus(); Keyboard.Focus(tb); tb.SelectAll(); }), DispatcherPriority.Input);
            surface.InvalidateVisual();
        }

        void FinishText(bool commit)
        {
            if (textBox == null) return;
            var tb = textBox; var o = textObj; bool isNew = textIsNew;
            textBox = null; textObj = null;
            Overlay.Children.Remove(tb);
            if (commit) surface.CommitText(o, isNew, tb.Text);
            else { surface.Hidden = null; surface.InvalidateVisual(); }
            Focus();
        }

        // ================================================================ 확대/축소
        double Dpi { get { return VisualTreeHelper.GetDpi(this).DpiScaleX; } }

        void ApplyZoom()
        {
            if (surface == null) return;
            double s = zoom / Dpi;
            Host.LayoutTransform = new ScaleTransform(s, s);
            surface.ViewScale = s;
            RenderOptions.SetBitmapScalingMode(surface, zoom >= 1.5 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            ZoomText.Text = Math.Round(zoom * 100) + "%";
            surface.InvalidateVisual();
        }

        void SetZoom(double z, Point? anchorImg = null, Point? anchorView = null)
        {
            z = Math.Max(0.05, Math.Min(8, z));
            zoom = z;
            ApplyZoom();
            if (anchorImg.HasValue && anchorView.HasValue)
            {
                Scroller.UpdateLayout();
                var np = surface.TranslatePoint(anchorImg.Value, Scroller);
                Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + (np.X - anchorView.Value.X));
                Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + (np.Y - anchorView.Value.Y));
            }
        }

        void FitZoom()
        {
            double vw = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth;
            double vh = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight;
            double availW = Math.Max(100, vw - 80) * Dpi, availH = Math.Max(100, vh - 80) * Dpi;
            var vr = surface.ViewRect;
            double z = Math.Min(availW / Math.Max(1, vr.Width), availH / Math.Max(1, vr.Height));
            SetZoom(Math.Min(1.0, z));
        }

        void Fit_Click(object sender, RoutedEventArgs e) { FitZoom(); }
        void Actual_Click(object sender, RoutedEventArgs e) { SetZoom(1.0); }
        void ZoomIn_Click(object sender, RoutedEventArgs e) { SetZoom(zoom * 1.25); }
        void ZoomOut_Click(object sender, RoutedEventArgs e) { SetZoom(zoom / 1.25); }

        void Scroller_Wheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;
            var img = e.GetPosition(surface);
            var view = e.GetPosition(Scroller);
            SetZoom(zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), img, view);
        }

        void Scroller_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            panning = true;
            panStart = e.GetPosition(Scroller);
            panH = Scroller.HorizontalOffset; panV = Scroller.VerticalOffset;
            Scroller.CaptureMouse();
            Scroller.Cursor = Cursors.SizeAll;
            e.Handled = true;
        }

        void Scroller_MouseMove(object sender, MouseEventArgs e)
        {
            if (!panning) return;
            var p = e.GetPosition(Scroller);
            Scroller.ScrollToHorizontalOffset(panH - (p.X - panStart.X));
            Scroller.ScrollToVerticalOffset(panV - (p.Y - panStart.Y));
        }

        void Scroller_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!panning || e.ChangedButton != MouseButton.Middle) return;
            panning = false;
            Scroller.ReleaseMouseCapture();
            Scroller.Cursor = Cursors.Arrow;
        }

        // ================================================================ 키보드
        void OnKey(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            var mod = Keyboard.Modifiers;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (mod == ModifierKeys.Control || mod == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                bool shift = (mod & ModifierKeys.Shift) != 0;
                switch (k)
                {
                    case Key.Z: if (shift) surface.DoRedo(); else surface.DoUndo(); e.Handled = true; return;
                    case Key.Y: surface.DoRedo(); e.Handled = true; return;
                    case Key.C: Copy_Click(null, null); e.Handled = true; return;
                    case Key.S: Save_Click(null, null); e.Handled = true; return;
                    case Key.D0: case Key.NumPad0: FitZoom(); e.Handled = true; return;
                    case Key.D1: case Key.NumPad1: SetZoom(1.0); e.Handled = true; return;
                    case Key.OemPlus: case Key.Add: SetZoom(zoom * 1.25); e.Handled = true; return;
                    case Key.OemMinus: case Key.Subtract: SetZoom(zoom / 1.25); e.Handled = true; return;
                }
                return;
            }
            if (mod != ModifierKeys.None && mod != ModifierKeys.Shift) return;
            switch (k)
            {
                case Key.Delete: case Key.Back: surface.DeleteSelected(); e.Handled = true; return;
                case Key.Enter: if (surface.PendingCrop.HasValue) { CropApply_Click(null, null); e.Handled = true; } return;
                case Key.Escape:
                    if (surface.PendingCrop.HasValue) surface.CancelPendingCrop();
                    else if (surface.Marquee.HasValue) surface.ClearMarquee();
                    else if (surface.Tool != EditTool.Select) SelectTool(EditTool.Select);
                    else if (surface.Selected != null) surface.Deselect();
                    e.Handled = true; return;
                case Key.V: SelectTool(EditTool.Select); break;
                case Key.C: SelectTool(EditTool.Crop); break;
                case Key.S: SelectTool(EditTool.Marquee); break;
                case Key.P: SelectTool(EditTool.Pen); break;
                case Key.L: SelectTool(EditTool.Line); break;
                case Key.A: SelectTool(EditTool.Arrow); break;
                case Key.O: SelectTool(EditTool.Ellipse); break;
                case Key.R: SelectTool(EditTool.Rect); break;
                case Key.T: SelectTool(EditTool.Text); break;
                case Key.M: SelectTool(EditTool.Mosaic); break;
                case Key.B: SelectTool(EditTool.Blur); break;
                case Key.E: SelectTool(EditTool.Eraser); break;
                default: return;
            }
            e.Handled = true;
        }

        // ================================================================ 복사 / 공유 / 저장
        void Copy_Click(object sender, RoutedEventArgs e)
        {
            FinishText(true);
            var region = surface.Marquee;
            var bmp = surface.Render(region);       // 확대 배율과 무관하게 원본 좌표로 잘라 복사
            if (ImageUtil.CopyToClipboard(bmp))
                Toast.Show(region.HasValue ? "선택 영역을 복사했어요" : "이미지를 복사했어요", bmp.PixelWidth + " × " + bmp.PixelHeight + " px");
            else Toast.Show("복사하지 못했어요", "다른 프로그램이 클립보드를 사용 중이에요.");
        }

        void Folder_Click(object sender, RoutedEventArgs e) { ExplorerUtil.Reveal(srcPath); }

        void Share_Click(object sender, RoutedEventArgs e)
        {
            FinishText(true);
            try
            {
                var bmp = surface.Render(surface.Marquee);
                string dir = Path.Combine(AppSettings.TempRoot, "share");
                string name = Path.GetFileNameWithoutExtension(srcPath);
                string tmp = SaveService.SaveSource(bmp, dir, name + "_share");
                if (Sharing.ShareFile(this, tmp, name)) return;
                ImageUtil.CopyToClipboard(bmp);
                Toast.Show("공유 창을 열지 못했어요", "대신 편집한 이미지를 복사했어요.");
            }
            catch (Exception ex) { Toast.Show("공유하지 못했어요", ex.Message); }
        }

        void Save_Click(object sender, RoutedEventArgs e) { FinishText(true); DoSave(); }

        bool DoSave()
        {
            while (true)
            {
                try
                {
                    var bmp = surface.Render(null);
                    string folder = Path.GetDirectoryName(srcPath);
                    string stem = Regex.Replace(Path.GetFileNameWithoutExtension(srcPath), "_edited(_\\d{3})?$", "");
                    string path = SaveService.SaveSource(bmp, folder, stem + "_edited");
                    savedVersion = surface.Version;
                    string p = path;
                    Toast.Show("편집본을 저장했어요", Path.GetFileName(path), "폴더 열기", () => ExplorerUtil.Reveal(p));
                    return true;
                }
                catch (Exception ex)
                {
                    int r = DialogWindow.Ask("편집본을 저장하지 못했어요", ex.Message + "\n\n편집 내용은 그대로 남아 있어요.", new[] { "취소 (Esc)", "다시 시도 (R)" }, 1, -1, " R");
                    if (r != 1) return false;
                }
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            FinishText(true);
            if (surface.Version == savedVersion) return;
            int r = DialogWindow.Ask("편집한 내용을 저장할까요?", "저장하지 않으면 변경 사항이 사라져요. 원본 파일은 그대로 남아 있어요.",
                new[] { "저장 안 함 (N)", "취소 (Esc)", "저장 (S)" }, 2, 0, "N S");
            if (r == 0) return;
            if (r == 2) { if (!DoSave()) e.Cancel = true; return; }
            e.Cancel = true;
        }
    }
}
