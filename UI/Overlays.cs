using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SD = System.Drawing;

namespace Chalkak
{
    internal enum OverlayMode { Region, Monitor }

    internal class OverlayHost
    {
        public OverlayMode Mode;
        public string Hint = "드래그해서 캡처할 영역을 선택하세요";
        public List<OverlayWindow> Windows = new List<OverlayWindow>();
        public TaskCompletionSource<object> Tcs = new TaskCompletionSource<object>();
        bool done;

        public void Complete(object result)
        {
            if (done) return;
            done = true;
            foreach (var w in Windows.ToList()) { try { w.Close(); } catch { } }
            Tcs.TrySetResult(result);
        }
    }

    internal static class Overlays
    {
        /// <summary>정지된 화면 위에서 드래그로 영역을 선택 (한 모니터 안에서). 취소하면 null.</summary>
        public static async Task<SD.Rectangle?> SelectRegion(SD.Bitmap frozen, SD.Rectangle virt, string hint = null)
        {
            var host = new OverlayHost { Mode = OverlayMode.Region };
            if (!string.IsNullOrEmpty(hint)) host.Hint = hint;
            var cur = ScreenGrab.CursorPos();
            OverlayWindow focus = null;
            foreach (var m in ScreenGrab.Monitors())
            {
                BitmapSource shot;
                using (var crop = ScreenGrab.Crop(frozen, new SD.Rectangle(m.Bounds.X - virt.X, m.Bounds.Y - virt.Y, m.Bounds.Width, m.Bounds.Height)))
                    shot = ImageUtil.ToSource(crop);
                var w = new OverlayWindow(host, m, shot);
                host.Windows.Add(w);
                w.Show();
                if (m.Bounds.Contains(cur)) focus = w;
            }
            if (focus != null) focus.Activate();
            object res = await host.Tcs.Task;
            if (res is SD.Rectangle) return (SD.Rectangle)res;
            return null;
        }

        /// <summary>모니터 번호를 보여 주고 클릭으로 선택. 취소하면 -1.</summary>
        public static async Task<int> PickMonitor(SD.Bitmap frozen, SD.Rectangle virt)
        {
            var host = new OverlayHost { Mode = OverlayMode.Monitor };
            var cur = ScreenGrab.CursorPos();
            OverlayWindow focus = null;
            foreach (var m in ScreenGrab.Monitors())
            {
                BitmapSource shot;
                using (var crop = ScreenGrab.Crop(frozen, new SD.Rectangle(m.Bounds.X - virt.X, m.Bounds.Y - virt.Y, m.Bounds.Width, m.Bounds.Height)))
                    shot = ImageUtil.ToSource(crop);
                var w = new OverlayWindow(host, m, shot);
                host.Windows.Add(w);
                w.Show();
                if (m.Bounds.Contains(cur)) focus = w;
            }
            if (focus != null) focus.Activate();
            object res = await host.Tcs.Task;
            return res is int ? (int)res : -1;
        }
    }

