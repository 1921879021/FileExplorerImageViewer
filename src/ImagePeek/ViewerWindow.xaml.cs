using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ImagePeek.Core;

namespace ImagePeek
{
    public partial class ViewerWindow : Window
    {
        private static readonly int ViewerMaxPixels = 8192;

        private string[] _files = new string[0];
        private int _index;
        private readonly object _loadLock = new object();
        private int _loadSeq;
        private bool _fitMode = true;
        private double _fitScale = 1.0;
        private double _zoom = 1.0;

        public ViewerWindow(string path)
        {
            InitializeComponent();

            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                _files = Directory.EnumerateFiles(dir)
                    .Where(SupportedFormats.IsSupported)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            if (_files.Length == 0)
            {
                _files = new[] { path };
                _index = 0;
            }
            else
            {
                _index = Math.Max(0, Array.IndexOf(_files, Path.GetFullPath(path)));
            }

            PreviewKeyDown += OnKey;
            MouseWheel += OnWheel;
            MouseDown += OnMouseDown;
            SizeChanged += (s, e) => { if (_fitMode) { ApplyFit(); } };
            Loaded += (s, e) => LoadCurrent();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Left:
                case Key.Up:
                case Key.PageUp:
                    Navigate(-1);
                    e.Handled = true;
                    break;
                case Key.Right:
                case Key.Down:
                case Key.PageDown:
                    Navigate(1);
                    e.Handled = true;
                    break;
                case Key.Home:
                    JumpTo(0);
                    e.Handled = true;
                    break;
                case Key.End:
                    JumpTo(_files.Length - 1);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
                case Key.D1:
                    SetZoom(1.0);
                    e.Handled = true;
                    break;
            }
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            double factor = e.Delta > 0 ? 1.25 : 0.8;
            double target = Math.Max(0.05, Math.Min(32.0, _zoom * factor));
            SetZoom(target);
            e.Handled = true;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                SetZoom(_zoom >= 0.99 && _zoom <= 1.01 ? _fitScale : 1.0);
            }
        }

        private void Navigate(int delta)
        {
            if (_files.Length == 0)
            {
                return;
            }
            int next = Math.Max(0, Math.Min(_files.Length - 1, _index + delta));
            if (next != _index)
            {
                _index = next;
                LoadCurrent();
            }
        }

        private void JumpTo(int index)
        {
            if (_files.Length == 0)
            {
                return;
            }
            index = Math.Max(0, Math.Min(_files.Length - 1, index));
            _index = index;
            LoadCurrent();
        }

        private void LoadCurrent()
        {
            if (_index < 0 || _index >= _files.Length)
            {
                return;
            }

            StopAnimation();
            string path = _files[_index];
            int seq = ++_loadSeq;

            Title = Path.GetFileName(path) + " — ImagePeek";
            TitleText.Text = Path.GetFileName(path) + "   (" + (_index + 1) + " / " + _files.Length + ")";
            InfoText.Text = "加载中…";
            Img.Source = null;

            string ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            bool maybeAnim = ext == "GIF" || ext == "WEBP";

            Task.Run(() =>
            {
                // 动图优先：逐帧解码并播放
                if (maybeAnim)
                {
                    try
                    {
                        var a = DecodeCore.DecodeAnimated(path, ViewerMaxPixels);
                        if (a.Frames.Count > 0)
                        {
                            var sources = a.Frames.Select(ToImageSource).ToArray();
                            var delays = a.DelaysMs.ToArray();
                            int totalMs = delays.Sum();
                            Dispatcher.BeginInvoke((Action)(() =>
                            {
                                if (seq != _loadSeq)
                                {
                                    return;
                                }

                                Img.Source = sources[0];
                                _fitMode = true;
                                ApplyFit();
                                InfoText.Text = string.Format("{0} × {1}   {2}   {3} 动图   {4} 帧 · {5:F1} 秒/循环   ·   {6:P0}",
                                    a.Frames[0].Width, a.Frames[0].Height, FormatBytes(a.FileSize), ext,
                                    sources.Length, totalMs / 1000.0, _zoom);
                                if (sources.Length > 1)
                                {
                                    StartAnimation(sources, delays);
                                }
                            }));
                            Task.Run(() => PreloadNeighbors());
                            return;
                        }
                    }
                    catch
                    {
                        // 落回静态解码
                    }
                }

                DecodeResult r;
                try
                {
                    r = DecodeCore.Decode(path, ViewerMaxPixels);
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (seq != _loadSeq)
                        {
                            return;
                        }
                        InfoText.Text = "解码失败：" + ex.Message;
                    }));
                    return;
                }

                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (seq != _loadSeq)
                    {
                        return;
                    }

                    Img.Source = ToImageSource(r.Bitmap);
                    _fitMode = true;
                    ApplyFit();
                    InfoText.Text = string.Format("{0} × {1}   {2}   {3}   ·   {4} ({5:F0} ms)   ·   {6:P0}",
                        r.Width, r.Height, FormatBytes(r.FileSize),
                        System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                        r.Decoder, r.Ms, _zoom);
                }));

                // 相邻预加载：当前图显示后立刻解码前后各 2 张进缓存
                Task.Run(() => PreloadNeighbors());
            });
        }

        // ---------- 动图播放（WPF DispatcherTimer）----------

        private System.Windows.Threading.DispatcherTimer _animTimer;
        private System.Windows.Media.Imaging.BitmapSource[] _animSources;
        private int[] _animDelays;
        private int _animIndex;

        private void StartAnimation(System.Windows.Media.Imaging.BitmapSource[] sources, int[] delays)
        {
            _animSources = sources;
            _animDelays = delays;
            _animIndex = 0;

            if (_animTimer == null)
            {
                _animTimer = new System.Windows.Threading.DispatcherTimer();
                _animTimer.Tick += (s, e) =>
                {
                    if (_animSources == null || _animSources.Length < 2)
                    {
                        _animTimer.Stop();
                        return;
                    }
                    _animIndex = (_animIndex + 1) % _animSources.Length;
                    Img.Source = _animSources[_animIndex];
                    _animTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _animDelays[_animIndex]));
                };
            }
            _animTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, delays[0]));
            _animTimer.Start();
        }

        private void StopAnimation()
        {
            _animTimer?.Stop();
            _animSources = null;
            _animDelays = null;
            _animIndex = 0;
        }

        private void PreloadNeighbors()
        {
            int[] offsets = { 1, -1, 2, -2 };
            foreach (int off in offsets)
            {
                int i = _index + off;
                if (i < 0 || i >= _files.Length)
                {
                    continue;
                }
                try
                {
                    lock (_loadLock)
                    {
                        DecodeCore.Decode(_files[i], ViewerMaxPixels);
                    }
                }
                catch
                {
                    // 预加载失败不打扰用户
                }
            }
        }

        private static System.Windows.Media.Imaging.BitmapSource ToImageSource(System.Drawing.Bitmap bmp)
        {
            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var src = System.Windows.Media.Imaging.BitmapSource.Create(
                    data.Width, data.Height, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32,
                    null,
                    data.Scan0,
                    data.Stride * data.Height,
                    data.Stride);
                src.Freeze();
                return src;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private void ApplyFit()
        {
            if (Img.Source == null)
            {
                return;
            }

            double vw = Scroll.ViewportWidth > 10 ? Scroll.ViewportWidth : ActualWidth - 16;
            double vh = Scroll.ViewportHeight > 10 ? Scroll.ViewportHeight : ActualHeight - 16;

            double kx = vw / Img.Source.Width;
            double ky = vh / Img.Source.Height;
            _fitScale = Math.Min(kx, ky);

            _zoom = _fitScale;
            ApplyZoom();
        }

        private void SetZoom(double z)
        {
            if (Img.Source == null)
            {
                return;
            }

            double old = _zoom;
            _zoom = Math.Max(0.05, Math.Min(32.0, z));
            _fitMode = Math.Abs(_zoom - _fitScale) < 0.001;
            ApplyZoomPreservingCenter(old);
            UpdateInfoZoom();
        }

        private void ApplyZoom()
        {
            if (Img.Source == null)
            {
                return;
            }
            ZoomT.ScaleX = _zoom;
            ZoomT.ScaleY = _zoom;
            Img.Stretch = System.Windows.Media.Stretch.None;
            Img.Width = Math.Max(1, Math.Round(Img.Source.Width));
            Img.Height = Math.Max(1, Math.Round(Img.Source.Height));
        }

        private void ApplyZoomPreservingCenter(double oldZoom)
        {
            if (oldZoom <= 0)
            {
                ApplyZoom();
                return;
            }

            double cx = Scroll.HorizontalOffset + Scroll.ViewportWidth / 2;
            double cy = Scroll.VerticalOffset + Scroll.ViewportHeight / 2;
            double rx = cx / oldZoom;
            double ry = cy / oldZoom;

            ApplyZoom();

            Scroll.UpdateLayout();
            Scroll.ScrollToHorizontalOffset(Math.Max(0, rx * _zoom - Scroll.ViewportWidth / 2));
            Scroll.ScrollToVerticalOffset(Math.Max(0, ry * _zoom - Scroll.ViewportHeight / 2));
        }

        private void UpdateInfoZoom()
        {
            string cur = InfoText.Text;
            int idx = cur.LastIndexOf("·", StringComparison.Ordinal);
            if (idx >= 0)
            {
                InfoText.Text = cur.Substring(0, idx + 1) + "   " + _zoom.ToString("P0");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }
            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024.0).ToString("F1") + " KB";
            }
            if (bytes < 1024L * 1024 * 1024)
            {
                return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
            }
            return (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GB";
        }
    }
}
