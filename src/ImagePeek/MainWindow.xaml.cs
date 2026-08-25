using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImagePeek.Core;
using Microsoft.Win32;

namespace ImagePeek
{
    public partial class MainWindow : Window
    {
        public sealed class FormatItem
        {
            public string Display { get; private set; }
            public string Ext { get; private set; }
            public bool IsChecked { get; set; }

            public FormatItem(string ext, string name, bool isChecked)
            {
                Ext = ext;
                Display = "." + ext + "  " + name;
                IsChecked = isChecked;
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            LoadFormats();
            RefreshStatus();
        }

        private void LoadFormats()
        {
            HashSet<string> registered = PreviewRegistration.GetRegisteredExtensions(SupportedFormats.AllExtensions());
            var items = new List<FormatItem>();
            foreach (var group in SupportedFormats.All.GroupBy(f => f.Group))
            {
                foreach (var f in group)
                {
                    bool isChecked = registered.Count > 0
                        ? registered.Contains(f.Ext)
                        : true;
                    items.Add(new FormatItem(f.Ext, f.Name, isChecked));
                }
            }
            FormatsList.ItemsSource = items;
        }

        private List<string> CheckedExtensions()
        {
            var items = FormatsList.ItemsSource as List<FormatItem>;
            if (items == null)
            {
                return new List<string>();
            }
            return items.Where(i => i.IsChecked).Select(i => i.Ext).ToList();
        }

        private void RefreshStatus()
        {
            string dll;
            bool registered = PreviewRegistration.IsHandlerRegistered(out dll);
            var exts = PreviewRegistration.GetRegisteredExtensions(SupportedFormats.AllExtensions());

            if (registered)
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                StatusText.Text = string.Format("已启用 · {0} 种格式接管中", exts.Count);
                RuntimePathText.Text = "解码组件：" + dll;
            }
            else
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA));
                StatusText.Text = "未启用";
                RuntimePathText.Text = "点击「一键启用预览」后，解码组件将释放到 %LocalAppData%\\ImagePeek";
            }

            FooterText.Text = "ImagePeek v1.0 · 预览处理器运行于系统沙箱 prevhost.exe · 缓存 " + FormatBytes(PayloadStore.CacheSize());
        }

        private bool EnsureElevatedFor(string arguments)
        {
            if (PreviewRegistration.IsElevated())
            {
                return true;
            }

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
                    return false;
                }
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false; // 用户取消 UAC
            }
            catch
            {
                return false;
            }
        }

        private void OnEnable(object sender, RoutedEventArgs e)
        {
            var exts = CheckedExtensions();
            if (exts.Count == 0)
            {
                MessageBox.Show(this, "请至少勾选一种格式。", "ImagePeek", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!PreviewRegistration.IsElevated())
            {
                if (!EnsureElevatedFor("--enable"))
                {
                    RefreshStatus();
                    return;
                }
                RefreshStatus();
                MessageBox.Show(this,
                    "启用成功！\n\n1. 打开资源管理器，按 Alt+P 显示预览窗格\n2. 点击任意图片即可在右侧立即预览\n\n如果预览窗格没变化，请在任务管理器中重启一次「Windows 资源管理器」。",
                    "ImagePeek", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                string dir = PayloadStore.EnsureRuntime();
                string dll = Path.Combine(dir, "ImagePeek.Preview.dll");
                string asmName = System.Reflection.AssemblyName.GetAssemblyName(dll).FullName;

                PreviewRegistration.RegisterHandler(dll, asmName);
                PreviewRegistration.RegisterExtensions(exts);
                PreviewRegistration.EnsurePreviewHandlersEnabled();
                RefreshStatus();

                MessageBox.Show(this,
                    "启用成功！\n\n" +
                    "1. 打开资源管理器，按 Alt+P 显示预览窗格\n" +
                    "2. 点击任意图片即可在右侧立即预览\n\n" +
                    "如果之前已打开预览窗格但没变化，请在任务管理器中重启一次「Windows 资源管理器」。",
                    "ImagePeek", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "启用失败：\n" + ex, "ImagePeek", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnDisable(object sender, RoutedEventArgs e)
        {
            if (!PreviewRegistration.IsElevated())
            {
                if (!EnsureElevatedFor("--disable"))
                {
                    return;
                }
                LoadFormats();
                RefreshStatus();
                return;
            }

            try
            {
                PreviewRegistration.UnregisterExtensions(SupportedFormats.AllExtensions());
                PreviewRegistration.UnregisterHandler();
                LoadFormats();
                RefreshStatus();
                MessageBox.Show(this, "已卸载全部注册，资源管理器恢复系统默认预览。\n（解码组件缓存已保留，可再次一键启用）",
                    "ImagePeek", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "卸载失败：\n" + ex, "ImagePeek", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnOpenViewer(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择一张图片",
                Filter = BuildImageFilter()
            };
            if (dlg.ShowDialog(this) == true)
            {
                new ViewerWindow(dlg.FileName).Show();
            }
        }

        private void OnOpenImage(object sender, RoutedEventArgs e)
        {
            OnOpenViewer(sender, e);
        }

        private static string BuildImageFilter()
        {
            string exts = string.Join(";*", SupportedFormats.AllExtensions().Select(x => "." + x));
            return "图片 (" + exts + ")|" + exts + "|所有文件|*.*";
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
