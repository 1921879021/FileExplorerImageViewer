using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using ImagePeek.Core;

namespace ImagePeek
{
    public partial class App : Application
    {
        private static Mutex _mutex;
        internal static bool ExitRequested;
        private static System.Windows.Forms.NotifyIcon _tray;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        // 单文件便携引导：exe 被单独复制到任意位置时，
        // 托管依赖（ImagePeek.Core / Magick.NET 等）从内嵌载荷释放目录解析
        static App()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try
                {
                    string name = new AssemblyName(e.Name).Name + ".dll";
                    string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    if (!string.IsNullOrEmpty(exeDir))
                    {
                        string p = Path.Combine(exeDir, name);
                        if (File.Exists(p))
                        {
                            return Assembly.LoadFrom(p);
                        }
                    }

                    string dir = PayloadStore.EnsureRuntime();
                    string q = Path.Combine(dir, name);
                    if (File.Exists(q))
                    {
                        return Assembly.LoadFrom(q);
                    }
                    string native = Path.Combine(dir, "native", name);
                    if (File.Exists(native))
                    {
                        return Assembly.LoadFrom(native);
                    }
                }
                catch
                {
                }
                return null;
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string[] args = e.Args ?? new string[0];
            if (args.Length > 0)
            {
                string first = args[0];
                if (string.Equals(first, "--enable", StringComparison.OrdinalIgnoreCase))
                {
                    Environment.ExitCode = CliEnable();
                    Shutdown(0);
                    return;
                }
                if (string.Equals(first, "--disable", StringComparison.OrdinalIgnoreCase))
                {
                    Environment.ExitCode = CliDisable();
                    Shutdown(0);
                    return;
                }
                if (string.Equals(first, "--status", StringComparison.OrdinalIgnoreCase))
                {
                    Environment.ExitCode = CliStatus();
                    Shutdown(0);
                    return;
                }
                if (string.Equals(first, "--thumbs-on", StringComparison.OrdinalIgnoreCase))
                {
                    Environment.ExitCode = CliThumbs(true);
                    Shutdown(0);
                    return;
                }
                if (string.Equals(first, "--thumbs-off", StringComparison.OrdinalIgnoreCase))
                {
                    Environment.ExitCode = CliThumbs(false);
                    Shutdown(0);
                    return;
                }

                // 单实例：普通 GUI 模式才检查
                _mutex = new Mutex(true, "ImagePeek_SingleInstance", out bool createdNew);
                if (!createdNew)
                {
                    MessageBox.Show("ImagePeek 已在运行（请查看任务栏托盘图标）。", "ImagePeek",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown(0);
                    return;
                }

                CreateTrayIcon();

                string candidate = first;
                if (File.Exists(candidate) && SupportedFormats.IsSupported(candidate))
                {
                    new ViewerWindow(candidate).Show();
                    return;
                }

                if (string.Equals(first, "--minimized", StringComparison.OrdinalIgnoreCase))
                {
                    _tray.ShowBalloonTip(2500, "ImagePeek 已启动",
                        "正在后台运行，双击托盘图标可打开设置。", System.Windows.Forms.ToolTipIcon.Info);
                    return;
                }
            }
            else
            {
                _mutex = new Mutex(true, "ImagePeek_SingleInstance", out bool createdNew);
                if (!createdNew)
                {
                    MessageBox.Show("ImagePeek 已在运行（请查看任务栏托盘图标）。", "ImagePeek",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown(0);
                    return;
                }
                CreateTrayIcon();
            }

            new MainWindow().Show();
        }

        // ---------- 托盘 ----------

        private void CreateTrayIcon()
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "ImagePeek — 资源管理器图片即时预览",
                Visible = true
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("打开设置", null, (s, ev) => ShowMain());
            menu.Items.Add("打开快速查看器…", null, (s, ev) => OpenViewerFromDialog());

            var auto = new System.Windows.Forms.ToolStripMenuItem("开机自启动")
            {
                CheckOnClick = true,
                Checked = AutoStartManager.IsEnabled()
            };
            auto.CheckedChanged += (s, ev) =>
            {
                try
                {
                    AutoStartManager.Set(auto.Checked);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("设置开机自启动失败：\n" + ex.Message, "ImagePeek",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    auto.Checked = AutoStartManager.IsEnabled();
                }
            };
            menu.Items.Add(auto);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (s, ev) => ExitApp());

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, ev) => ShowMain();
        }

        private static System.Drawing.Icon LoadAppIcon()
        {
            using (var s = Application.GetResourceStream(new Uri("pack://application:,,,/ImagePeek.ico")).Stream)
            {
                return new System.Drawing.Icon(s);
            }
        }

        internal static void ShowMain()
        {
            var w = Current.Windows.OfType<MainWindow>().FirstOrDefault();
            if (w == null)
            {
                w = new MainWindow();
                w.Show();
            }
            else
            {
                w.Show();
                w.WindowState = WindowState.Normal;
                w.Activate();
            }
        }

