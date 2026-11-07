using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Chalkak
{
    internal class FrameVM : INotifyPropertyChanged
    {
        public CapturedFrame F;
        public event PropertyChangedEventHandler PropertyChanged;
        bool selected, saved;
        string error;
        public string SavedPath;
        public Action OnSelectionChanged;

        public BitmapSource Thumb => F.Thumb;
        public string Label => "#" + F.Index + "   " + F.Time.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture);
        public bool Saved { get { return saved; } set { saved = value; Raise("Saved"); Raise("StatusText"); Raise("StatusBrush"); } }
        public string Error { get { return error; } set { error = value; Raise("StatusText"); Raise("StatusBrush"); } }

        public bool IsSelected
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value;
                Raise("IsSelected");
                var h = OnSelectionChanged; if (h != null) h();
            }
        }

        public string StatusText => Saved ? "저장됨" : (!string.IsNullOrEmpty(Error) ? "저장 실패" : "");
        public Brush StatusBrush => (Brush)Application.Current.FindResource(Saved ? "Success" : "Danger");

        void Raise(string n) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(n)); }
    }

    internal class GroupVM : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public List<FrameVM> All = new List<FrameVM>();
        public ObservableCollection<FrameVM> Visible { get; } = new ObservableCollection<FrameVM>();
        bool expanded;

        public bool IsGroup => All.Count > 1;
        public Visibility HeaderVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;
        public string HeaderText => "비슷한 사진 " + All.Count + "장  ·  " + All[0].F.Time.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture);
        public string ToggleText => expanded ? "접기" : "펼치기";

        public void Refresh()
        {
            Visible.Clear();
            if (!IsGroup || expanded) foreach (var f in All) Visible.Add(f);
            else Visible.Add(All[0]);          // 접힌 묶음은 대표(첫 사진)만 보여 준다
            var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs("ToggleText"));
        }

        public void Toggle() { expanded = !expanded; Refresh(); }
    }

    public partial class GalleryWindow : ChromeWindow
    {
        static readonly List<GalleryWindow> OpenGalleries = new List<GalleryWindow>();

        readonly RepeatSession session;
        readonly List<FrameVM> frames = new List<FrameVM>();
        List<GroupVM> groups = new List<GroupVM>();
        bool force, closedFlag, keepTemp;

        internal GalleryWindow(RepeatSession s)
        {
            InitializeComponent();
            session = s;
            foreach (var f in s.Frames)
            {
                var vm = new FrameVM { F = f };
                vm.OnSelectionChanged = UpdateStatus;
                frames.Add(vm);
            }
            TitleText.Text = s.NameBase + " · " + frames.Count + "장 촬영";
            string sub = s.Started.ToString("yyyy년 M월 d일 HH:mm:ss", CultureInfo.InvariantCulture);
            if (s.Skipped > 0) sub += "  ·  " + s.Skipped + "장은 저장이 밀려 건너뛰었어요";
            if (!string.IsNullOrEmpty(s.EndReason)) sub += "  ·  " + s.EndReason;
            SubText.Text = sub + "   |   사진을 눌러 선택하고, 두 번 누르면 크게 봐요";
            GroupSwitch.IsChecked = AppSettings.Current.GroupSimilar;
            Rebuild();
            UpdateStatus();
            OpenGalleries.Add(this);
            PreviewKeyDown += (a, e) =>
            {
                if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { SelectAll_Click(null, null); e.Handled = true; }
                else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { Save_Click(null, null); e.Handled = true; }
            };
        }

        internal static bool CloseAll()
        {
            foreach (var g in OpenGalleries.ToList())
            {
                g.Activate();
                g.Close();
                if (!g.closedFlag) return false;
            }
            return true;
        }

        void Rebuild()
        {
            groups.Clear();
            if (GroupSwitch.IsChecked == true)
            {
                var byFrame = frames.ToDictionary(v => v.F);
                foreach (var g in Similarity.Group(frames.Select(v => v.F).ToList(), AppSettings.Current.SimilarSensitivity))
                {
                    var gv = new GroupVM();
                    foreach (var f in g) gv.All.Add(byFrame[f]);
                    groups.Add(gv);
                }
            }
            else
            {
                foreach (var v in frames) { var gv = new GroupVM(); gv.All.Add(v); groups.Add(gv); }
            }
            foreach (var g in groups) g.Refresh();
            GroupList.ItemsSource = null;
            GroupList.ItemsSource = groups;
        }

        void UpdateStatus()
        {
            int sel = frames.Count(f => f.IsSelected);
            int pending = frames.Count(f => f.IsSelected && !f.Saved);
            StatusText.Text = frames.Count + "장 중 " + sel + "장 선택";
            SaveBtn.IsEnabled = pending > 0;
            SaveBtn.Content = pending > 0 ? "선택한 사진 저장 (" + pending + ")" : "선택한 사진 저장";
        }

        // ---------------- 이벤트 ----------------
        void GroupSwitch_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Current.GroupSimilar = GroupSwitch.IsChecked == true;
            AppSettings.Current.Save();
            Rebuild();
        }

        void ToggleGroup_Click(object sender, RoutedEventArgs e)
        {
            var g = (sender as FrameworkElement)?.Tag as GroupVM;
            if (g != null) g.Toggle();
        }

        void SelectGroup_Click(object sender, RoutedEventArgs e)
        {
            var g = (sender as FrameworkElement)?.Tag as GroupVM;
            if (g == null) return;
            bool all = g.All.All(f => f.IsSelected);
            foreach (var f in g.All) f.IsSelected = !all;
        }

        void Card_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var vm = (sender as FrameworkElement)?.DataContext as FrameVM;
            if (vm == null) return;
            // 체크박스를 직접 누른 경우는 체크박스가 처리한다
            if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) != null) return;
            if (e.ClickCount >= 2)
            {
                vm.IsSelected = !vm.IsSelected;           // 첫 클릭에서 바뀐 선택을 되돌리고
                var list = frames;
                new ViewerWindow(list, list.IndexOf(vm)).Show();
                e.Handled = true;
                return;
            }
            vm.IsSelected = !vm.IsSelected;
        }

        static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T) return (T)d;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var f in frames) f.IsSelected = true; }
        void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var f in frames) f.IsSelected = false; }
        void Folder_Click(object sender, RoutedEventArgs e) { ExplorerUtil.OpenFolder(AppSettings.Current.SaveFolder); }

        async void Save_Click(object sender, RoutedEventArgs e)
        {
            var todo = frames.Where(f => f.IsSelected && !f.Saved).ToList();
            if (todo.Count == 0) { Toast.Show("저장할 사진을 먼저 선택해 주세요"); return; }
            SaveBtn.IsEnabled = false;
            string folder = AppSettings.Current.SaveFolder;
            string nameBase = FileNaming.Sanitize(session.NameBase, "반복");

            var results = await Task.Run(() =>
            {
                var list = new List<Tuple<FrameVM, string, string>>();
                foreach (var vm in todo)
                {
                    try
                    {
                        string p = SaveService.CopyFile(vm.F.Path, folder, nameBase + "_" + FileNaming.Stamp(vm.F.Time));
                        list.Add(Tuple.Create(vm, p, (string)null));
                    }
                    catch (Exception ex) { list.Add(Tuple.Create(vm, (string)null, ex.Message)); }
                }
                return list;
            });

            int ok = 0, fail = 0;
            string firstPath = null, firstError = null;
            foreach (var r in results)
            {
                if (r.Item2 != null) { r.Item1.SavedPath = r.Item2; r.Item1.Error = null; r.Item1.Saved = true; ok++; if (firstPath == null) firstPath = r.Item2; }
                else { r.Item1.Error = r.Item3; fail++; if (firstError == null) firstError = r.Item3; }
            }
            UpdateStatus();
            ResultText.Text = ok + "장 저장됨" + (fail > 0 ? "  ·  " + fail + "장 실패 (다시 저장을 눌러 재시도)" : "");
            FolderBtn.Visibility = Visibility.Visible;
            if (fail > 0) Toast.Show(fail + "장을 저장하지 못했어요", firstError, null, null, 5500);
            else { string p = firstPath; Toast.Show(ok + "장을 저장했어요", folder, "폴더 열기", () => ExplorerUtil.Reveal(p)); }
        }

        // ---------------- 닫기 ----------------
        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (force) return;
            int unsaved = frames.Count(f => !f.Saved);
            if (unsaved == 0) return;
            int r = DialogWindow.Ask("저장하지 않은 사진 " + unsaved + "장이 있어요",
                "‘폐기’하면 되돌릴 수 없어요. 나중에 다시 보려면 ‘보관’을 선택하세요 (트레이 메뉴의 ‘보관된 반복 촬영 열기’).",
                new[] { "폐기하고 닫기", "취소", "보관" }, 2, 0);
            if (r == 0) return;
            if (r == 2) { keepTemp = true; return; }
            e.Cancel = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            closedFlag = true;
            OpenGalleries.Remove(this);
            RepeatSession.OpenDirs.Remove(session.Dir);
            if (!keepTemp) session.DeleteFiles();          // 세션 임시 폴더만 정리 (최종 저장 파일은 건드리지 않음)
            GroupList.ItemsSource = null;
            frames.Clear();
        }
    }

    /// <summary>사진 크게 보기 (←/→ 이동, 스페이스로 선택)</summary>
    internal class ViewerWindow : ChromeWindow
    {
        readonly List<FrameVM> list;
        int index;
        readonly Image img = new Image { Stretch = Stretch.Uniform };
        readonly TextBlock label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        readonly Button selBtn;

        public ViewerWindow(List<FrameVM> list, int index)
        {
            this.list = list;
            this.index = Math.Max(0, index);
            Title = "크게 보기";
            Width = 1000; Height = 700; MinWidth = 520; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowZoom = true;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var imgHost = new Border { Margin = new Thickness(20, 16, 20, 8), CornerRadius = new CornerRadius(14), Background = (Brush)FindResource("Canvas"), Child = img };
            img.Margin = new Thickness(12);
            root.Children.Add(imgHost);

            var bar = new DockPanel { Margin = new Thickness(24, 6, 24, 18) };
            Grid.SetRow(bar, 1);
            var prev = new Button { Content = "◀", Style = (Style)FindResource("PillButton"), Padding = new Thickness(16, 8, 16, 8) };
            var next = new Button { Content = "▶", Style = (Style)FindResource("PillButton"), Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0) };
            selBtn = new Button { Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(20, 8, 20, 8) };
            prev.Click += (s, e) => Go(-1);
            next.Click += (s, e) => Go(1);
            selBtn.Click += (s, e) => { list[this.index].IsSelected = !list[this.index].IsSelected; UpdateLabel(); };
            DockPanel.SetDock(selBtn, Dock.Right);
            DockPanel.SetDock(next, Dock.Left);
            DockPanel.SetDock(prev, Dock.Left);
            bar.Children.Add(selBtn);
            bar.Children.Add(prev);
            bar.Children.Add(next);
            label.Margin = new Thickness(16, 0, 0, 0);
            bar.Children.Add(label);
            root.Children.Add(bar);
            Content = root;

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Left) { Go(-1); e.Handled = true; }
                else if (e.Key == Key.Right) { Go(1); e.Handled = true; }
                else if (e.Key == Key.Space) { selBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
                else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };
            Loaded += (s, e) => Show_();
        }

        void Go(int d)
        {
            int n = index + d;
            if (n < 0 || n >= list.Count) return;
            index = n;
            Show_();
        }

        void Show_()
        {
            try { img.Source = ImageUtil.Load(list[index].F.Path); } catch { img.Source = list[index].Thumb; }
            UpdateLabel();
        }

        void UpdateLabel()
        {
            var vm = list[index];
            label.Text = (index + 1) + " / " + list.Count + "    " + vm.Label;
            selBtn.Content = vm.IsSelected ? "선택 해제 (Space)" : "선택 (Space)";
        }
    }
}
