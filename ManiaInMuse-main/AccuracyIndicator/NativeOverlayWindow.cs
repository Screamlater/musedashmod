using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AccuracyIndicator;

/// <summary>
/// 原生透明覆盖窗口。
/// 屏幕可见，但通过 SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)
/// 从 Windows 捕获 API（Steam 录屏 / 截图 / Xbox Game Bar / OBS 等）中排除，
/// 因此录屏里不会出现本窗口的内容。
/// 仅依赖 user32/gdi32，无第三方库；窗口必须在 Unity 主线程创建（消息泵由游戏主循环提供）。
/// </summary>
public sealed class NativeOverlayWindow : IDisposable
{
    // ---------------- 常量 ----------------
    private const uint WS_POPUP = 0x80000000;

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020; // 点击穿透
    private const int WS_EX_TOOLWINDOW = 0x00000080;  // 不进 Alt+Tab / 任务栏
    private const int WS_EX_NOACTIVATE = 0x08000000;  // 不抢焦点

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011; // Win10 2004+：从捕获中排除

    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;

    private const uint AC_SRC_OVER = 0;
    private const byte AC_SRC_ALPHA = 0x01;
    private const uint ULW_ALPHA = 0x00000002;

    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;
    private const int WHITE_BRUSH = 0;

    // ---------------- P/Invoke user32 ----------------
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst,
        ref SIZE size, IntPtr hdcSrc, ref POINT src, uint crKey, ref BLENDFUNCTION blend, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string className, IntPtr hInstance);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string name);

    // ---------------- P/Invoke gdi32 ----------------
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation,
        int cWeight, uint italic, uint underline, uint strikeOut, uint charSet,
        uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr hdc, int color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkColor(IntPtr hdc, int color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TextOutW(IntPtr hdc, int x, int y, string text, int length);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int length, out SIZE size);

    [DllImport("gdi32.dll")]
    private static extern bool PatBlt(IntPtr hdc, int x, int y, int w, int h, uint rop);

    // ---------------- 结构体 / 委托 ----------------
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Cx; public int Cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private static readonly WndProcDelegate WndProcInstance = WndProcImpl;

    private static IntPtr WndProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                return new IntPtr(HTTRANSPARENT); // 点击穿透，不影响游戏操作
            case WM_ERASEBKGND:
                return new IntPtr(1);
            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    // ---------------- 字段 ----------------
    private IntPtr _hwnd;
    private IntPtr _hdcMem;
    private IntPtr _hBmp;
    private IntPtr _hOldBmp; // 位图未选中时的占位对象，删除位图前先换回它
    private IntPtr _scan0;
    private byte[] _pixels;
    private byte[] _rowBuffer;
    private string _className;
    private int _width = 1;
    private int _height = 1;
    private bool _disposed;

    // GDI 文字（遮罩合成，用于连击数字等）
    private IntPtr _maskDc;
    private IntPtr _maskBmp;
    private IntPtr _maskScan0;
    private int _maskW;
    private int _maskH;
    private IntPtr _font;
    private int _fontSizePx;

    /// <summary>当前窗口客户区尺寸（由 SyncToGameWindow 每帧更新）。</summary>
    public int ClientWidth { get; private set; } = 1;

    /// <summary>当前窗口客户区尺寸（由 SyncToGameWindow 每帧更新）。</summary>
    public int ClientHeight { get; private set; } = 1;

    public IntPtr Handle => _hwnd;

    /// <summary>创建窗口（默认点击穿透）。必须在 Unity 主线程调用。</summary>
    public void Create(int width, int height)
    {
        Create(width, height, true);
    }

    /// <summary>创建窗口。clickThrough=false 时窗口接收鼠标（用于设置菜单）。必须在 Unity 主线程调用。</summary>
    public void Create(int width, int height, bool clickThrough)
    {
        if (_hwnd != IntPtr.Zero)
            return;

        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        _className = "ManiaInMuseOverlay_" + Guid.NewGuid().ToString("N"); // 唯一类名：多个实例共存时避免注册冲突
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcInstance),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className
        };
        if (RegisterClassW(ref wc) == 0)
            throw new InvalidOperationException("RegisterClass failed");

        int exStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | (clickThrough ? WS_EX_TRANSPARENT : 0);
        _hwnd = CreateWindowExW(exStyle, _className, "ManiaInMuseOverlay",
            WS_POPUP, 0, 0, _width, _height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        // ★ 核心：把本窗口从屏幕捕获中排除（Steam 录屏/截图里看不到它，屏幕上照常可见）
        if (!SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE))
            MelonLogger.Error($"[ManiaInMuse] SetWindowDisplayAffinity failed: {Marshal.GetLastWin32Error()}");

        _hdcMem = CreateCompatibleDC(IntPtr.Zero);
        _hOldBmp = SelectObject(_hdcMem, GetStockObject(WHITE_BRUSH)); // 占位对象
        CreateBackBuffer(_width, _height);
    }

    /// <summary>把窗口移到指定屏幕位置并调整大小（用于设置菜单等独立窗口）。</summary>
    public void Move(int x, int y, int width, int height)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        int w = Math.Max(1, width);
        int h = Math.Max(1, height);
        ClientWidth = w;
        ClientHeight = h;

        if (w != _width || h != _height)
            CreateBackBuffer(w, h);

        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>控制本窗口是否从屏幕捕获中排除（游戏叠层为 true，设置菜单为 false）。</summary>
    public void SetCaptureExcluded(bool excluded)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        SetWindowDisplayAffinity(_hwnd, excluded ? WDA_EXCLUDEFROMCAPTURE : 0);
    }

    /// <summary>把窗口贴到目标窗口（游戏窗口）客户区上方并置顶；返回是否成功。</summary>
    public bool SyncToGameWindow(IntPtr targetHwnd)
    {
        if (_hwnd == IntPtr.Zero || targetHwnd == IntPtr.Zero)
            return false;

        if (!GetClientRect(targetHwnd, out var client))
            return false;

        int w = Math.Max(1, client.Right - client.Left);
        int h = Math.Max(1, client.Bottom - client.Top);
        ClientWidth = w;
        ClientHeight = h;

        if (w != _width || h != _height)
            CreateBackBuffer(w, h);

        var topLeft = new POINT();
        ClientToScreen(targetHwnd, ref topLeft);

        SetWindowPos(_hwnd, HWND_TOPMOST, topLeft.X, topLeft.Y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        return true;
    }

    public void Show()
    {
        if (_hwnd != IntPtr.Zero)
            ShowWindow(_hwnd, SW_SHOWNA);
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero)
            ShowWindow(_hwnd, SW_HIDE);
    }

    /// <summary>判断指定窗口当前是否为前台窗口（用于游戏失焦时隐藏叠加层）。</summary>
    public static bool IsForeground(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;
    }

    /// <summary>判断前台窗口是否属于本进程（游戏是否处于前台）。比比较窗口句柄更稳健。</summary>
    public static bool IsProcessForeground()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        GetWindowThreadProcessId(foreground, out uint foregroundPid);

        uint myPid;
        try
        {
            myPid = (uint)Process.GetCurrentProcess().Id;
        }
        catch
        {
            return false;
        }

        return foregroundPid == myPid;
    }

    /// <summary>窗口句柄是否仍然有效（句柄缓存后用于校验）。</summary>
    public static bool IsWindowValid(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero && IsWindow(hwnd);
    }

    /// <summary>查找本进程面积最大的可见顶层窗口（游戏主窗口）。</summary>
    public static IntPtr FindProcessWindow()
    {
        uint pid;
        try
        {
            pid = (uint)Process.GetCurrentProcess().Id;
        }
        catch
        {
            return IntPtr.Zero;
        }

        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            GetWindowThreadProcessId(hwnd, out uint windowPid);
            if (windowPid != pid)
                return true;

            GetWindowRect(hwnd, out var rect);
            long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }

            return true;
        }, IntPtr.Zero);
        return best;
    }

    // ---------------- 绘制（每帧：Clear → 若干 FillRect → Commit） ----------------

    /// <summary>清空画布为全透明。每帧开头调用。</summary>
    public void Clear()
    {
        if (_pixels != null && _pixels.Length > 0)
            Array.Clear(_pixels, 0, _pixels.Length);
    }

    /// <summary>填充一个轴对齐矩形（窗口像素坐标，y 向下，支持半透明 alpha）。</summary>
    public void FillRect(float x, float y, float w, float h, byte r, byte g, byte b, byte a)
    {
        if (_pixels == null || a == 0 || w <= 0 || h <= 0)
            return;

        int x0 = Math.Max(0, (int)Math.Floor(x));
        int y0 = Math.Max(0, (int)Math.Floor(y));
        int x1 = Math.Min(_width, (int)Math.Ceiling(x + w));
        int y1 = Math.Min(_height, (int)Math.Ceiling(y + h));
        if (x1 <= x0 || y1 <= y0)
            return;

        int rowPixels = x1 - x0;
        int needed = rowPixels * 4;
        if (_rowBuffer == null || _rowBuffer.Length < needed)
            _rowBuffer = new byte[needed];

        // 预乘 Alpha（UpdateLayeredWindow + AC_SRC_ALPHA 需要预乘 BGRA）
        byte pr = (byte)((r * a + 127) / 255);
        byte pg = (byte)((g * a + 127) / 255);
        byte pb = (byte)((b * a + 127) / 255);
        for (int i = 0; i < needed; i += 4)
        {
            _rowBuffer[i] = pb;
            _rowBuffer[i + 1] = pg;
            _rowBuffer[i + 2] = pr;
            _rowBuffer[i + 3] = a;
        }

        int stride = _width * 4;
        for (int yy = y0; yy < y1; yy++)
            Array.Copy(_rowBuffer, 0, _pixels, yy * stride + x0 * 4, needed);
    }

    /// <summary>
    /// 把一张已预乘 Alpha 的 BGRA 图像缩放到目标矩形并合成到画布。
    /// 源图尺寸 imgW x imgH，目标 (x, y, w, h)，全局透明度 alpha（0-255）。
    /// </summary>
    public void DrawImage(byte[] bgra, int imgW, int imgH, float x, float y, float w, float h, byte alpha)
    {
        if (_pixels == null || alpha == 0 || w <= 0 || h <= 0 || imgW <= 0 || imgH <= 0)
            return;

        int x0 = Math.Max(0, (int)Math.Floor(x));
        int y0 = Math.Max(0, (int)Math.Floor(y));
        int x1 = Math.Min(_width, (int)Math.Ceiling(x + w));
        int y1 = Math.Min(_height, (int)Math.Ceiling(y + h));
        if (x1 <= x0 || y1 <= y0)
            return;

        float invW = 1f / w;
        float invH = 1f / h;
        int imgStride = imgW * 4;
        int dstStride = _width * 4;

        for (int yy = y0; yy < y1; yy++)
        {
            int sy = (int)((yy + 0.5f - y) * invH * imgH);
            if (sy < 0) sy = 0;
            else if (sy >= imgH) sy = imgH - 1;
            int srcRow = sy * imgStride;
            int dstRow = yy * dstStride + x0 * 4;

            for (int xx = x0; xx < x1; xx++)
            {
                int sx = (int)((xx + 0.5f - x) * invW * imgW);
                if (sx < 0) sx = 0;
                else if (sx >= imgW) sx = imgW - 1;

                int si = srcRow + sx * 4;
                int di = dstRow + (xx - x0) * 4;

                byte a = (byte)((bgra[si + 3] * alpha + 127) / 255);
                if (a == 0)
                    continue;

                int inv = 255 - a;
                _pixels[di] = (byte)((bgra[si] * a + _pixels[di] * inv + 127) / 255);
                _pixels[di + 1] = (byte)((bgra[si + 1] * a + _pixels[di + 1] * inv + 127) / 255);
                _pixels[di + 2] = (byte)((bgra[si + 2] * a + _pixels[di + 2] * inv + 127) / 255);
                _pixels[di + 3] = (byte)(a + (_pixels[di + 3] * inv + 127) / 255);
            }
        }
    }

    /// <summary>把 CPU 画布（_pixels）复制进 DIB，供 GDI 文字等叠加。Commit 之前调用。</summary>
    public void CopyBackBuffer()
    {
        if (_hwnd == IntPtr.Zero || _pixels == null || _pixels.Length == 0 || _scan0 == IntPtr.Zero)
            return;
        Marshal.Copy(_pixels, 0, _scan0, _pixels.Length);
    }

    /// <summary>把 DIB 内容合成到窗口（UpdateLayeredWindow）。</summary>
    public void Present()
    {
        if (_hwnd == IntPtr.Zero || _pixels == null || _pixels.Length == 0)
            return;

        var dst = new POINT();
        var src = new POINT();
        var size = new SIZE { Cx = _width, Cy = _height };
        var blend = new BLENDFUNCTION { BlendOp = (byte)AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref dst, ref size, _hdcMem, ref src, 0, ref blend, ULW_ALPHA);
    }

    /// <summary>测量 GDI 文字像素宽度（与 DrawGdiText 同一字体）。</summary>
    public int MeasureTextWidth(string text, int fontSizePx)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        EnsureFont(fontSizePx);
        EnsureMask();
        SelectObject(_maskDc, _font);

        if (!GetTextExtentPoint32W(_maskDc, text, text.Length, out var ext))
            return 0;

        return ext.Cx + 4;
    }

    /// <summary>
    /// 用 GDI 绘制抗锯齿文字（先画到 32bpp 遮罩，按亮度合成到画布）。
    /// 只在 CPU 画布上绘制，必须在 CopyBackBuffer 之前调用。
    /// </summary>
    public void DrawGdiText(string text, float x, float y, int fontSizePx, byte r, byte g, byte b)
    {
        if (_pixels == null || string.IsNullOrEmpty(text))
            return;

        EnsureFont(fontSizePx);
        EnsureMask();

        SelectObject(_maskDc, _font);
        SetTextColor(_maskDc, 0x00FFFFFF); // 白字
        SetBkColor(_maskDc, 0x00000000);   // 黑底
        SetBkMode(_maskDc, 2 /*OPAQUE*/);

        if (!GetTextExtentPoint32W(_maskDc, text, text.Length, out var ext))
            return;

        int mw = Math.Max(8, ext.Cx + 4);
        int mh = Math.Max(8, ext.Cy + 4);
        EnsureMaskSize(mw, mh);

        PatBlt(_maskDc, 0, 0, _maskW, _maskH, 0x00000042 /*BLACKNESS*/);
        TextOutW(_maskDc, 2, 2, text, text.Length);

        // 遮罩亮度 -> alpha，合成到画布（遮罩为 32bpp，每行 _maskW * 4 字节）
        int maskStride = _maskW * 4;
        int dstStride = _width * 4;
        int startX = (int)Math.Floor(x);
        int startY = (int)Math.Floor(y);

        for (int yy = 0; yy < _maskH; yy++)
        {
            int py = startY + yy;
            if (py < 0 || py >= _height)
                continue;

            int maskRow = yy * maskStride;
            int dstRow = py * dstStride;
            for (int xx = 0; xx < _maskW; xx++)
            {
                int px = startX + xx;
                if (px < 0 || px >= _width)
                    continue;

                byte v = Marshal.ReadByte(_maskScan0, maskRow + xx);
                if (v == 0)
                    continue;

                int di = dstRow + px * 4;
                _pixels[di] = (byte)((b * v + 127) / 255);
                _pixels[di + 1] = (byte)((g * v + 127) / 255);
                _pixels[di + 2] = (byte)((r * v + 127) / 255);
                _pixels[di + 3] = v;
            }
        }
    }

    /// <summary>把画布内容合成到窗口。Clear/FillRect 之后、每帧末尾调用。</summary>
    public void Commit()
    {
        CopyBackBuffer();
        Present();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_maskDc != IntPtr.Zero)
        {
            DeleteDC(_maskDc); // 先删 DC，解除位图选中关系
            _maskDc = IntPtr.Zero;
        }
        if (_maskBmp != IntPtr.Zero)
        {
            DeleteObject(_maskBmp);
            _maskBmp = IntPtr.Zero;
        }
        if (_font != IntPtr.Zero)
        {
            DeleteObject(_font);
            _font = IntPtr.Zero;
        }
        if (_hBmp != IntPtr.Zero)
        {
            SelectObject(_hdcMem, _hOldBmp); // 先解除选中再删除
            DeleteObject(_hBmp);
            _hBmp = IntPtr.Zero;
        }
        if (_hdcMem != IntPtr.Zero)
        {
            DeleteDC(_hdcMem);
            _hdcMem = IntPtr.Zero;
        }
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (!string.IsNullOrEmpty(_className))
        {
            try
            {
                UnregisterClassW(_className, GetModuleHandle(null));
            }
            catch { }
            _className = null;
        }

        _pixels = null;
        _rowBuffer = null;
    }

    private void CreateBackBuffer(int width, int height)
    {
        if (_hBmp != IntPtr.Zero)
        {
            SelectObject(_hdcMem, _hOldBmp); // 先把旧位图移出 DC，避免删除仍选中的 GDI 对象
            DeleteObject(_hBmp);
            _hBmp = IntPtr.Zero;
        }

        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        bmi.bmiHeader.biWidth = _width;
        bmi.bmiHeader.biHeight = -_height; // 负值：自上而下
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;

        _hBmp = CreateDIBSection(IntPtr.Zero, ref bmi, DIB_RGB_COLORS, out _scan0, IntPtr.Zero, 0);
        if (_hBmp == IntPtr.Zero)
            throw new InvalidOperationException($"CreateDIBSection failed: {Marshal.GetLastWin32Error()}");

        _hOldBmp = SelectObject(_hdcMem, _hBmp);
        _pixels = new byte[_width * _height * 4];
    }

    private void EnsureFont(int fontSizePx)
    {
        if (_font != IntPtr.Zero && _fontSizePx == fontSizePx)
            return;

        if (_font != IntPtr.Zero)
        {
            DeleteObject(_font);
            _font = IntPtr.Zero;
        }

        // 粗体，-fontSizePx = 像素高度
        _font = CreateFontW(-Math.Max(8, fontSizePx), 0, 0, 0, 700 /*FW_BOLD*/,
            0, 0, 0, 1 /*DEFAULT_CHARSET*/, 0, 0, 4 /*CLEARTYPE_QUALITY*/, 0, "Segoe UI");
        _fontSizePx = fontSizePx;
    }

    private void EnsureMask()
    {
        if (_maskDc != IntPtr.Zero)
            return;

        _maskDc = CreateCompatibleDC(IntPtr.Zero);
        _maskW = 8;
        _maskH = 8;
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        bmi.bmiHeader.biWidth = _maskW;
        bmi.bmiHeader.biHeight = -_maskH;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;
        _maskBmp = CreateDIBSection(IntPtr.Zero, ref bmi, DIB_RGB_COLORS, out _maskScan0, IntPtr.Zero, 0);
        SelectObject(_maskDc, _maskBmp);
    }

    private void EnsureMaskSize(int w, int h)
    {
        if (_maskDc == IntPtr.Zero)
            return;

        if (w <= _maskW && h <= _maskH)
            return;

        // 按需扩容（2 倍向上取整）
        int nw = Math.Max(w, _maskW * 2);
        int nh = Math.Max(h, _maskH * 2);
        nw = Math.Min(nw, 4096);
        nh = Math.Min(nh, 4096);

        if (_maskBmp != IntPtr.Zero)
        {
            DeleteObject(_maskBmp);
            _maskBmp = IntPtr.Zero;
        }

        _maskW = nw;
        _maskH = nh;
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        bmi.bmiHeader.biWidth = _maskW;
        bmi.bmiHeader.biHeight = -_maskH;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;
        _maskBmp = CreateDIBSection(IntPtr.Zero, ref bmi, DIB_RGB_COLORS, out _maskScan0, IntPtr.Zero, 0);
        SelectObject(_maskDc, _maskBmp); // 重建后必须重新选入 DC
    }
}
