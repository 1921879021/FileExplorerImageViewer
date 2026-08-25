using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ImagePeek.Core
{
    /// <summary>
    /// 把原生解码库（MagickNative.dll 等）预加载进当前进程。
    /// prevhost.exe / dllhost.exe 等宿主的应用目录不是我们的目录，
    /// 必须用完整路径 + LOAD_WITH_ALTERED_SEARCH_PATH 预加载，
    /// 之后托管层的 DllImport 才能按名字命中已加载的模块。
    /// </summary>
    public static class NativeLoader
    {
        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x8;

        private static int _state; // 0=未尝试 1=成功 -1=失败

        public static bool IsAvailable => Volatile.Read(ref _state) == 1;

        public static bool EnsureLoaded()
        {
            int s = Volatile.Read(ref _state);
            if (s != 0)
            {
                return s == 1;
            }

            lock (typeof(NativeLoader))
            {
                if (Volatile.Read(ref _state) != 0)
                {
                    return _state == 1;
                }

                try
                {
                    if (!Environment.Is64BitProcess)
                    {
                        Volatile.Write(ref _state, -1);
                        return false;
                    }

                    string nativeDir = LocateNativeDir();
                    if (nativeDir == null)
                    {
                        Volatile.Write(ref _state, -1);
                        return false;
                    }

                    foreach (string dll in Directory.EnumerateFiles(nativeDir, "*.dll"))
                    {
                        LoadLibraryEx(dll, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
                    }

                    Volatile.Write(ref _state, 1);
                    return true;
                }
                catch
                {
                    Volatile.Write(ref _state, -1);
                    return false;
                }
            }
        }

        private static string LocateNativeDir()
        {
            try
            {
                string coreDir = Path.GetDirectoryName(typeof(NativeLoader).Assembly.Location);
                if (string.IsNullOrEmpty(coreDir))
                {
                    return null;
                }

                string candidate = Path.Combine(coreDir, "native");
                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.dll").Any())
                {
                    return candidate;
                }

                if (Directory.EnumerateFiles(coreDir, "*.dll")
                    .Any(f => Path.GetFileName(f).StartsWith("Magick", StringComparison.OrdinalIgnoreCase)))
                {
                    return coreDir;
                }
            }
            catch
            {
            }
            return null;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);
    }
}
