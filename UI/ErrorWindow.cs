using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Chalkak
{
    /// <summary>오류 내용을 선택·복사할 수 있고 저절로 사라지지 않는 창. 다른 캡처 프로그램에도 찍힌다.</summary>
    internal class ErrorWindow : Window
    {
        public static string LogPath => Path.Combine(AppSettings.DataDir, "error.log");

        public static void Show(string title, Exception ex)
        {
            string text = title + "\r\n\r\n" + ex;
            try
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + text + "\r\n\r\n");
            }
            catch { }
            try { Clipboard.SetText(text); } catch { }
            try { new ErrorWindow(title, text).Show(); } catch { }
        }

        ErrorWindow(string title, string text)
        {
            Title = "찰칵 - 오류";
            Width = 640; Height = 380;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var head = new TextBlock { Text = title + " (내용이 클립보드에 복사되었어요)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
            var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
            Grid.SetRow(box, 1);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var copy = new Button { Content = "복사", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0) };
            copy.Click += (s, e) => { try { Clipboard.SetText(text); } catch { } };
            var ok = new Button { Content = "닫기", Padding = new Thickness(16, 5, 16, 5), IsDefault = true };
            ok.Click += (s, e) => Close();
            row.Children.Add(copy); row.Children.Add(ok);
            Grid.SetRow(row, 2);
            grid.Children.Add(head); grid.Children.Add(box); grid.Children.Add(row);
            Content = grid;
        }
    }
}
