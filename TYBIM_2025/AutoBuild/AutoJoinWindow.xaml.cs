using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TYBIM_2025.AutoBuild
{
    public partial class AutoJoinWindow : Window
    {
        private readonly RadioButton[] hosts;
        private readonly CheckBox[] targets;

        internal AutoJoinWindow(string viewName)
        {
            InitializeComponent();
            hosts = new[] { HostColumns, HostFraming, HostWalls, HostFloors };
            targets = new[] { TargetColumns, TargetFraming, TargetWalls, TargetFloors };
            string scope = $"目前視圖　{viewName}";
            ViewScope.Text = scope;
            ViewScope.ToolTip = scope;

            var assembly = typeof(AutoJoinWindow).Assembly;
            using (var icon = assembly.GetManifestResourceStream("TYBIM.WindowIcon"))
                Icon = BitmapFrame.Create(icon, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            JoinArtwork.Source = LoadArtwork(112);

            ModeChanged(this, new RoutedEventArgs());
            UpdateSelection();
        }

        internal int HostCategory => Array.FindIndex(hosts, row => row.IsChecked == true);
        internal int[] TargetCategories => Enumerable.Range(0, targets.Length)
            .Where(index => targets[index].IsChecked == true).ToArray();
        internal bool Unjoin => UnjoinMode.IsChecked == true;

        internal static BitmapSource LoadArtwork(int width, bool whiteBackground = false)
        {
            using var stream = typeof(AutoJoinWindow).Assembly.GetManifestResourceStream("TYBIM.AutoJoinImage");
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = width;
            image.EndInit();
            if (!whiteBackground)
            {
                image.Freeze();
                return image;
            }

            var bounds = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            var artwork = new DrawingVisual();
            using (var drawing = artwork.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, bounds);
                drawing.DrawImage(image, bounds);
            }

            var ribbonImage = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            ribbonImage.Render(artwork);
            ribbonImage.Freeze();
            return ribbonImage;
        }

        private void ModeChanged(object sender, RoutedEventArgs e)
        {
            if (RunButton == null)
                return;

            RunButton.Content = Unjoin ? "解除接合" : "開始接合";
            RunButton.Background = Unjoin ? System.Windows.Media.Brushes.Firebrick
                : (System.Windows.Media.Brush)FindResource("Teal");
            ModeHint.Text = Unjoin ? "解除目前視圖內所選品類的既有接合。" : "依目前視圖與所選品類建立幾何接合。";
        }

        private void SelectAll(object sender, RoutedEventArgs e) => SetAllTargets(true);
        private void ClearAll(object sender, RoutedEventArgs e) => SetAllTargets(false);
        private void TargetChanged(object sender, RoutedEventArgs e) => UpdateSelection();

        private void SetAllTargets(bool selected)
        {
            foreach (var row in targets)
                row.IsChecked = selected;
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            int count = targets.Count(row => row.IsChecked == true);
            SelectionCount.Text = count == 0 ? "請至少選擇一個品類" : $"已選 {count} 個品類";
            RunButton.IsEnabled = count > 0;
        }

        private void Run(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
