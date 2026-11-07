using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace Chalkak
{
    /// <summary>투명 배경 + 둥근 모서리 + 그림자의 작은 떠 있는 창. 화면 캡처에는 찍히지 않는다.</summary>
    internal class FloatWindow : Window
    {
        readonly bool hideFromCapture;

        public FloatWindow(bool hideFromCapture = true, bool startHidden = true)
        {
            this.hideFromCapture = hideFromCapture;
            Style = (Style)Application.Current.FindResource("AppleFloat");
            Topmost = true;
            ShowActivated = false;
            if (startHidden) Opacity = 0;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            if (hideFromCapture)
            {
                try { Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, Native.WDA_EXCLUDEFROMCAPTURE); }
                catch { }
            }
        }

        public IntPtr Hwnd => new WindowInteropHelper(this).Handle;
        public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

        public void SetPosPx(int x, int y)
        {
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, x, y, 0, 0, 0x0001 | Native.SWP_NOACTIVATE);   // SWP_NOSIZE
        }

        /// <summary>대상 모니터로 옮긴 뒤(해당 모니터 DPI 적용) 크기를 재서 원하는 위치에 배치하고 서서히 나타낸다.</summary>
        public void PlaceAnchored(MonitorInfo monitor, Func<SD.Rectangle, SD.Size, SD.Point> position)
        {
            PlaceAnchored(monitor.Bounds, position);
        }

        public void PlaceAnchored(SD.Rectangle monitor, Func<SD.Rectangle, SD.Size, SD.Point> position)
        {
            SetPosPx(monitor.X + 20, monitor.Y + 20);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateLayout();
                double s = DpiScale;
                var size = new SD.Size((int)Math.Ceiling(ActualWidth * s), (int)Math.Ceiling(ActualHeight * s));
                var p = position(monitor, size);
                SetPosPx(p.X, p.Y);
                FadeIn();
            }), DispatcherPriority.Loaded);
        }

        public void FadeIn(int ms = 140)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)));
        }

        public void FadeOutAndClose(int ms = 180)
        {
            var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(ms));
            anim.Completed += (s, e) => { try { Close(); } catch { } };
            BeginAnimation(OpacityProperty, anim);
        }

        public static SD.Rectangle WorkArea(MonitorInfo m) { return WorkArea(m.Bounds); }

        public static SD.Rectangle WorkArea(SD.Rectangle bounds)
        {
            foreach (var s in WF.Screen.AllScreens)
                if (s.Bounds == bounds) return s.WorkingArea;
            return bounds;
        }
    }

    /// <summary>신호등 버튼이 있는 일반 창 베이스</summary>
    public class ChromeWindow : Window
    {
        public bool ShowZoom { get; set; }
        public bool ShowMinimize { get; set; } = true;

        public ChromeWindow()
        {
            Style = (Style)Application.Current.FindResource("AppleWindow");
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            var close = GetTemplateChild("PART_Close") as Button;
            var min = GetTemplateChild("PART_Min") as Button;
            var zoom = GetTemplateChild("PART_Zoom") as Button;
            if (close != null) close.Click += (s, e) => Close();
            if (min != null)
            {
                min.Click += (s, e) => WindowState = WindowState.Minimized;
                min.Visibility = ShowMinimize ? Visibility.Visible : Visibility.Collapsed;
            }
            if (zoom != null)
            {
                zoom.Click += (s, e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                zoom.Visibility = ShowZoom ? Visibility.Visible : Visibility.Collapsed;
                StateChanged += (s, e) =>
                {
                    bool max = WindowState == WindowState.Maximized;
                    zoom.Content = max ? "\uE923" : "\uE922";            // 최대화 ↔ 이전 크기로 복원 아이콘
                    zoom.ToolTip = max ? "이전 크기로 복원" : "최대화";
                };
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Native.RoundCorners(new WindowInteropHelper(this).Handle);
        }

        public IntPtr Hwnd => new WindowInteropHelper(this).Handle;
    }

    /// <summary>작은 알림 (저장됨 등)</summary>
    internal class Toast : FloatWindow
    {
        static Toast current;
        readonly DispatcherTimer timer;

        public static void Show(string title, string message = null, string actionText = null, Action action = null, int ms = 3600)
        {
            try
            {
                if (current != null) { try { current.Close(); } catch { } current = null; }
                var t = new Toast(title, message, actionText, action, ms);
                current = t;
                t.Show();
                var m = ScreenGrab.PrimaryMonitor();
                t.PlaceAnchored(m.Bounds, (mon, size) =>
                {
                    var wa = WorkArea(mon);
                    return new SD.Point(wa.Right - size.Width + 4, wa.Bottom - size.Height + 4);
                });
            }
            catch { }
        }

        Toast(string title, string message, string actionText, Action action, int ms)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 12, 14, 12) };
            var badge = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 12, 0),
                Background = (Brush)Application.Current.FindResource("Accent"), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "✓", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            row.Children.Add(badge);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 280 };
            texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(message))
                texts.Children.Add(new TextBlock
                {
                    Text = message, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = (Brush)Application.Current.FindResource("TextSecondary")
                });
            row.Children.Add(texts);
            if (!string.IsNullOrEmpty(actionText) && action != null)
            {
                var b = new Button
                {
                    Content = actionText, Style = (Style)Application.Current.FindResource("PillButton"),
                    Margin = new Thickness(14, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center
                };
                b.Click += (s, e) => { Close(); action(); };
                row.Children.Add(b);
            }
            Content = row;

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (s, e) => { timer.Stop(); FadeOutAndClose(); };
            timer.Start();
            Closed += (s, e) => { timer.Stop(); if (current == this) current = null; };
        }
    }

    /// <summary>애플 알림창 스타일의 확인 대화상자</summary>
    internal class DialogWindow : FloatWindow
    {
        public int Result = -1;

        /// <param name="keys">버튼마다 누르면 바로 실행되는 글자 (없으면 공백). Enter=기본 버튼, Esc=취소(-1)</param>
        public static int Ask(string title, string message, string[] buttons, int primary = 0, int danger = -1, string keys = null)
        {
            var d = new DialogWindow(title, message, buttons, primary, danger, keys);
            d.ShowDialog();
            return d.Result;
        }

        DialogWindow(string title, string message, string[] buttons, int primary, int danger, string keys) : base(false, false)
        {
            ShowActivated = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var panel = new StackPanel { Width = 360, Margin = new Thickness(26, 24, 26, 22) };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(message))
                panel.Children.Add(new TextBlock
                {
                    Text = message, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0),
                    Foreground = (Brush)Application.Current.FindResource("TextSecondary")
                });
            var row = new UniformGrid { Rows = 1, Margin = new Thickness(0, 20, 0, 0) };
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                string styleKey = i == danger ? "DangerButton" : (i == primary ? "PrimaryButton" : "PillButton");
                var b = new Button
                {
                    Content = buttons[i], Style = (Style)Application.Current.FindResource(styleKey),
                    Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0), Padding = new Thickness(10, 8, 10, 8), IsDefault = i == primary
                };
                b.Click += (s, e) => { Result = idx; Close(); };
                row.Children.Add(b);
            }
            panel.Children.Add(row);
            Content = panel;
            PreviewKeyDown += (s, e) =>
            {
                var k = e.Key == System.Windows.Input.Key.ImeProcessed ? e.ImeProcessedKey : e.Key;     // 한글 입력 상태에서도 동작
                if (k == System.Windows.Input.Key.Escape) { Result = -1; Close(); e.Handled = true; return; }
                if (string.IsNullOrEmpty(keys)) return;
                for (int i = 0; i < buttons.Length && i < keys.Length; i++)
                {
                    if (keys[i] == ' ') continue;
                    System.Windows.Input.Key want;
                    if (Enum.TryParse(keys[i].ToString(), true, out want) && want == k) { Result = i; Close(); e.Handled = true; return; }
                }
            };
            Loaded += (s, e) => Activate();
        }
    }
}