        internal static void OpenViewerFromDialog()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择一张图片",
                Filter = "图片文件|*.jpg;*.jpeg;*.jfif;*.png;*.gif;*.bmp;*.dib;*.tif;*.tiff;*.ico;*.webp;*.avif;*.heic;*.heif;*.hif;*.jxl;*.jp2;*.j2k;*.svg;*.svgz;*.psd;*.exr;*.hdr;*.tga;*.pbm;*.pgm;*.ppm;*.pnm;*.pfm|所有文件|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                new ViewerWindow(dlg.FileName).Show();
            }
        }

        internal static void ExitApp()
        {
            ExitRequested = true;
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            Current.Shutdown();
        }

        // ---------- 命令行 ----------

        private static void AttachParentConsole()
        {
            try
            {
                AttachConsole(-1);
                var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(writer);
            }
            catch
            {
            }
        }

        private static int CliEnable()
        {
            AttachParentConsole();
            if (!PreviewRegistration.IsElevated())
            {
                return RunElevated("--enable");
            }
            try
            {
                string dir = PayloadStore.EnsureRuntime();
                string dll = Path.Combine(dir, "ImagePeek.Preview.dll");
                string asmName = System.Reflection.AssemblyName.GetAssemblyName(dll).FullName;
                PreviewRegistration.RegisterHandler(dll, asmName);
                PreviewRegistration.RegisterExtensions(SupportedFormats.AllExtensions());
                PreviewRegistration.EnsurePreviewHandlersEnabled();
                Console.WriteLine("ImagePeek: 已启用 " + new System.Collections.Generic.HashSet<string>(SupportedFormats.AllExtensions()).Count + " 种图片格式的资源管理器预览。");
                Console.WriteLine("在资源管理器按 Alt+P 打开预览窗格即可点击查看。");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ImagePeek 启用失败: " + ex.Message);
                return 1;
            }
        }

        private static int CliDisable()
        {
            AttachParentConsole();
            if (!PreviewRegistration.IsElevated())
            {
                return RunElevated("--disable");
            }
            try
            {
                PreviewRegistration.UnregisterExtensions(SupportedFormats.AllExtensions());
                PreviewRegistration.UnregisterHandler();
                PreviewRegistration.UnregisterThumbnailExtensions(SupportedFormats.AllExtensions());
                PreviewRegistration.UnregisterThumbnailHandler();
                Console.WriteLine("ImagePeek: 已卸载全部预览注册。");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ImagePeek 卸载失败: " + ex.Message);
                return 1;
            }
        }

        private static int CliThumbs(bool enable)
        {
            AttachParentConsole();
            if (!PreviewRegistration.IsElevated())
            {
                return RunElevated(enable ? "--thumbs-on" : "--thumbs-off");
            }
            try
            {
                if (enable)
                {
                    string dir = PayloadStore.EnsureRuntime();
                    string dll = Path.Combine(dir, "ImagePeek.Preview.dll");
                    string asmName = System.Reflection.AssemblyName.GetAssemblyName(dll).FullName;
                    PreviewRegistration.RegisterThumbnailHandler(dll, asmName);
                    PreviewRegistration.RegisterThumbnailExtensions(SupportedFormats.AllExtensions());
                    Console.WriteLine("ImagePeek: 已启用文件夹缩略图（重启资源管理器后生效）。");
                }
                else
                {
                    PreviewRegistration.UnregisterThumbnailExtensions(SupportedFormats.AllExtensions());
                    PreviewRegistration.UnregisterThumbnailHandler();
                    Console.WriteLine("ImagePeek: 已卸载文件夹缩略图。");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ImagePeek 缩略图设置失败: " + ex.Message);
                return 1;
            }
        }

        private static int RunElevated(string arguments)
        {
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                var psi = new System.Diagnostics.ProcessStartInfo(exe)
                {
                    Arguments = arguments,
                    Verb = "runas",
                    UseShellExecute = true
                };
                var p = System.Diagnostics.Process.Start(psi);
                if (p == null)
                {
                    return 1;
                }
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.WriteLine("ImagePeek: 用户取消了管理员授权。");
                return 2;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ImagePeek 提权失败: " + ex.Message);
                return 1;
            }
        }

        private static int CliStatus()
        {
            AttachParentConsole();
            string dll;
            bool registered = PreviewRegistration.IsHandlerRegistered(out dll);
            Console.WriteLine("Handler registered: " + (registered ? "YES (" + dll + ")" : "NO"));
            if (registered && File.Exists(dll))
            {
                try
                {
                    Console.WriteLine("Assembly: " + System.Reflection.AssemblyName.GetAssemblyName(dll).FullName);
                }
                catch
                {
                }
            }
            var exts = PreviewRegistration.GetRegisteredExtensions(SupportedFormats.AllExtensions());
            Console.WriteLine("Registered extensions (" + exts.Count + "): " + string.Join(" ", exts));
            Console.WriteLine("Autostart: " + (AutoStartManager.IsEnabled() ? "ON" : "OFF"));
            return 0;
        }
    }
}
