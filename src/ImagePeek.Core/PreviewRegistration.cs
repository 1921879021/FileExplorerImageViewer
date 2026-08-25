using System;
using System.Collections.Generic;
using System.IO;

namespace ImagePeek.Core
{
    public static class ImagePeekPaths
    {
        public static string Root
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ImagePeek");
            }
        }

        public static string RuntimeRoot => Path.Combine(Root, "runtime");

        public static string CurrentVersionFile => Path.Combine(Root, "current.version");
    }

    /// <summary>
    /// 预览处理器的注册/反注册。
    /// .NET Framework 4 托管 COM 的版本化 InprocServer32 协议只认 HKLM，
    /// 因此启用/卸载需要一次性管理员确认（与官方 regasm /codebase 写法一致）。
    /// </summary>
    public static class PreviewRegistration
    {
        public const string Clsid = "A74E8F2C-6B3D-4F1A-9E5C-2D8B7A1F4E93";
        public const string ClsidBraced = "{A74E8F2C-6B3D-4F1A-9E5C-2D8B7A1F4E93}";
        public const string HandlerTitle = "ImagePeek 图片预览处理器";

        // 系统自带的中立 prevhost 代理宿主（OS 预定义 DllSurrogate），配合
        // CLSID 上的 DisableLowILProcessIsolation=1 以 Medium IL 运行
        //（CLR 无法可靠运行于 Low IL 进程，Excel 自家预览处理器同样退出沙箱）
        private const string AppIdOwn = "{6D2B5079-2F0B-48DD-AB7F-97CEC514D30B}";
        public const string PreviewShellexKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
        private const string ManagedComCategoryId = "{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}";

        private const string ClassesRoot = @"Software\Classes";
        private const string ClsidKey = ClassesRoot + @"\CLSID\" + ClsidBraced;
        private const string PreviewHandlersListKey = @"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers";
        private const string ExplorerAdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        private static Microsoft.Win32.RegistryKey ClassesBase => Microsoft.Win32.Registry.LocalMachine;

        public static bool IsElevated()
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }

        /// <summary>注册 COM 服务器本身（CLSID + prevhost 宿主），写法与官方 regasm /codebase 一致。</summary>
        public static void RegisterHandler(string dllPath, string assemblyFullName)
        {
            if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
            {
                throw new FileNotFoundException("找不到预览处理器 DLL", dllPath);
            }

            string version = new System.Reflection.AssemblyName(assemblyFullName).Version.ToString();

            using (var key = ClassesBase.CreateSubKey(ClsidKey))
            {
                key.SetValue("AppID", AppIdOwn, Microsoft.Win32.RegistryValueKind.String);
                // 退出 Low IL 沙箱（CLR/WinForms 需要 Medium IL）
                key.SetValue("DisableLowILProcessIsolation", 1, Microsoft.Win32.RegistryValueKind.DWord);

                using (var ips = key.CreateSubKey("InprocServer32"))
                {
                    // 默认值 = CLR 垫片：代理宿主 prevhost 加载它，由它再解析版本化子键中的托管程序集
                    // ThreadingModel 必须是 Apartment（微软文档标准值）：
                    // WinForms 渲染控件要求 STA 线程，"Both" 会被放进 MTA 导致永远画不出来
                    ips.SetValue(null, "mscoree.dll", Microsoft.Win32.RegistryValueKind.String);
                    ips.SetValue("ThreadingModel", "Apartment", Microsoft.Win32.RegistryValueKind.String);

                    using (var ver = ips.CreateSubKey(version))
                    {
                        ver.SetValue("Class", "ImagePeek.Preview.ImagePreviewHandler", Microsoft.Win32.RegistryValueKind.String);
                        ver.SetValue("Assembly", assemblyFullName, Microsoft.Win32.RegistryValueKind.String);
                        ver.SetValue("RuntimeVersion", "v4.0.30319", Microsoft.Win32.RegistryValueKind.String);
                        // 注意：必须用 ToString()（保留原始 Unicode 字符），
                        // AbsoluteUri 的百分号编码会导致 CLR 在 prevhost 中 LoadFrom 失败
                        ver.SetValue("CodeBase", new Uri(dllPath).ToString(), Microsoft.Win32.RegistryValueKind.String);
                    }
                }

                using (var progId = key.CreateSubKey("ProgId"))
                {
                    progId.SetValue(null, "ImagePeek.PreviewHandler", Microsoft.Win32.RegistryValueKind.String);
                }

                using (var cat = key.CreateSubKey(@"Implemented Categories\" + ManagedComCategoryId))
                {
                }
            }

            using (var list = ClassesBase.CreateSubKey(PreviewHandlersListKey))
            {
                list.SetValue(ClsidBraced, HandlerTitle, Microsoft.Win32.RegistryValueKind.String);
            }
        }

        /// <summary>把处理器挂到一组扩展名上。</summary>
        public static void RegisterExtensions(IEnumerable<string> extensions)
        {
            foreach (var ext in extensions)
            {
                if (string.IsNullOrWhiteSpace(ext))
                {
                    continue;
                }
                string path = ClassesRoot + @"\." + ext.TrimStart('.') + @"\shellex\" + PreviewShellexKey;
                using (var key = ClassesBase.CreateSubKey(path))
                {
                    key.SetValue(null, ClsidBraced, Microsoft.Win32.RegistryValueKind.String);
                }
            }
        }

        public static void UnregisterExtensions(IEnumerable<string> extensions)
        {
            foreach (var ext in extensions)
            {
                if (string.IsNullOrWhiteSpace(ext))
                {
                    continue;
                }

                string shellexPath = ClassesRoot + @"\." + ext.TrimStart('.') + @"\shellex";
                string handlerPath = shellexPath + @"\" + PreviewShellexKey;

                using (var key = ClassesBase.OpenSubKey(handlerPath, true))
                {
                    if (key != null)
                    {
                        var v = key.GetValue(null) as string;
                        if (string.Equals(v, ClsidBraced, StringComparison.OrdinalIgnoreCase))
                        {
                            // 只清理属于我们的注册，不动其他软件的
                            ClassesBase.DeleteSubKeyTree(handlerPath, false);
                        }
                    }
                }

                TryDeleteKeyIfEmpty(shellexPath);
                TryDeleteKeyIfEmpty(ClassesRoot + @"\." + ext.TrimStart('.'));
            }
        }

        /// <summary>反注册 COM 服务器。</summary>
        public static void UnregisterHandler()
        {
            ClassesBase.DeleteSubKeyTree(ClsidKey, false);
            using (var list = ClassesBase.OpenSubKey(PreviewHandlersListKey, true))
            {
                if (list != null && list.GetValue(ClsidBraced) != null)
                {
                    list.DeleteValue(ClsidBraced, false);
                }
            }
        }

        public static bool IsHandlerRegistered(out string dllPath)
        {
            dllPath = null;
            using (var ips = ClassesBase.OpenSubKey(ClsidKey + @"\InprocServer32"))
            {
                if (ips == null)
                {
                    return false;
                }
                // 版本化子键里取 CodeBase
                foreach (var sub in ips.GetSubKeyNames())
                {
                    using (var v = ips.OpenSubKey(sub))
                    {
                        var cb = v != null ? v.GetValue("CodeBase") as string : null;
                        if (!string.IsNullOrEmpty(cb))
                        {
                            dllPath = new Uri(cb).LocalPath;
                            return true;
                        }
                    }
                }
                dllPath = ips.GetValue(null) as string;
                return !string.IsNullOrEmpty(dllPath);
            }
        }

        public static HashSet<string> GetRegisteredExtensions(IEnumerable<string> candidates)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in candidates)
            {
                string path = ClassesRoot + @"\." + ext + @"\shellex\" + PreviewShellexKey;
                using (var key = ClassesBase.OpenSubKey(path))
                {
                    var v = key != null ? key.GetValue(null) as string : null;
                    if (string.Equals(v, ClsidBraced, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(ext);
                    }
                }
            }
            return result;
        }

        /// <summary>确保资源管理器“显示预览处理程序”处于打开状态（默认即开）。</summary>
        public static void EnsurePreviewHandlersEnabled()
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ExplorerAdvancedKey))
            {
                if (key.GetValue("ShowPreviewHandlers") == null)
                {
                    key.SetValue("ShowPreviewHandlers", 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
        }

        private static void TryDeleteKeyIfEmpty(string path)
        {
            try
            {
                using (var key = ClassesBase.OpenSubKey(path))
                {
                    if (key == null)
                    {
                        return;
                    }
                    if (key.SubKeyCount == 0 && key.ValueCount == 0)
                    {
                        ClassesBase.DeleteSubKey(path, false);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
