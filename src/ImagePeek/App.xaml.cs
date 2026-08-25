using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using ImagePeek.Core;

namespace ImagePeek
{
    public partial class App : Application
    {
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

                string candidate = first;
                if (File.Exists(candidate) && SupportedFormats.IsSupported(candidate))
                {
                    new ViewerWindow(candidate).Show();
                    return;
                }
            }

            new MainWindow().Show();
        }

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
                Console.WriteLine("ImagePeek: 已卸载全部预览注册。");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ImagePeek 卸载失败: " + ex.Message);
                return 1;
            }
        }

        private static int RunElevated(string arguments)
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
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
            return 0;
        }
    }
}
