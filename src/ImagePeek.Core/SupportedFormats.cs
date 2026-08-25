using System;
using System.Collections.Generic;
using System.IO;

namespace ImagePeek.Core
{
    public enum FormatGroup
    {
        Common,
        Modern,
        Pro
    }

    public sealed class FormatInfo
    {
        public string Ext { get; }
        public string Name { get; }
        public FormatGroup Group { get; }

        public FormatInfo(string ext, string name, FormatGroup group)
        {
            Ext = ext;
            Name = name;
            Group = group;
        }
    }

    public static class SupportedFormats
    {
        public static readonly FormatInfo[] All =
        {
            new FormatInfo("jpg",  "JPEG", FormatGroup.Common),
            new FormatInfo("jpeg", "JPEG", FormatGroup.Common),
            new FormatInfo("jfif", "JFIF", FormatGroup.Common),
            new FormatInfo("png",  "PNG", FormatGroup.Common),
            new FormatInfo("gif",  "GIF", FormatGroup.Common),
            new FormatInfo("bmp",  "BMP", FormatGroup.Common),
            new FormatInfo("dib",  "DIB", FormatGroup.Common),
            new FormatInfo("tif",  "TIFF", FormatGroup.Common),
            new FormatInfo("tiff", "TIFF", FormatGroup.Common),
            new FormatInfo("ico",  "图标", FormatGroup.Common),

            new FormatInfo("webp", "WebP", FormatGroup.Modern),
            new FormatInfo("avif", "AVIF", FormatGroup.Modern),
            new FormatInfo("heic", "HEIC (iPhone)", FormatGroup.Modern),
            new FormatInfo("heif", "HEIF", FormatGroup.Modern),
            new FormatInfo("hif",  "HIF", FormatGroup.Modern),
            new FormatInfo("jxl",  "JPEG XL", FormatGroup.Modern),
            new FormatInfo("jp2",  "JPEG 2000", FormatGroup.Modern),
            new FormatInfo("j2k",  "JPEG 2000", FormatGroup.Modern),
            new FormatInfo("svg",  "SVG 矢量", FormatGroup.Modern),
            new FormatInfo("svgz", "SVGZ 矢量", FormatGroup.Modern),

            new FormatInfo("psd",  "Photoshop", FormatGroup.Pro),
            new FormatInfo("exr",  "OpenEXR", FormatGroup.Pro),
            new FormatInfo("hdr",  "Radiance HDR", FormatGroup.Pro),
            new FormatInfo("tga",  "TGA", FormatGroup.Pro),
            new FormatInfo("pbm",  "PBM", FormatGroup.Pro),
            new FormatInfo("pgm",  "PGM", FormatGroup.Pro),
            new FormatInfo("ppm",  "PPM", FormatGroup.Pro),
            new FormatInfo("pnm",  "PNM", FormatGroup.Pro),
            new FormatInfo("pfm",  "PFM", FormatGroup.Pro),
        };

        private static readonly HashSet<string> VipsOnlySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "webp", "avif", "heic", "heif", "hif", "jxl", "jp2", "j2k",
            "svg", "svgz", "psd", "exr", "hdr", "tga", "pbm", "pgm", "ppm", "pnm", "pfm"
        };

        private static readonly HashSet<string> AllSet = BuildAllSet();

        private static HashSet<string> BuildAllSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in All)
            {
                set.Add(f.Ext);
            }
            return set;
        }

        public static string GetExtension(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext))
            {
                return string.Empty;
            }
            return ext.Substring(1);
        }

        public static bool IsSupported(string path)
        {
            return AllSet.Contains(GetExtension(path));
        }

        public static bool IsVipsOnly(string path)
        {
            return VipsOnlySet.Contains(GetExtension(path));
        }

        public static IEnumerable<string> AllExtensions()
        {
            foreach (var f in All)
            {
                yield return f.Ext;
            }
        }
    }
}