    /// <summary>모니터 한 대를 덮는 전체 화면 오버레이 (물리 픽셀 기준으로 그린다)</summary>
    internal class OverlayWindow : Window
    {
        readonly OverlayHost host;
        readonly MonitorInfo mon;
        readonly Grid root = new Grid();
        readonly Canvas canvas = new Canvas();
        readonly Path dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), IsHitTestVisible = false };
        readonly Rectangle selBorder = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1.5, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        readonly Border sizeTag = new Border { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        readonly TextBlock sizeText = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold };
        readonly Border hint = new Border { IsHitTestVisible = false };
        Border monCard;
        bool dragging, hasSel;
        Point start;
        Rect sel;

        double W => mon.Bounds.Width;
        double H => mon.Bounds.Height;

        public OverlayWindow(OverlayHost host, MonitorInfo mon, BitmapSource shot)
        {
            this.host = host;
            this.mon = mon;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Black;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = mon.Bounds.X; Top = mon.Bounds.Y; Width = 400; Height = 300;
            Cursor = host.Mode == OverlayMode.Region ? Cursors.Cross : Cursors.Hand;
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

            canvas.Width = W; canvas.Height = H;
            var img = new Image { Source = shot, Width = W, Height = H, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            canvas.Children.Add(img);
            canvas.Children.Add(dim);
            canvas.Children.Add(selBorder);

            sizeTag.Background = new SolidColorBrush(Color.FromArgb(220, 28, 28, 30));
            sizeTag.CornerRadius = new CornerRadius(7);
            sizeTag.Padding = new Thickness(9, 3, 9, 3);
            sizeTag.Child = sizeText;
            canvas.Children.Add(sizeTag);

            if (host.Mode == OverlayMode.Region) BuildHint(); else BuildMonitorCard();

            root.Children.Add(canvas);
            // 창의 레이아웃 크기와 상관없이 모니터 전체 크기로 그려지도록, 크기를 고정한 root 를 Canvas 위에 올린다
            root.Width = W; root.Height = H;
            root.HorizontalAlignment = HorizontalAlignment.Left;
            root.VerticalAlignment = VerticalAlignment.Top;
            var holder = new Canvas { ClipToBounds = false };
            holder.Children.Add(root);
            Content = holder;
            UpdateDim();

            SourceInitialized += (s, e) => Place();
            Loaded += (s, e) => { Place(); UpdateScale(); Place(); };
            DpiChanged += (s, e) => { UpdateScale(); Place(); };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) host.Complete(null); };
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonUp += (s, e) => host.Complete(null);
            MouseEnter += (s, e) => { if (host.Mode == OverlayMode.Monitor) HighlightMonitor(true); };
            MouseLeave += (s, e) => { if (host.Mode == OverlayMode.Monitor) HighlightMonitor(false); };
        }

        void Place()
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            var b = mon.Bounds;
            Native.SetWindowPos(h, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_SHOWWINDOW);
        }

        void UpdateScale()
        {
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (s <= 0) s = 1;
            root.LayoutTransform = new ScaleTransform(1 / s, 1 / s);
            // WPF의 창 크기(DIP)가 400x300 으로 남아 일부만 그려지는 문제 방지: 모니터의 물리 픽셀 크기에 맞춘다
            Width = W / s;
            Height = H / s;
        }

        // ----- 영역 선택 -----
        void BuildHint()
        {
            // host 는 생성자에서 이미 설정됨
            hint.Background = new SolidColorBrush(Color.FromArgb(220, 28, 28, 30));
            hint.CornerRadius = new CornerRadius(12);
            hint.Padding = new Thickness(18, 9, 18, 9);
            hint.Child = new TextBlock
            {
                Text = host.Hint + "   ·   Esc 취소",
                Foreground = Brushes.White, FontSize = 15
            };
            hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            canvas.Children.Add(hint);
            Canvas.SetLeft(hint, Math.Max(10, (W - 520) / 2));
            Canvas.SetTop(hint, 36);
            hint.SizeChanged += (s, e) => Canvas.SetLeft(hint, Math.Max(10, (W - hint.ActualWidth) / 2));
        }

        // ----- 모니터 선택 -----
        void BuildMonitorCard()
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = (mon.Index + 1).ToString(), Foreground = Brushes.White, FontSize = 110, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = mon.Label, Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = mon.Resolution + "   ·   클릭해서 캡처", Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), FontSize = 15, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
            monCard = new Border
            {
                Width = 460, Height = 320, CornerRadius = new CornerRadius(28), Padding = new Thickness(20),
                Background = new SolidColorBrush(Color.FromArgb(200, 28, 28, 30)), Child = stack, IsHitTestVisible = false
            };
            stack.VerticalAlignment = VerticalAlignment.Center;
            canvas.Children.Add(monCard);
            Canvas.SetLeft(monCard, (W - 460) / 2);
            Canvas.SetTop(monCard, (H - 320) / 2);
        }

        void HighlightMonitor(bool on)
        {
            monCard.Background = on ? new SolidColorBrush(Color.FromArgb(235, 10, 132, 255)) : new SolidColorBrush(Color.FromArgb(200, 28, 28, 30));
            dim.Fill = new SolidColorBrush(Color.FromArgb((byte)(on ? 40 : 120), 0, 0, 0));
        }

        // ----- 마우스 -----
        Point Pos(MouseEventArgs e)
        {
            var p = e.GetPosition(canvas);
            return new Point(Math.Max(0, Math.Min(W, p.X)), Math.Max(0, Math.Min(H, p.Y)));
        }

        void OnDown(object s, MouseButtonEventArgs e)
        {
            if (host.Mode == OverlayMode.Monitor) { host.Complete(mon.Index); return; }
            dragging = true;
            start = Pos(e);
            hasSel = false;
            hint.Visibility = Visibility.Collapsed;
            CaptureMouse();
        }

        void OnMove(object s, MouseEventArgs e)
        {
            if (!dragging) return;
            var p = Pos(e);
            sel = new Rect(start, p);
            hasSel = sel.Width > 0 && sel.Height > 0;
            UpdateSel();
        }

        void OnUp(object s, MouseButtonEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            ReleaseMouseCapture();
            if (!hasSel || sel.Width < 4 || sel.Height < 4)
            {
                hasSel = false; UpdateSel();
                return;       // 클릭만 한 경우는 무시하고 계속 선택
            }
            var r = new SD.Rectangle(mon.Bounds.X + (int)Math.Round(sel.X), mon.Bounds.Y + (int)Math.Round(sel.Y),
                                     (int)Math.Round(sel.Width), (int)Math.Round(sel.Height));
            host.Complete(r);
        }

        void UpdateSel()
        {
            UpdateDim();
            if (!hasSel)
            {
                selBorder.Visibility = Visibility.Collapsed;
                sizeTag.Visibility = Visibility.Collapsed;
                return;
            }
            selBorder.Visibility = Visibility.Visible;
            selBorder.Width = sel.Width; selBorder.Height = sel.Height;
            Canvas.SetLeft(selBorder, sel.X); Canvas.SetTop(selBorder, sel.Y);
            sizeText.Text = (int)Math.Round(sel.Width) + " × " + (int)Math.Round(sel.Height);
            sizeTag.Visibility = Visibility.Visible;
            double ty = sel.Y - 34 < 4 ? sel.Y + 8 : sel.Y - 34;
            Canvas.SetLeft(sizeTag, Math.Min(sel.X, W - 120));
            Canvas.SetTop(sizeTag, ty);
        }

        void UpdateDim()
        {
            var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
            g.Children.Add(new RectangleGeometry(new Rect(0, 0, W, H)));
            if (hasSel) g.Children.Add(new RectangleGeometry(sel));
            dim.Data = g;
        }
    }

    /// <summary>지연 캡처 카운트다운</summary>
    internal class CountdownWindow : FloatWindow
    {
        readonly TextBlock number = new TextBlock { FontSize = 64, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center };
        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        readonly TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
        int remaining;
        readonly DateTime born = DateTime.UtcNow;

        public static async Task<bool> Run(int seconds, SD.Rectangle monitor)
        {
            var w = new CountdownWindow(seconds);
            w.Show();
            w.PlaceAnchored(monitor, (m, size) => new SD.Point(m.X + (m.Width - size.Width) / 2, m.Y + (m.Height - size.Height) / 3));
            bool ok = await w.tcs.Task;
            try { w.Close(); } catch { }
            return ok;
        }

        CountdownWindow(int seconds)
        {
            remaining = seconds;
            number.Text = remaining.ToString();
            var stack = new StackPanel { Margin = new Thickness(36, 14, 36, 18), MinWidth = 150 };
            stack.Children.Add(number);
            stack.Children.Add(new TextBlock
            {
                Text = "곧 찰칵!  ·  Esc 또는 클릭으로 취소", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("TextSecondary")
            });
            Content = stack;
            MouseLeftButtonDown += (s, e) => tcs.TrySetResult(false);
            tick.Tick += (s, e) =>
            {
                remaining--;
                if (remaining <= 0) { Stop(); tcs.TrySetResult(true); }
                else number.Text = remaining.ToString();
            };
            poll.Tick += (s, e) =>
            {
                if ((DateTime.UtcNow - born).TotalMilliseconds > 400 && (Native.GetAsyncKeyState(Native.VK_ESCAPE) & 0x8000) != 0)
                { Stop(); tcs.TrySetResult(false); }
            };
            Loaded += (s, e) => { tick.Start(); poll.Start(); };
            Closed += (s, e) => Stop();
        }

        void Stop() { tick.Stop(); poll.Stop(); }
    }
}
