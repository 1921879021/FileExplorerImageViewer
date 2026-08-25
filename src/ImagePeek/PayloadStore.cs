using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;

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
