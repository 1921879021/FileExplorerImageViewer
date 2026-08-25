using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ImagePeek.Core;

namespace ImagePeek.Preview
{
    /// <summary>
    /// ImagePeek 预览处理器：注册进资源管理器预览窗格，
    /// 点击受支持的图片文件时在右侧立即渲染预览。
    /// 渲染控件运行在专用 STA 线程（宿主线程可能是 MTA，WinForms 必须 STA）。
    /// </summary>
    [ComVisible(true)]
    [Guid(PreviewRegistration.Clsid)]
    [ClassInterface(ClassInterfaceType.None)]
    [ProgId("ImagePeek.PreviewHandler")]
    public sealed class ImagePreviewHandler :
        IInitializeWithFile,
        IObjectWithSite,
        IOleWindow,
        IPreviewHandler,
        IPreviewHandlerVisuals
    {
        private const int S_OK = 0;
        private const int S_FALSE = 1;
        private const int E_FAIL = unchecked((int)0x80004005);
        private const int E_POINTER = unchecked((int)0x80004003);

        private string _filePath;
        private object _site;
        private PreviewControl _control;          // 由 STA 线程创建/拥有
        private Thread _uiThread;
        private RECT _rect;
        private IntPtr _parentHwnd;
        private CancellationTokenSource _cts;

        static ImagePreviewHandler()
        {
            // prevhost 的应用目录不是我们的目录，托管依赖需要手动探测加载
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try
                {
                    if (e.RequestingAssembly == null)
                    {
                        return null;
                    }

                    string dir = Path.GetDirectoryName(e.RequestingAssembly.Location);
                    if (string.IsNullOrEmpty(dir))
                    {
                        return null;
                    }

                    string name = new System.Reflection.AssemblyName(e.Name).Name + ".dll";
                    string candidate = Path.Combine(dir, name);
                    return File.Exists(candidate) ? System.Reflection.Assembly.LoadFrom(candidate) : null;
                }
                catch
                {
                    return null;
                }
            };
        }

        // ---------- IInitializeWithFile ----------

        int IInitializeWithFile.Initialize(string pszFilePath, uint grfMode)
        {
            try
            {
                // 按规范：这里只存路径，读取推迟到 DoPreview
                _filePath = pszFilePath;
                Log("Initialize: " + pszFilePath);
                return S_OK;
            }
            catch (Exception ex)
            {
                Log("Initialize EX: " + ex.Message);
                return ex.HResult;
            }
        }

        // ---------- IObjectWithSite ----------

        int IObjectWithSite.SetSite(object pUnkSite)
        {
            _site = pUnkSite;
            return S_OK;
        }

        int IObjectWithSite.GetSite(ref Guid riid, out object ppvSite)
        {
            ppvSite = _site;
            return _site == null ? E_POINTER : S_OK;
        }

        // ---------- IOleWindow ----------

        int IOleWindow.GetWindow(out IntPtr phwnd)
        {
            var c = _control;
            if (c != null && c.IsHandleCreated)
            {
                phwnd = c.Handle;
                return S_OK;
            }
            phwnd = IntPtr.Zero;
            return E_FAIL;
        }

        int IOleWindow.ContextSensitiveHelp(bool fEnterMode)
        {
            return S_OK;
        }

        // ---------- IPreviewHandler ----------

        int IPreviewHandler.SetWindow(IntPtr hwnd, ref RECT rect)
        {
            try
            {
                _parentHwnd = hwnd;
                _rect = rect;
                RunOnUi(() => AttachAndLayout());
                return S_OK;
            }
            catch (Exception ex)
            {
                return ex.HResult;
            }
        }

        int IPreviewHandler.SetRect(ref RECT rect)
        {
            try
            {
                _rect = rect;
                RunOnUi(() => AttachAndLayout());
                return S_OK;
            }
            catch (Exception ex)
            {
                return ex.HResult;
            }
        }

