using System;
using System.Drawing;
using System.Windows.Forms;

namespace ImagePeek.Preview
{
    /// <summary>
    /// 预览窗格的渲染控件：透明棋盘格背景 + 等比缩放 + 状态/信息文字。
    /// </summary>
    internal sealed class PreviewControl : Control
    {
        private Bitmap _image;
        private bool _hasAlpha;
        private string _message;
        private string _info;
        private Color _bgColor = Color.White;

        // 动图播放
        private Bitmap[] _animFrames;
        private int[] _animDelays;
        private int _animIndex;
        private System.Windows.Forms.Timer _animTimer;

        private const int CheckerCell = 8;
        private static readonly Color CheckerA = Color.White;
        private static readonly Color CheckerB = Color.FromArgb(0xE9, 0xE9, 0xE9);

        public PreviewControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.Opaque, true);
            SetStyle(ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.DoubleBuffer, true);
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }

        public void SetImage(Bitmap bmp, bool hasAlpha, string info)
        {
            StopAnimation();
            _image = bmp;
            _hasAlpha = hasAlpha;
            _info = info;
            _message = null;
            Invalidate();
            Update();
        }

        /// <summary>播放动图：逐帧切换，帧延迟由文件数据决定。</summary>
        public void SetAnimation(Bitmap[] frames, int[] delaysMs, bool hasAlpha, string info)
        {
            if (frames == null || frames.Length == 0)
            {
                return;
            }

            _animFrames = frames;
            _animDelays = delaysMs;
            _animIndex = 0;
            _image = frames[0];
            _hasAlpha = hasAlpha;
            _info = info;
            _message = null;

            if (_animTimer == null)
            {
                _animTimer = new System.Windows.Forms.Timer();
                _animTimer.Tick += OnAnimTick;
            }
            _animTimer.Interval = Math.Max(20, delaysMs[0]);
            _animTimer.Start();

            Invalidate();
            Update();
        }

        private void StopAnimation()
        {
            _animTimer?.Stop();
            _animFrames = null;
            _animDelays = null;
            _animIndex = 0;
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            if (IsDisposed || _animFrames == null || _animFrames.Length < 2)
            {
                _animTimer?.Stop();
                return;
            }

            _animIndex = (_animIndex + 1) % _animFrames.Length;
            _image = _animFrames[_animIndex];
            _animTimer.Interval = Math.Max(20, _animDelays[_animIndex]);
            Invalidate();
        }

        public void SetMessage(string message)
        {
            StopAnimation();
            _message = message;
            Invalidate();
            Update();
        }

        public void SetBackground(Color c)
        {
            _bgColor = c;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_bgColor);

            try
            {
                if (_image != null)
                {
                    Rectangle rect = FitRect(_image.Size, ClientSize);
                    if (_hasAlpha)
                    {
                        DrawChecker(g, rect);
                    }

                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                    g.DrawImage(_image, rect);

                    DrawInfoBar(g);
                }
                else if (!string.IsNullOrEmpty(_message))
                {
                    TextRenderer.DrawText(g, _message, Font, ClientRectangle,
                        Color.FromArgb(0x8A, 0x8A, 0x8A),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                }
            }
            catch
            {
                // 位图被缓存淘汰的竞态：忽略这一帧
            }
        }

        private void DrawInfoBar(Graphics g)
        {
            if (string.IsNullOrEmpty(_info))
            {
                return;
            }

            Font font = new Font("Microsoft YaHei UI", 8.25f, FontStyle.Regular);
            Size ts = TextRenderer.MeasureText(_info, font);
            int pad = 6;
            Rectangle bar = new Rectangle(0, ClientSize.Height - ts.Height - pad * 2, Math.Min(ClientSize.Width, ts.Width + pad * 2), ts.Height + pad * 2);
            using (Brush b = new SolidBrush(Color.FromArgb(160, 0xFA, 0xFA, 0xFA)))
            {
                g.FillRectangle(b, bar);
            }
            TextRenderer.DrawText(g, _info, font,
                new Rectangle(bar.X + pad, bar.Y + pad, bar.Width - pad, bar.Height - pad),
                Color.FromArgb(0x60, 0x60, 0x60));
        }

        private static Rectangle FitRect(Size imgSize, Size client)
        {
            if (imgSize.Width <= 0 || imgSize.Height <= 0 || client.Width <= 0 || client.Height <= 0)
            {
                return Rectangle.Empty;
            }

            double kx = (double)client.Width / imgSize.Width;
            double ky = (double)client.Height / imgSize.Height;
            double k = Math.Min(kx, ky);

            int w = Math.Max(1, (int)Math.Round(imgSize.Width * k));
            int h = Math.Max(1, (int)Math.Round(imgSize.Height * k));
            int x = (client.Width - w) / 2;
            int y = (client.Height - h) / 2;
            return new Rectangle(x, y, w, h);
        }

        private static void DrawChecker(Graphics g, Rectangle rect)
        {
            using (Brush a = new SolidBrush(CheckerA))
            using (Brush b = new SolidBrush(CheckerB))
            {
                for (int y = rect.Top; y < rect.Bottom; y += CheckerCell)
                {
                    int row = (y - rect.Top) / CheckerCell;
                    for (int x = rect.Left; x < rect.Right; x += CheckerCell)
                    {
                        int col = (x - rect.Left) / CheckerCell;
                        g.FillRectangle(((row + col) & 1) == 0 ? a : b,
                            x, y, Math.Min(CheckerCell, rect.Right - x), Math.Min(CheckerCell, rect.Bottom - y));
                    }
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 位图归 DecodeCore 缓存/动图帧数组所有，这里只停动画
                StopAnimation();
                _animTimer?.Dispose();
                _animTimer = null;
                _image = null;
            }
            base.Dispose(disposing);
        }
    }
}
