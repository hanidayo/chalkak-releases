using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SD = System.Drawing;

namespace Chalkak
{
    internal static class HudParts
    {
        public static System.Windows.Shapes.Ellipse PulseDot()
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 12, Height = 12, Fill = (Brush)Application.Current.FindResource("Danger"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0)
            };
            dot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            return dot;
        }
        public static Button Btn(string text, string style, Thickness margin)
        {
            return new Button
            {
                Content = text, Style = (Style)Application.Current.FindResource(style), Margin = margin,
                Padding = new Thickness(14, 7, 14, 7), VerticalAlignment = VerticalAlignment.Center
            };
        }
        public static Brush Res(string key) { return (Brush)Application.Current.FindResource(key); }
    }

    internal class RepeatHud : FloatWindow
    {
        public event Action StopRequested;
        readonly TextBlock detail = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        readonly Border fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        readonly double duration;
        readonly MonitorInfo mon;
        readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        readonly DateTime born = DateTime.UtcNow;
        const double BarWidth = 230;

        public RepeatHud(double duration, MonitorInfo mon)
        {
            this.duration = duration;
            this.mon = mon;
            fill.Background = HudParts.Res("Accent");
            detail.Foreground = HudParts.Res("TextSecondary");
            detail.Text = "준비 중…";

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = "반복 촬영 중", FontWeight = FontWeights.SemiBold, FontSize = 14 });
            texts.Children.Add(detail);
            var track = new Grid { Width = BarWidth, Height = 4, Margin = new Thickness(0, 8, 0, 0) };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = HudParts.Res("FillHover") });
            track.Children.Add(fill);
            texts.Children.Add(track);

            var stop = HudParts.Btn("중지  (Esc)", "PrimaryButton", new Thickness(18, 0, 0, 0));
            stop.Click += (s, e) => { var h = StopRequested; if (h != null) h(); };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 14, 16, 14) };
            row.Children.Add(HudParts.PulseDot());
            row.Children.Add(texts);
            row.Children.Add(stop);
            Content = row;

            poll.Tick += (s, e) =>
            {
                if ((DateTime.UtcNow - born).TotalMilliseconds > 600 && (Native.GetAsyncKeyState(Native.VK_ESCAPE) & 0x8000) != 0)
                { poll.Stop(); var h = StopRequested; if (h != null) h(); }
            };
            poll.Start();
        }

        public void PlaceAtTop()
        {
            PlaceAnchored(mon.Bounds, (m, size) => new SD.Point(m.X + (m.Width - size.Width) / 2, m.Y + 20));
        }

        public void Update(double elapsed, int count, int skipped)
        {
            double left = Math.Max(0, duration - elapsed);
            detail.Text = left.ToString("0.0") + "초 남음  ·  " + count + "장" + (skipped > 0 ? "  ·  건너뜀 " + skipped : "");
            fill.Width = Math.Min(1, elapsed / Math.Max(0.1, duration)) * BarWidth;
        }

        public void Finish()
        {
            poll.Stop();
            try { Close(); } catch { }
        }
    }

    internal class ScrollHud : FloatWindow
    {
        public event Action AddClicked, DoneClicked, CancelClicked;
        readonly TextBlock count = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        readonly TextBlock hint = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        readonly MonitorInfo mon;

        public ScrollHud(bool auto, MonitorInfo mon)
        {
            this.mon = mon;
            count.Foreground = HudParts.Res("TextSecondary");
            hint.Foreground = HudParts.Res("TextSecondary");
            count.Text = "1장";

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = auto ? "자동 스크롤 중…" : "수동 스크롤 캡처", FontWeight = FontWeights.SemiBold, FontSize = 14 });
            texts.Children.Add(count);
            if (!auto)
            {
                hint.Text = "스크롤한 뒤 ‘현재 화면 추가’를 눌러 주세요";
                texts.Children.Add(hint);
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 14, 16, 14) };
            row.Children.Add(HudParts.PulseDot());
            row.Children.Add(texts);
            if (auto)
            {
                var stop = HudParts.Btn("중지  (Esc)", "PrimaryButton", new Thickness(20, 0, 0, 0));
                stop.Click += (s, e) => { var h = CancelClicked; if (h != null) h(); };
                row.Children.Add(stop);
            }
            else
            {
                var add = HudParts.Btn("현재 화면 추가", "PillButton", new Thickness(20, 0, 0, 0));
                add.Click += (s, e) => { var h = AddClicked; if (h != null) h(); };
                var done = HudParts.Btn("완료", "PrimaryButton", new Thickness(8, 0, 0, 0));
                done.Click += (s, e) => { var h = DoneClicked; if (h != null) h(); };
                var cancel = HudParts.Btn("취소", "PillButton", new Thickness(8, 0, 0, 0));
                cancel.Click += (s, e) => { var h = CancelClicked; if (h != null) h(); };
                row.Children.Add(add); row.Children.Add(done); row.Children.Add(cancel);
            }
            Content = row;
        }

        public void SetCount(int n) { count.Text = n + "장"; }

        public void SetHotkeyHint(string hk)
        {
            if (!string.IsNullOrEmpty(hk)) hint.Text = "스크롤한 뒤 " + hk + " 또는 ‘현재 화면 추가’";
        }

        public void PlaceAtBottom()
        {
            PlaceAnchored(mon.Bounds, (m, size) =>
            {
                var wa = WorkArea(mon);
                return new SD.Point(m.X + (m.Width - size.Width) / 2, wa.Bottom - size.Height - 12);
            });
        }

        public void Finish() { try { Close(); } catch { } }
    }
}