        int IPreviewHandler.DoPreview()
        {
            try
            {
                Log("DoPreview begin, host thread=" + Thread.CurrentThread.GetApartmentState());
                if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
                {
                    EnsureControl();
                    RunOnUi(() => _control.SetMessage("文件不可用或已被移动"));
                    return S_OK;
                }

                EnsureControl();
                RunOnUi(() => _control.SetMessage("正在加载…"));

                if (_cts != null)
                {
                    _cts.Dispose();
                }
                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;
                string path = _filePath;
                string ext = SupportedFormats.GetExtension(path).ToUpperInvariant();

                Task.Run(() =>
                {
                    try
                    {
                        // 动图优先：第一帧秒显，动画帧后台解码
                        bool maybeAnim = ext == "GIF" || ext == "WEBP";
                        bool firstShown = false;
                        if (maybeAnim)
                        {
                            // 1) 第一帧秒显（走静态缓存，约 15ms）
                            try
                            {
                                var first = DecodeCore.Decode(path, 800, token);
                                if (!token.IsCancellationRequested)
                                {
                                    firstShown = true;
                                    string finfo = string.Format("{0} × {1}   {2}   {3}   ·   正在解码动画帧…",
                                        first.Width, first.Height, FormatBytes(first.FileSize), ext);
                                    RunOnUi(() =>
                                    {
                                        if (!token.IsCancellationRequested)
                                        {
                                            _control.SetImage(first.Bitmap, first.HasAlpha, finfo);
                                        }
                                    });
                                }
                            }
                            catch
                            {
                            }

                            // 2) 后台解码全部帧（原始像素直拷 + 缓存）
                            try
                            {
                                var a = DecodeCore.DecodeAnimated(path, 800, token);
                                if (token.IsCancellationRequested)
                                {
                                    return;
                                }

                                if (a.Frames.Count > 1)
                                {
                                    int totalMs = 0;
                                    foreach (var d in a.DelaysMs)
                                    {
                                        totalMs += d;
                                    }

                                    Log("Animated: " + a.Frames.Count + " frames, " + totalMs + "ms loop, decode " + a.Ms + "ms");
                                    string ainfo = string.Format("{0} × {1}   {2}   {3} 动图   {4} 帧 · {5:F1} 秒/循环   ·   Magick",
                                        a.Frames[0].Width, a.Frames[0].Height, FormatBytes(a.FileSize), ext,
                                        a.Frames.Count, totalMs / 1000.0);
                                    RunOnUi(() =>
                                    {
                                        if (!token.IsCancellationRequested)
                                        {
                                            Log("SetAnimation executing on UI thread");
                                            _control.SetAnimation(a.Frames.ToArray(), a.DelaysMs.ToArray(), a.HasAlpha, ainfo);
                                        }
                                    });
                                    return;
                                }

                                // 单帧"动图"：第一帧已显示，直接结束
                                return;
                            }
                            catch (OperationCanceledException)
                            {
                                return;
                            }
                            catch (Exception animEx)
                            {
                                Log("Animated failed: " + animEx.Message);
                                if (firstShown)
                                {
                                    return;   // 静态第一帧已显示，静默结束
                                }
                            }
                        }

                        DecodeResult r = DecodeCore.Decode(path, DecodeCore.DefaultMaxPixels, token);
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        Log("Decode OK: " + r.Width + "x" + r.Height + " via " + r.Decoder + " " + r.Ms + "ms");

                        string info = string.Format("{0} × {1}   {2}   {3}   ·   {4} ({5:F0} ms)",
                            r.Width, r.Height, FormatBytes(r.FileSize), ext, r.Decoder, r.Ms);

                        RunOnUi(() =>
                        {
                            if (!token.IsCancellationRequested)
                            {
                                Log("SetImage executing on UI thread");
                                _control.SetImage(r.Bitmap, r.HasAlpha, info);
                            }
                        });
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Log("Decode EX: " + ex.GetType().Name + " " + ex.Message);
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }
                        string msg = "无法预览该文件" + Environment.NewLine + ex.Message;
                        _control.BeginInvoke((Action)(() =>
                        {
                            if (!token.IsCancellationRequested)
                            {
                                _control.SetMessage(msg);
                            }
                        }));
                    }
                }, token);

                return S_OK;
            }
            catch (Exception ex)
            {
                Log("DoPreview EX: " + ex.Message);
                return ex.HResult;
            }
        }

        int IPreviewHandler.Unload()
        {
            try
            {
                Log("Unload");
                if (_cts != null)
                {
                    try { _cts.Cancel(); } catch { }
                    _cts.Dispose();
                    _cts = null;
                }

                var c = _control;
                _control = null;
                if (c != null)
                {
                    try
                    {
                        c.BeginInvoke((Action)(() => Application.ExitThread()));
                    }
                    catch { }
                }

                var t = _uiThread;
                _uiThread = null;
                if (t != null && t.IsAlive)
                {
                    t.Join(1000);
                }

                _filePath = null;
                return S_OK;
            }
            catch (Exception ex)
            {
                return ex.HResult;
            }
        }

        int IPreviewHandler.SetFocus()
        {
            try
            {
                var c = _control;
                if (c != null)
                {
                    RunOnUi(() => c.Focus());
                }
                return S_OK;
            }
            catch (Exception ex)
            {
                return ex.HResult;
            }
        }

        int IPreviewHandler.QueryFocus(out IntPtr phwnd)
        {
            phwnd = NativeMethods.GetFocus();
            return phwnd != IntPtr.Zero ? S_OK : E_FAIL;
        }

        int IPreviewHandler.TranslateAccelerator(ref MSG pmsg)
        {
            // 全部按键交还宿主（Explorer）处理
            return S_FALSE;
        }

        // ---------- IPreviewHandlerVisuals ----------

        int IPreviewHandlerVisuals.SetBackgroundColor(int color)
        {
            try
            {
                Color c = Color.FromArgb(color & 0xFF, (color >> 8) & 0xFF, (color >> 16) & 0xFF);
                RunOnUi(() => _control.SetBackground(c));
                return S_OK;
            }
            catch (Exception ex)
            {
                return ex.HResult;
            }
        }

        int IPreviewHandlerVisuals.SetFont(ref LOGFONTW plf)
        {
            return S_OK;
        }

        int IPreviewHandlerVisuals.SetTextColor(int color)
        {
            return S_OK;
        }

        // ---------- 专用 STA 渲染线程 ----------

        private void EnsureControl()
        {
            if (_control != null)
            {
                return;
            }

            var ready = new ManualResetEventSlim(false);
            var parent = _parentHwnd;
            var rect = _rect;
            var thread = new Thread(() =>
            {
                try
                {
                    var c = new PreviewControl();
                    _control = c;
                    if (parent != IntPtr.Zero)
                    {
                        NativeMethods.SetParent(c.Handle, parent);
                    }
                    int w = Math.Max(1, rect.Right - rect.Left);
                    int h = Math.Max(1, rect.Bottom - rect.Top);
                    c.SetBounds(rect.Left, rect.Top, w, h);
                    c.Show();
                    Log("UI thread ready, apt=" + Thread.CurrentThread.GetApartmentState());
                    ready.Set();
                    Application.Run(); // 专用消息泵，直到 ExitThread
                }
                catch (Exception ex)
                {
                    Log("UI thread EX: " + ex.Message);
                    try { ready.Set(); } catch { }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _uiThread = thread;
            ready.Wait(3000);
        }

        private void RunOnUi(Action action)
        {
            var c = _control;
            if (c == null)
            {
                return;
            }
            try
            {
                c.BeginInvoke(action);
            }
            catch
            {
                // 控件正在销毁
            }
        }

        private void AttachAndLayout()
        {
            var c = _control;
            if (c == null)
            {
                return;
            }
            if (_parentHwnd != IntPtr.Zero)
            {
                NativeMethods.SetParent(c.Handle, _parentHwnd);
            }
            int w = Math.Max(1, _rect.Right - _rect.Left);
            int h = Math.Max(1, _rect.Bottom - _rect.Top);
            c.SetBounds(_rect.Left, _rect.Top, w, h);
            c.Invalidate();
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

        private static void Log(string msg)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ImagePeek");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "handler.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff ") + msg + "\r\n");
            }
            catch
            {
            }
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

            [DllImport("user32.dll")]
            public static extern IntPtr GetFocus();
        }
    }
}
