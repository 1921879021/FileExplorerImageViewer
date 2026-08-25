using System;
using System.Runtime.InteropServices;

namespace ImagePeek.Preview
{
    // IID 定义与 Windows SDK / Stephen Toub《Writing Your Own Preview Handlers》(MSDN Magazine 2007) 一致

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOGFONTW
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    [ComImport]
    [Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPreviewHandler
    {
        [PreserveSig]
        int SetWindow(IntPtr hwnd, ref RECT rect);

        [PreserveSig]
        int SetRect(ref RECT rect);

        [PreserveSig]
        int DoPreview();

        [PreserveSig]
        int Unload();

        [PreserveSig]
        int SetFocus();

        [PreserveSig]
        int QueryFocus(out IntPtr phwnd);

        [PreserveSig]
        int TranslateAccelerator(ref MSG pmsg);
    }

    [ComImport]
    [Guid("8327b13c-b63f-4b24-9b8a-d010dcc3f599")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPreviewHandlerVisuals
    {
        [PreserveSig]
        int SetBackgroundColor(int color); // COLORREF

        [PreserveSig]
        int SetFont(ref LOGFONTW plf);

        [PreserveSig]
        int SetTextColor(int color); // COLORREF
    }

    [ComImport]
    [Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithFile
    {
        [PreserveSig]
        int Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport]
    [Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IObjectWithSite
    {
        [PreserveSig]
        int SetSite([MarshalAs(UnmanagedType.IUnknown)] object pUnkSite);

        [PreserveSig]
        int GetSite(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvSite);
    }

    [ComImport]
    [Guid("00000114-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleWindow
    {
        [PreserveSig]
        int GetWindow(out IntPtr phwnd);

        [PreserveSig]
        int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
    }
}
