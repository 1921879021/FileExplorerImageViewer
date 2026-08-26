using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace ImagePeek
{
    /// <summary>
    /// 把 exe 内嵌的解码载荷（Preview DLL + 原生库）释放到
    /// %LocalAppData%\ImagePeek\runtime\&lt;version&gt;\，按版本目录隔离，
    /// 避免 prevhost 占用 DLL 导致更新失败。
    /// 注意：本类刻意不依赖 ImagePeek.Core，供 exe 的 AssemblyResolve 引导使用。
    /// </summary>
    public static class PayloadStore
    {
        private static string RootDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ImagePeek");
            }
        }

        private static string RuntimeRoot => Path.Combine(RootDir, "runtime");

        private static string CurrentVersionFile => Path.Combine(RootDir, "current.version");

        private static string _cachedDir;

        public static string EnsureRuntime()
        {
            if (_cachedDir != null && Directory.Exists(_cachedDir))
            {
                return _cachedDir;
            }

            Assembly asm = typeof(PayloadStore).Assembly;
            string version = ReadEmbeddedVersion(asm);

            string dir = Path.Combine(RuntimeRoot, version);
            string flag = Path.Combine(dir, "complete.flag");

            if (!File.Exists(flag))
            {
                Directory.CreateDirectory(dir);

                foreach (string res in asm.GetManifestResourceNames())
                {
                    if (!res.StartsWith("pp_", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string name = res.Substring(3);
                    string rel = name.StartsWith("native__", StringComparison.Ordinal)
                        ? Path.Combine("native", name.Substring(8))
                        : name;

                    string target = Path.Combine(dir, rel);
                    string targetDir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    using (Stream src = asm.GetManifestResourceStream(res))
                    using (var outFs = File.Create(target))
                    {
                        CopyPossiblyGzipped(src, outFs);
                    }
                }

                File.WriteAllText(flag, version);
            }

            Directory.CreateDirectory(RootDir);
            try
            {
                File.WriteAllText(CurrentVersionFile, version);
            }
            catch
            {
            }

            CleanupOldVersions(version);
            _cachedDir = dir;
            return dir;
        }

        /// <summary>
        /// 删除整个 %LocalAppData%\ImagePeek 目录。
        /// 多轮尝试：杀 prevhost / 其他 ImagePeek 实例 / Explorer（缩略图 DLL 占用者），
        /// 全部失败则注册"下次开机自动删除"。返回是否当场完全删除。
        /// </summary>
        public static bool RemoveAll()
        {
            int selfPid = System.Diagnostics.Process.GetCurrentProcess().Id;

            for (int attempt = 0; attempt < 4; attempt++)
            {
                KillProcess("prevhost", 0);
                KillProcess("ImagePeek", selfPid);

                try
                {
                    if (Directory.Exists(RootDir))
                    {
                        Directory.Delete(RootDir, true);
                    }
                }
                catch
                {
                }

                if (!Directory.Exists(RootDir))
                {
                    _cachedDir = null;
                    return true;
                }

                if (attempt >= 1)
                {
                    KillProcess("explorer", 0);
                }
                Thread.Sleep(700);
            }

            // 兜底 1：注册开机自动删除（需要管理员，调用方处于提权流程中）
            try
            {
                ScheduleDeleteOnReboot(RootDir);
            }
            catch
            {
            }

            // 兜底 2：当前进程自己可能锁着目录（AssemblyResolve 从 runtime 加载了 Core.dll），
            // 生成一个延迟 2 秒的独立清理进程，等本进程退出后删除
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    "/c ping -n 3 127.0.0.1 > nul & rd /s /q \"" + RootDir + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
            }

            _cachedDir = null;
            return !Directory.Exists(RootDir);
        }

        private static void KillProcess(string name, int exceptPid)
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    if (exceptPid > 0 && p.Id == exceptPid)
                    {
                        p.Dispose();
                        continue;
                    }
                    try { p.Kill(); } catch { }
                    p.Dispose();
                }
            }
            catch
            {
            }
        }

        private static void ScheduleDeleteOnReboot(string path)
        {
            // 路径需以 \??\ 前缀写入 PendingFileRenameOperations
            ScheduleDeleteOnRebootRecursive(path);
        }

        private static void ScheduleDeleteOnRebootRecursive(string dir)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    MoveFileEx(f, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                }
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    ScheduleDeleteOnRebootRecursive(d);
                }
                MoveFileEx(dir, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            }
            catch
            {
            }
        }

        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);

        public static bool ExplorerRunning()
        {
            try
            {
                return System.Diagnostics.Process.GetProcessesByName("explorer").Length > 0;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>终止 prevhost 代理进程（可能占用解码 DLL）。</summary>
        private static void KillPrevhost()
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("prevhost"))
                {
                    try { p.Kill(); } catch { }
                    p.Dispose();
                }
            }
            catch
            {
            }
        }

        public static bool RootExists()
        {
            return Directory.Exists(RootDir);
        }

        public static long CacheSize()
        {
            try
            {
                long total = 0;
                foreach (var f in new DirectoryInfo(RuntimeRoot).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    total += f.Length;
                }
                return total;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>载荷可能是 gzip（1F 8B 魔数）也可能是原始字节，自动识别。</summary>
        private static void CopyPossiblyGzipped(Stream src, Stream dst)
        {
            int b0 = src.ReadByte();
            int b1 = src.ReadByte();
            src.Position = 0;

            if (b0 == 0x1F && b1 == 0x8B)
            {
                using (var gz = new GZipStream(src, CompressionMode.Decompress, true))
                {
                    gz.CopyTo(dst);
                }
            }
            else
            {
                src.CopyTo(dst);
            }
        }

        private static string ReadEmbeddedVersion(Assembly asm)
        {
            using (Stream src = asm.GetManifestResourceStream("pp_version.txt"))
            using (var ms = new MemoryStream())
            {
                CopyPossiblyGzipped(src, ms);
                string text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                return text.Trim();
            }
        }

        private static void CleanupOldVersions(string current)
        {
            try
            {
                string root = RuntimeRoot;
                if (!Directory.Exists(root))
                {
                    return;
                }

                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    if (string.Equals(Path.GetFileName(dir), current, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch
                    {
                        // 被 prevhost 占用时跳过，下次再清
                    }
                }
            }
            catch
            {
            }
        }
    }
}
