using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ImagePeek.Core;
using ImagePeek.Preview;

namespace ImagePeek.TestHost
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Native.SetProcessDPIAware(); // 物理像素坐标，避免 DPI 虚拟化导致截屏偏移
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

            try
            {
                if (cmd == "decode")
                {
                    return DecodeSmoke();
                }
                if (cmd == "com")
                {
                    string file = args.Length > 1 ? args[1] : null;
                    return ComHostTest(file, cleanup: false);
                }
                if (cmd == "comclean")
                {
                    string file = args.Length > 1 ? args[1] : null;
                    return ComHostTest(file, cleanup: true);
                }
                if (cmd == "sur")
                {
                    string file = args.Length > 1 ? args[1] : null;
                    return SurrogateTest(file);
                }
                if (cmd == "magicktest")
                {
                    return MagickMinimalTest();
                }
                if (cmd == "all")
                {
                    int r1 = DecodeSmoke();
                    int r2 = ComHostTest(null, cleanup: true);
                    return (r1 == 0 && r2 == 0) ? 0 : 1;
                }
                Console.WriteLine("用法: TestHost [decode|com <file>|comclean <file>|all]");
                return 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 1;
            }
        }

        // ---------- 代理宿主测试（CLSCTX_LOCAL_SERVER，强制 prevhost，与 Explorer 一致） ----------

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

        private static int SurrogateTest(string file)
        {
            Console.WriteLine("=== 代理宿主（prevhost）测试 ===");
            if (file == null || !File.Exists(file))
            {
                Console.WriteLine("用法: TestHost sur <文件路径>");
                return 2;
            }

            // 确保已注册（HKLM）
            string dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImagePeek.Preview.dll");
            string asmName = System.Reflection.AssemblyName.GetAssemblyName(dll).FullName;
            PreviewRegistration.RegisterHandler(dll, asmName);
            Console.WriteLine("  已注册: " + dll);

            var clsid = new Guid(PreviewRegistration.Clsid);
            var iidIUnknown = new Guid("00000000-0000-0000-C000-000000000046");
            const uint CLSCTX_LOCAL_SERVER = 0x4;

            object comObj;
            try
            {
                CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iidIUnknown, out comObj);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] CoCreateInstance(LOCAL_SERVER): " + ex.Message);
                return 1;
            }
            Console.WriteLine("  CoCreateInstance(LOCAL_SERVER) OK —— 对象运行在 prevhost 代理进程内");

            int result = 1;
            IPreviewHandler handler = null;
            var form = new ClipForm
            {
                Text = "SurrogateHostTest",
                Size = new System.Drawing.Size(600, 500),
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(60, 60),
                ShowInTaskbar = false
            };
            form.Show();
            form.CreateControl();
            try
            {
                var init = (IInitializeWithFile)comObj;
                int hr = init.Initialize(file, 0);
                if (hr != 0)
                {
                    Console.WriteLine("  [FAIL] Initialize hr=0x" + hr.ToString("X8"));
                    goto Done;
                }

                handler = (IPreviewHandler)comObj;
                var rect = new RECT { Left = 0, Top = 0, Right = 580, Bottom = 460 };
                hr = handler.SetWindow(form.Handle, ref rect);
                if (hr != 0)
                {
                    Console.WriteLine("  [FAIL] SetWindow hr=0x" + hr.ToString("X8"));
                    goto Done;
                }

                hr = handler.DoPreview();
                Console.WriteLine("  DoPreview hr=0x" + hr.ToString("X8"));

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 8000)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                }

                IntPtr child = Native.GetWindow(form.Handle, Native.GW_CHILD);
                if (child == IntPtr.Zero)
                {
                    Console.WriteLine("  [FAIL] 预览子窗口未创建（查看 %LocalAppData%\\ImagePeek\\handler.log）");
                    goto Done;
                }
                Console.WriteLine("  [OK] prevhost 内渲染成功 (hwnd=0x" + child.ToString("X") + ")");
                try
                {
                    var r = new Native.RECT();
                    Native.GetWindowRect(child, ref r);
                    IntPtr parent = Native.GetAncestor(child, 1 /* GA_PARENT */);
                    Console.WriteLine("  子窗口 rect=({0},{1})-({2},{3})  parent=0x{4:X} (form=0x{5:X})",
                        r.Left, r.Top, r.Right, r.Bottom, parent, form.Handle);
                }
                catch { }

                // 像素级检查：直接截取子窗口在屏幕上的实际内容
                try
                {
                    Thread.Sleep(500);
                    var r = new Native.RECT();
                    Native.GetWindowRect(child, ref r);
                    int cw = Math.Min(300, r.Right - r.Left);
                    int ch = Math.Min(240, r.Bottom - r.Top);
                    int distinctColors;
                    string capturePath = Path.Combine(Path.GetTempPath(), "ImagePeekSurCapture.png");
                    using (var bmp = new System.Drawing.Bitmap(cw, ch))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(cw, ch));
                        }
                        var colors = new System.Collections.Generic.HashSet<int>();
                        for (int y = 0; y < ch; y += 4)
                        {
                            for (int x = 0; x < cw; x += 4)
                            {
                                colors.Add(bmp.GetPixel(x, y).ToArgb());
                            }
                        }
                        distinctColors = colors.Count;
                        bmp.Save(capturePath);
                    }
                    Console.WriteLine("  像素检查: 不同颜色数=" + distinctColors + (distinctColors > 8 ? " → [OK] 已画出内容 (截图: " + capturePath + ")" : " → [FAIL] 疑似空白"));
                }
                catch (Exception px)
                {
                    Console.WriteLine("  像素检查异常: " + px.Message);
                }
                result = 0;

                Done:
                try { handler?.Unload(); } catch { }
            }
            finally
            {
                if (comObj != null && Marshal.IsComObject(comObj))
                {
                    Marshal.ReleaseComObject(comObj);
                }
                form.Dispose();
            }
            return result;
        }

        // ---------- Magick 最小调用测试 ----------

        private static int MagickMinimalTest()
        {
            Console.WriteLine("=== Magick 最小调用测试 ===");
            try
            {
                if (!NativeLoader.EnsureLoaded())
                {
                    Console.WriteLine("  [FAIL] NativeLoader");
                    return 1;
                }

                string jpg = Path.Combine(Path.GetTempPath(), "ImagePeekTests", "test.jpg");
                if (!File.Exists(jpg))
                {
                    Console.WriteLine("  [FAIL] 找不到 " + jpg);
                    return 1;
                }

                using (var im = new ImageMagick.MagickImage(jpg))
                {
                    Console.WriteLine("  Read OK: " + im.Width + "x" + im.Height);
                    im.Resize(128, 128);
                    im.Format = ImageMagick.MagickFormat.Png;
                    byte[] png = im.ToByteArray();
                    Console.WriteLine("  ToByteArray OK: " + png.Length + " bytes");
                }
                Console.WriteLine("  [PASS] Magick 调用正常");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        // ---------- 解码冒烟测试 ----------

        private static int DecodeSmoke()
        {
            Console.WriteLine("=== DecodeCore 冒烟测试 ===");
            string dir = Path.Combine(Path.GetTempPath(), "ImagePeekTests");
            Directory.CreateDirectory(dir);
            CreateTestImages(dir);

            int failures = 0;
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                string sw = Stopwatch.StartNew().Elapsed.ToString();
                try
                {
                    var r = DecodeCore.Decode(file);
                    Console.WriteLine(string.Format(
                        "  [OK]   {0,-12} {1}x{2,-6} {3,-8} {4:F0}ms",
                        Path.GetFileName(file), r.Width, r.Height, r.Decoder, r.Ms));
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine("  [FAIL] " + Path.GetFileName(file) + " -> " + ex.Message);
                }
                _ = sw;
            }

            Console.WriteLine(failures == 0 ? "解码测试全部通过" : failures + " 个失败");
            return failures == 0 ? 0 : 1;
        }

        private static void CreateTestImages(string dir)
        {
            // GDI+ 生成常规格式
            using (var bmp = new System.Drawing.Bitmap(300, 200))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.CornflowerBlue);
                g.FillEllipse(System.Drawing.Brushes.Orange, 80, 40, 140, 120);
                bmp.Save(Path.Combine(dir, "test.jpg"), System.Drawing.Imaging.ImageFormat.Jpeg);
                bmp.Save(Path.Combine(dir, "test.bmp"), System.Drawing.Imaging.ImageFormat.Bmp);
                bmp.Save(Path.Combine(dir, "test.gif"), System.Drawing.Imaging.ImageFormat.Gif);
            }

            // Magick 生成现代/专业格式样张
            if (!NativeLoader.EnsureLoaded())
            {
                Console.WriteLine("  [WARN] Magick 不可用，跳过现代格式样张生成");
                return;
            }

            try
            {
                string jpg = Path.Combine(dir, "test.jpg");
                using (var im = new ImageMagick.MagickImage(jpg))
                {
                    TryMagickWrite(im, Path.Combine(dir, "test.webp"), ImageMagick.MagickFormat.WebP);
                    TryMagickWrite(im, Path.Combine(dir, "test.tif"), ImageMagick.MagickFormat.Tiff);
                    TryMagickWrite(im, Path.Combine(dir, "test.tga"), ImageMagick.MagickFormat.Tga);
                    TryMagickWrite(im, Path.Combine(dir, "test.avif"), ImageMagick.MagickFormat.Avif);
                    TryMagickWrite(im, Path.Combine(dir, "test.heic"), ImageMagick.MagickFormat.Heic);

                    // 16 位 PNG（用户场景：资源管理器缩略图/预览失败的那类文件）
                    using (var im16 = new ImageMagick.MagickImage(im))
                    {
                        im16.Depth = 16;
                        TryMagickWrite(im16, Path.Combine(dir, "test16bit.png"), ImageMagick.MagickFormat.Png);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [WARN] Magick 样张生成部分失败: " + ex.Message);
            }

            try
            {
                File.WriteAllText(Path.Combine(dir, "test.svg"),
                    "<svg xmlns='http://www.w3.org/2000/svg' width='500' height='400'><rect width='500' height='400' fill='#2b6cb0'/><circle cx='250' cy='200' r='120' fill='#f6ad55'/></svg>");
            }
            catch
            {
            }
        }

        private static void TryMagickWrite(ImageMagick.MagickImage im, string path, ImageMagick.MagickFormat format)
        {
            try
            {
                im.Format = format;
                im.Write(path);
            }
            catch
            {
                TryDelete(path);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        // ---------- COM 宿主测试（模拟 Explorer 的调用顺序） ----------

        private static int ComHostTest(string file, bool cleanup)
        {
            Console.WriteLine("=== Preview Handler COM 宿主测试 ===");

            string dll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ImagePeek.Preview.dll");
            if (!File.Exists(dll))
            {
                Console.WriteLine("  [FAIL] 找不到 " + dll);
                return 1;
            }

            if (file == null)
            {
                string dir = Path.Combine(Path.GetTempPath(), "ImagePeekTests");
                file = Path.Combine(dir, "test.jpg");
                if (!File.Exists(file))
                {
                    Directory.CreateDirectory(dir);
                    using (var bmp = new System.Drawing.Bitmap(200, 150))
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.Clear(System.Drawing.Color.SeaGreen);
                        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Jpeg);
                    }
                }
            }

            // 1) 注册（HKCU）
            string asmName = System.Reflection.AssemblyName.GetAssemblyName(dll).FullName;
            PreviewRegistration.RegisterHandler(dll, asmName);
            PreviewRegistration.RegisterExtensions(new[] { "png", "jpg" });
            Console.WriteLine("  已注册: " + dll);

            // 2) CoCreateInstance + 初始化 + 渲染
            var clsid = new Guid(PreviewRegistration.Clsid);
            Type t = Type.GetTypeFromCLSID(clsid, throwOnError: false);
            if (t == null)
            {
                Console.WriteLine("  [FAIL] GetTypeFromCLSID 失败");
                return 1;
            }

            object comObj = null;
            var form = new ClipForm
            {
                Text = "PreviewHostTest",
                Size = new System.Drawing.Size(600, 500),
                StartPosition = FormStartPosition.Manual,
                Location = new System.Drawing.Point(-20000, -20000),
                ShowInTaskbar = false
            };
            form.CreateControl();

            int result = 1;
            IPreviewHandler handler = null;
            try
            {
                comObj = Activator.CreateInstance(t);
                var init = (IInitializeWithFile)comObj;
                int hr = init.Initialize(file, 0);
                if (hr != 0)
                {
                    Console.WriteLine("  [FAIL] Initialize hr=0x" + hr.ToString("X8"));
                    goto Cleanup;
                }

                handler = (IPreviewHandler)comObj;
                var rect = new RECT { Left = 0, Top = 0, Right = 580, Bottom = 460 };
                hr = handler.SetWindow(form.Handle, ref rect);
                if (hr != 0)
                {
                    Console.WriteLine("  [FAIL] SetWindow hr=0x" + hr.ToString("X8"));
                    goto Cleanup;
                }

                hr = handler.DoPreview();
                if (hr != 0)
                {
                    Console.WriteLine("  [FAIL] DoPreview hr=0x" + hr.ToString("X8"));
                    goto Cleanup;
                }

                // 泵消息等解码完成
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 4000)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                }

                // 检查子窗口是否创建（渲染控件）
                IntPtr child = Native.GetWindow(form.Handle, Native.GW_CHILD);
                if (child == IntPtr.Zero)
                {
                    Console.WriteLine("  [FAIL] 预览子窗口未创建");
                    goto Cleanup;
                }

                Console.WriteLine("  [OK] 预览子窗口已创建并渲染 (hwnd=0x" + child.ToString("X") + ")");
                result = 0;

                Cleanup:
                try { handler?.Unload(); } catch { }
            }
            finally
            {
                if (comObj != null && Marshal.IsComObject(comObj))
                {
                    Marshal.ReleaseComObject(comObj);
                }
                form.Dispose();

                if (cleanup)
                {
                    PreviewRegistration.UnregisterExtensions(new[] { "png", "jpg" });
                    PreviewRegistration.UnregisterHandler();
                    Console.WriteLine("  已清理注册");
                }
            }

            return result;
        }

        private sealed class ClipForm : Form
        {
            protected override System.Windows.Forms.CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.Style |= 0x02000000; // WS_CLIPCHILDREN：防止父窗口白底盖住子窗口
                    return cp;
                }
            }
        }

        private sealed class Native
        {
            public const uint GW_CHILD = 5;

            [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [DllImport("user32.dll")]
            public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

            [DllImport("user32.dll")]
            public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

            [DllImport("user32.dll")]
            public static extern bool SetProcessDPIAware();

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hWnd, ref RECT rect);

            [DllImport("user32.dll")]
            public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        }
    }
}
