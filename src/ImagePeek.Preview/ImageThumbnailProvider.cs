using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ImagePeek.Core;

namespace ImagePeek.Preview
{
    /// <summary>
    /// 缩略图提供程序：让资源管理器文件夹里直接显示图片内容。
    /// 契约：shell 传入期望的最大边长 cx，返回等比缩放、长边不超过 cx 的 HBITMAP
    ///（32bpp 预乘 alpha DIB），大小不一由 shell 负责排版与缓存。
    /// </summary>
    [ComVisible(true)]
    [Guid(PreviewRegistration.ThumbClsid)]
    [ClassInterface(ClassInterfaceType.None)]
    [ProgId("ImagePeek.ThumbnailProvider")]
    public sealed class ImageThumbnailProvider : IInitializeWithFile, IThumbnailProvider
    {
        private string _filePath;

        static ImageThumbnailProvider()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try
                {
                    if (e.RequestingAssembly == null)
                    {
                        return null;
                    }

                    string dir = Path.GetDirectoryName(e.RequestingAssembly.Location);
                    if (string.IsNullOrEmpty(dir))
                    {
                        return null;
                    }

                    string name = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
                    string candidate = Path.Combine(dir, name);
                    return File.Exists(candidate) ? System.Reflection.Assembly.LoadFrom(candidate) : null;
                }
                catch
                {
                    return null;
                }
            };
        }

        int IInitializeWithFile.Initialize(string pszFilePath, uint grfMode)
        {
            _filePath = pszFilePath;
            return 0; // S_OK
        }

        int IThumbnailProvider.GetThumbnail(uint cx, out IntPtr phbm)
        {
            phbm = IntPtr.Zero;
            try
            {
                int max = (int)Math.Min(Math.Max(cx, 32u), 2048u);
                DecodeResult r = DecodeCore.Decode(_filePath, max, CancellationToken.None);

                phbm = GdiHelper.CreatePremultipliedHbitmap(r.Bitmap);
                return phbm != IntPtr.Zero ? 0 : unchecked((int)0x80004005);
            }
            catch (Exception ex)
            {
                Log("Thumb EX [" + _filePath + "]: " + ex.Message);
                return ex.HResult; // 失败时 Explorer 显示默认图标
            }
        }

        private static void Log(string msg)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ImagePeek");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "thumb.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff ") + msg + "\r\n");
            }
            catch
            {
            }
        }
    }

    internal static class GdiHelper
    {
        public static IntPtr CreatePremultipliedHbitmap(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;          // top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;      // BI_RGB

            IntPtr bits;
            IntPtr hbm = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out bits, IntPtr.Zero, 0);
            if (hbm == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            unsafe
            {
                byte* src = (byte*)bd.Scan0;
                byte* dst = (byte*)bits;
                for (int y = 0; y < h; y++)
                {
                    byte* s = src + (long)y * bd.Stride;
                    byte* d = dst + (long)y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = s[0], g = s[1], r = s[2], a = s[3];
                        // 预乘 alpha（shell 缩略图标准格式）
                        d[0] = (byte)((b * a + 127) / 255);
                        d[1] = (byte)((g * a + 127) / 255);
                        d[2] = (byte)((r * a + 127) / 255);
                        d[3] = a;
                        s += 4;
                        d += 4;
                    }
                }
            }
            bmp.UnlockBits(bd);
            return hbm;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
    }
}
