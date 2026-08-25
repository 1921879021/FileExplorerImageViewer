using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace ImagePeek.Core
{
    public sealed class DecodeResult
    {
        public Bitmap Bitmap { get; internal set; }
        public bool HasAlpha { get; internal set; }
        public string Decoder { get; internal set; }
        public double Ms { get; internal set; }
        public long FileSize { get; internal set; }

        public int Width => Bitmap != null ? Bitmap.Width : 0;
        public int Height => Bitmap != null ? Bitmap.Height : 0;
    }

    public sealed class AnimatedDecodeResult
    {
        public System.Collections.Generic.List<Bitmap> Frames { get; } = new System.Collections.Generic.List<Bitmap>();
        public System.Collections.Generic.List<int> DelaysMs { get; } = new System.Collections.Generic.List<int>();
        public bool HasAlpha { get; internal set; }
        public string Decoder { get; internal set; }
        public double Ms { get; internal set; }
        public long FileSize { get; internal set; }
    }

    /// <summary>
    /// 两级解码核心：
    ///   L1 GDI+   —— jpg/png/gif/bmp/tiff/ico 等常规格式，速度最快
    ///   L2 Magick —— webp/avif/heic/jxl/svg/psd/exr/tga/dds 等 100+ 格式兜底
    /// 带进程内 LRU 缓存：同一文件在预览窗格里反复点击时零解码开销。
    /// </summary>
    public static class DecodeCore
    {
        public const int DefaultMaxPixels = 2048;
        private const int CacheCapacity = 6;

        private static readonly object Gate = new object();
        private static readonly LinkedList<KeyValuePair<string, DecodeResult>> CacheOrder =
            new LinkedList<KeyValuePair<string, DecodeResult>>();
        private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, DecodeResult>>> CacheIndex =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, DecodeResult>>>(StringComparer.OrdinalIgnoreCase);

        public static DecodeResult Decode(string path, int maxPixels = DefaultMaxPixels, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("文件不存在", path);
            }

            ct.ThrowIfCancellationRequested();

            string key = BuildKey(path, maxPixels);
            DecodeResult cached = CacheGet(key);
            if (cached != null)
            {
                return cached;
            }

            ct.ThrowIfCancellationRequested();

            long size = 0;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch
            {
            }

            var sw = Stopwatch.StartNew();
            DecodeResult result = DecodeSlow(path, maxPixels, ct);
            sw.Stop();

            result.Ms = sw.Elapsed.TotalMilliseconds;
            result.FileSize = size;

            CachePut(key, result);
            return result;
        }

        private static DecodeResult DecodeSlow(string path, int maxPixels, CancellationToken ct)
        {
            if (SupportedFormats.IsVipsOnly(path))
            {
                return DecodeWithMagick(path, maxPixels);
            }

            Exception gdiError;
            try
            {
                return DecodeWithGdiPlus(path, maxPixels);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                gdiError = ex;
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                return DecodeWithMagick(path, maxPixels);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception magickError)
            {
                throw new InvalidOperationException(
                    "GDI+: " + gdiError.Message + " | Magick: " + magickError.Message, magickError);
            }
        }

        // ---------- 动图（GIF / WebP 动画，Magick 逐帧） ----------

        private static readonly object AnimGate = new object();
        private static readonly LinkedList<KeyValuePair<string, AnimatedDecodeResult>> AnimOrder =
            new LinkedList<KeyValuePair<string, AnimatedDecodeResult>>();
        private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, AnimatedDecodeResult>>> AnimIndex =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, AnimatedDecodeResult>>>(StringComparer.OrdinalIgnoreCase);
        private const int AnimCacheCapacity = 2;

        /// <summary>
        /// 解码动图的全部帧。Coalesce 合成增量帧后，用原始像素直拷转 GDI+ 位图
        /// （避免逐帧 PNG 编码/解码往返），帧数上限 48、尺寸上限 1024。
        /// 结果带小容量缓存（2 条），重复点击零解码。
        /// </summary>
        public static AnimatedDecodeResult DecodeAnimated(string path, int maxPixels, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("文件不存在", path);
            }
            if (!NativeLoader.EnsureLoaded())
            {
                throw new NotSupportedException("Magick 原生组件不可用");
            }

            if (maxPixels <= 0 || maxPixels > 1024)
            {
                maxPixels = 1024;
            }

            ct.ThrowIfCancellationRequested();

            string key = BuildKey(path, maxPixels) + "|anim";
            lock (AnimGate)
            {
                LinkedListNode<KeyValuePair<string, AnimatedDecodeResult>> node;
                if (AnimIndex.TryGetValue(key, out node))
                {
                    AnimOrder.Remove(node);
                    AnimOrder.AddFirst(node);
                    return node.Value.Value;
                }
            }

            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }

            var sw = Stopwatch.StartNew();
            var result = new AnimatedDecodeResult { Decoder = "Magick(anim)" };

            using (var coll = new ImageMagick.MagickImageCollection(path))
            {
                if (coll.Count <= 1)
                {
                    throw new InvalidOperationException("不是动图（只有一帧）");
                }

                coll.Coalesce();   // 合成增量帧，保证每帧完整
                int count = Math.Min(coll.Count, 48);

                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var frame = coll[i];
                    if (frame.Width > maxPixels || frame.Height > maxPixels)
                    {
                        frame.Resize((uint)maxPixels, (uint)maxPixels);
                    }
                    frame.Alpha(ImageMagick.AlphaOption.Set);   // 统一 4 通道，便于直拷

                    int w = (int)frame.Width, h = (int)frame.Height;
                    byte[] rgba = frame.GetPixels().GetArea(0, 0, (uint)w, (uint)h);

                    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    unsafe
                    {
                        fixed (byte* sp = rgba)
                        {
                            byte* s = sp;
                            byte* d = (byte*)bd.Scan0;
                            for (int y = 0; y < h; y++)
                            {
                                for (int x = 0; x < w; x++)
                                {
                                    // Magick RGBA → GDI+ BGRA
                                    d[0] = s[2];
                                    d[1] = s[1];
                                    d[2] = s[0];
                                    d[3] = s[3];
                                    s += 4;
                                    d += 4;
                                }
                            }
                        }
                    }
                    bmp.UnlockBits(bd);
                    result.Frames.Add(bmp);

                    int delay = (int)frame.AnimationDelay * 10;   // ticks(1/100s) → ms
                    if (delay < 20)
                    {
                        delay = 100;   // 防止 0 延迟闪屏
                    }
                    result.DelaysMs.Add(delay);
                }

                result.HasAlpha = result.Frames.Count > 0;
            }

            sw.Stop();
            result.Ms = sw.Elapsed.TotalMilliseconds;
            result.FileSize = size;

            // 内存保护：超过 64MB 的动画不缓存
            long est = 0;
            foreach (var f in result.Frames)
            {
                est += (long)f.Width * f.Height * 4;
            }
            if (est <= 64L * 1024 * 1024)
            {
                lock (AnimGate)
                {
                    if (!AnimIndex.ContainsKey(key))
                    {
                        var node = new LinkedListNode<KeyValuePair<string, AnimatedDecodeResult>>(
                            new KeyValuePair<string, AnimatedDecodeResult>(key, result));
                        AnimIndex[key] = node;
                        AnimOrder.AddFirst(node);
                        while (AnimOrder.Count > AnimCacheCapacity)
                        {
                            var last = AnimOrder.Last;
                            if (last == null)
                            {
                                break;
                            }
                            AnimOrder.RemoveLast();
                            AnimIndex.Remove(last.Value.Key);
                            foreach (var f in last.Value.Value.Frames)
                            {
                                try { f.Dispose(); } catch { }
                            }
                        }
                    }
                }
            }

            return result;
        }

        // ---------- L1 GDI+ ----------

        private static DecodeResult DecodeWithGdiPlus(string path, int maxPixels)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var loaded = new Bitmap(fs))
            {
                Bitmap current = ExifHelper.ApplyOrientation(loaded);
                Bitmap scaled = DownscaleIfNeeded(current, maxPixels);
                Bitmap extra = null;
                try
                {
                    // Bitmap(Stream) 要求流在位图生命周期内保持打开，深拷贝解耦
                    var final = new Bitmap(scaled);
                    extra = final;
                    return new DecodeResult
                    {
                        Bitmap = final,
                        HasAlpha = HasEffectiveAlpha(final),
                        Decoder = "GDI+"
                    };
                }
                finally
                {
                    if (!ReferenceEquals(scaled, loaded))
                    {
                        scaled.Dispose();
                    }
                    if (!ReferenceEquals(current, loaded) && !ReferenceEquals(current, scaled))
                    {
                        current.Dispose();
                    }
                    // extra 与 final 是同一个对象，不在这里释放
                    _ = extra;
                }
            }
        }

        private static Bitmap DownscaleIfNeeded(Bitmap src, int maxPixels)
        {
            if (maxPixels <= 0)
            {
                maxPixels = DefaultMaxPixels;
            }

            int maxDim = Math.Max(src.Width, src.Height);
            if (maxDim <= maxPixels)
            {
                return src;
            }

            double k = (double)maxPixels / maxDim;
            int w = Math.Max(1, (int)Math.Round(src.Width * k));
            int h = Math.Max(1, (int)Math.Round(src.Height * k));

            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                using (var g = Graphics.FromImage(dst))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighSpeed;
                    g.DrawImage(src, new Rectangle(0, 0, w, h),
                        new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
                }
                return dst;
            }
            catch
            {
                dst.Dispose();
                throw;
            }
        }

        private static bool HasEffectiveAlpha(Bitmap b)
        {
            switch (b.PixelFormat)
            {
                case PixelFormat.Format32bppArgb:
                case PixelFormat.Format32bppPArgb:
                case PixelFormat.Format64bppArgb:
                case PixelFormat.Format64bppPArgb:
                case PixelFormat.Format16bppArgb1555:
                    return true;
                default:
                    return false;
            }
        }

        // ---------- L2 Magick ----------

        private static DecodeResult DecodeWithMagick(string path, int maxPixels)
        {
            if (!NativeLoader.EnsureLoaded())
            {
                throw new NotSupportedException("Magick 原生解码组件不可用（ImagePeek 解码组件仅支持 64 位进程）");
            }

            if (maxPixels <= 0)
            {
                maxPixels = DefaultMaxPixels;
            }

            using (var mi = new ImageMagick.MagickImage())
            {
                mi.Read(path);
                mi.AutoOrient();
                mi.ColorSpace = ImageMagick.ColorSpace.sRGB;

                if (mi.Width > maxPixels || mi.Height > maxPixels)
                {
                    // 等比缩小到 maxPixels 以内（只缩不放）
                    mi.Resize((uint)maxPixels, (uint)maxPixels);
                }

                bool alpha = mi.HasAlpha;
                mi.Format = ImageMagick.MagickFormat.Png;
                byte[] png = mi.ToByteArray();

                var bmp = new Bitmap(new MemoryStream(png));
                return new DecodeResult
                {
                    Bitmap = bmp,
                    HasAlpha = alpha,
                    Decoder = "Magick"
                };
            }
        }

        // ---------- 缓存 ----------

        private static string BuildKey(string path, int maxPixels)
        {
            long len = 0;
            DateTime mtime = DateTime.MinValue;
            try
            {
                var fi = new FileInfo(path);
                len = fi.Length;
                mtime = fi.LastWriteTimeUtc;
            }
            catch
            {
            }

            return path.ToUpperInvariant() + "|" + len + "|" + mtime.Ticks + "|" + maxPixels;
        }

        private static DecodeResult CacheGet(string key)
        {
            lock (Gate)
            {
                LinkedListNode<KeyValuePair<string, DecodeResult>> node;
                if (CacheIndex.TryGetValue(key, out node))
                {
                    CacheOrder.Remove(node);
                    CacheOrder.AddFirst(node);
                    return node.Value.Value;
                }
                return null;
            }
        }

        private static void CachePut(string key, DecodeResult value)
        {
            lock (Gate)
            {
                if (CacheIndex.ContainsKey(key))
                {
                    return;
                }

                var node = new LinkedListNode<KeyValuePair<string, DecodeResult>>(
                    new KeyValuePair<string, DecodeResult>(key, value));
                CacheIndex[key] = node;
                CacheOrder.AddFirst(node);

                while (CacheOrder.Count > CacheCapacity)
                {
                    var last = CacheOrder.Last;
                    if (last == null)
                    {
                        break;
                    }
                    CacheOrder.RemoveLast();
                    CacheIndex.Remove(last.Value.Key);
                    try
                    {
                        last.Value.Value.Bitmap?.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }
    }

    internal static class ExifHelper
    {
        private const int PropertyTagOrientation = 0x0112;

        public static Bitmap ApplyOrientation(Bitmap b)
        {
            try
            {
                if (Array.IndexOf(b.PropertyIdList, PropertyTagOrientation) < 0)
                {
                    return b;
                }

                int o = b.GetPropertyItem(PropertyTagOrientation).Value[0];
                switch (o)
                {
                    case 2: b.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                    case 3: b.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                    case 4: b.RotateFlip(RotateFlipType.Rotate180FlipX); break;
                    case 5: b.RotateFlip(RotateFlipType.Rotate90FlipX); break;
                    case 6: b.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                    case 7: b.RotateFlip(RotateFlipType.Rotate90FlipY); break;
                    case 8: b.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                }
            }
            catch
            {
                // 无 EXIF 或损坏的 EXIF 不影响预览
            }
            return b;
        }
    }
}
