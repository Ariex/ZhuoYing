using System;
using System.Runtime.InteropServices;

namespace Zhuoying.Platform.Linux;

/// <summary>
/// Linux/X11 的 P/Invoke 集中地（对应 Windows 的 <c>Win32.cs</c>）。
/// 声明只放这里，调用点分散在 X11*.cs——不要向上层泄漏（见 CLAUDE.md「平台边界」）。
///
/// NativeAOT 约束：全部为 DllImport 直调，无 COM、无反射。
/// </summary>
internal static partial class X11Interop
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXrandr = "libXrandr.so.2";
    private const string LibXfixes = "libXfixes.so.3";
    private const string LibXext = "libXext.so.6";

    // ---------- 会话类型 ----------

    /// <summary>当前图形会话类型："x11" / "wayland" / "none"。
    /// Wayland 下应用经 Xwayland 跑，X11 抓屏只能看到 X 客户端窗口（合成器内容全黑），
    /// 抓屏必须走 xdg-desktop-portal，见 <see cref="PortalScreenCast"/>。</summary>
    internal static string SessionKind { get; } = DetectSession();

    internal static bool IsWayland => SessionKind == "wayland";

    private static string DetectSession()
    {
        var t = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (string.Equals(t, "wayland", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return "wayland";
        if (string.Equals(t, "x11", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return "x11";
        return "none";
    }

    // ---------- 基础类型 ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct XRectangle
    {
        public short X, Y;
        public ushort Width, Height;
    }

    /// <summary>XImage 头部（只声明到我们要读的字段之后；实际结构更长，
    /// 一律经指针访问，绝不按值传递）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XImage
    {
        public int Width, Height;
        public int XOffset;
        public int Format;
        public IntPtr Data;
        public int ByteOrder;
        public int BitmapUnit;
        public int BitmapBitOrder;
        public int BitmapPad;
        public int Depth;
        public int BytesPerLine;
        public int BitsPerPixel;
        public nuint RedMask, GreenMask, BlueMask;
    }

    internal const int ZPixmap = 2;
    internal static readonly nuint AllPlanes = unchecked((nuint)~0UL);

    // ---------- Display / Window ----------

    [LibraryImport(LibX11, EntryPoint = "XOpenDisplay", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr XOpenDisplay(string? display);

    [LibraryImport(LibX11, EntryPoint = "XCloseDisplay")]
    internal static partial int XCloseDisplay(IntPtr display);

    [LibraryImport(LibX11, EntryPoint = "XDefaultRootWindow")]
    internal static partial IntPtr XDefaultRootWindow(IntPtr display);

    [LibraryImport(LibX11, EntryPoint = "XDefaultScreen")]
    internal static partial int XDefaultScreen(IntPtr display);

    [LibraryImport(LibX11, EntryPoint = "XDisplayWidth")]
    internal static partial int XDisplayWidth(IntPtr display, int screen);

    [LibraryImport(LibX11, EntryPoint = "XDisplayHeight")]
    internal static partial int XDisplayHeight(IntPtr display, int screen);

    [LibraryImport(LibX11, EntryPoint = "XSync")]
    internal static partial int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);

    [LibraryImport(LibX11, EntryPoint = "XFlush")]
    internal static partial int XFlush(IntPtr display);

    [LibraryImport(LibX11, EntryPoint = "XFree")]
    internal static partial int XFree(IntPtr data);

    [LibraryImport(LibX11, EntryPoint = "XResourceManagerString")]
    internal static partial IntPtr XResourceManagerString(IntPtr display);

    [LibraryImport(LibX11, EntryPoint = "XInitThreads")]
    internal static partial int XInitThreads();

    // ---------- 抓屏 ----------

    [LibraryImport(LibX11, EntryPoint = "XGetImage")]
    internal static partial IntPtr XGetImage(IntPtr display, IntPtr drawable,
        int x, int y, uint width, uint height, nuint planeMask, int format);

    [LibraryImport(LibX11, EntryPoint = "XDestroyImage")]
    internal static partial int XDestroyImage(IntPtr image);

    // ---------- 指针 ----------

    [LibraryImport(LibX11, EntryPoint = "XQueryPointer")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool XQueryPointer(IntPtr display, IntPtr window,
        out IntPtr rootReturn, out IntPtr childReturn,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    // ---------- 窗口树与属性（EWMH 窗口枚举） ----------

    [LibraryImport(LibX11, EntryPoint = "XInternAtom", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr XInternAtom(IntPtr display, string name,
        [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [LibraryImport(LibX11, EntryPoint = "XGetWindowProperty")]
    internal static partial int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property,
        nint offset, nint length, [MarshalAs(UnmanagedType.Bool)] bool delete, IntPtr reqType,
        out IntPtr actualType, out int actualFormat, out nuint nItems, out nuint bytesAfter,
        out IntPtr prop);

    [StructLayout(LayoutKind.Sequential)]
    internal struct XWindowAttributes
    {
        public int X, Y;
        public int Width, Height;
        public int BorderWidth;
        public int Depth;
        public IntPtr Visual;
        public IntPtr Root;
        public int Class;
        public int BitGravity, WinGravity;
        public int BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public IntPtr Colormap;
        public int MapInstalled;
        public int MapState;
        public nint AllEventMasks, YourEventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public IntPtr Screen;
    }

    internal const int IsViewable = 2;

    [LibraryImport(LibX11, EntryPoint = "XGetWindowAttributes")]
    internal static partial int XGetWindowAttributes(IntPtr display, IntPtr window,
        out XWindowAttributes attrs);

    [LibraryImport(LibX11, EntryPoint = "XTranslateCoordinates")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool XTranslateCoordinates(IntPtr display, IntPtr srcWindow,
        IntPtr destWindow, int srcX, int srcY, out int destX, out int destY, out IntPtr child);

    // ---------- 全局热键 ----------

    [LibraryImport(LibX11, EntryPoint = "XGrabKey")]
    internal static partial int XGrabKey(IntPtr display, int keycode, uint modifiers,
        IntPtr grabWindow, [MarshalAs(UnmanagedType.Bool)] bool ownerEvents,
        int pointerMode, int keyboardMode);

    [LibraryImport(LibX11, EntryPoint = "XUngrabKey")]
    internal static partial int XUngrabKey(IntPtr display, int keycode, uint modifiers,
        IntPtr grabWindow);

    [LibraryImport(LibX11, EntryPoint = "XKeysymToKeycode")]
    internal static partial byte XKeysymToKeycode(IntPtr display, nuint keysym);

    [LibraryImport(LibX11, EntryPoint = "XStringToKeysym", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nuint XStringToKeysym(string name);

    [LibraryImport(LibX11, EntryPoint = "XSelectInput")]
    internal static partial int XSelectInput(IntPtr display, IntPtr window, nint eventMask);

    [LibraryImport(LibX11, EntryPoint = "XNextEvent")]
    internal static partial int XNextEvent(IntPtr display, IntPtr eventReturn);

    [LibraryImport(LibX11, EntryPoint = "XPending")]
    internal static partial int XPending(IntPtr display);

    [LibraryImport(LibX11, EntryPoint = "XSetErrorHandler")]
    internal static partial IntPtr XSetErrorHandler(IntPtr handler);

    [LibraryImport(LibX11, EntryPoint = "XConnectionNumber")]
    internal static partial int XConnectionNumber(IntPtr display);

    internal const int GrabModeSync = 0;
    internal const int GrabModeAsync = 1;
    internal const int KeyPress = 2;
    internal const nint KeyPressMask = 1 << 0;

    /// <summary>X11 修饰键掩码。</summary>
    internal const uint ShiftMask = 1 << 0;
    internal const uint LockMask = 1 << 1;   // CapsLock
    internal const uint ControlMask = 1 << 2;
    internal const uint Mod1Mask = 1 << 3;   // Alt
    internal const uint Mod2Mask = 1 << 4;   // NumLock
    internal const uint Mod4Mask = 1 << 6;   // Super/Win
    internal const uint Mod5Mask = 1 << 7;   // ScrollLock（部分布局）

    // ---------- XRandR（多显示器） ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct XRRScreenResources
    {
        public nint Timestamp, ConfigTimestamp;
        public int NCrtc;
        public IntPtr Crtcs;
        public int NOutput;
        public IntPtr Outputs;
        public int NMode;
        public IntPtr Modes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XRRCrtcInfo
    {
        public nint Timestamp;
        public int X, Y;
        public uint Width, Height;
        public IntPtr Mode;
        public ushort Rotation;
        public int NOutput;
        public IntPtr Outputs;
        public ushort Rotations;
        public int NPossible;
        public IntPtr Possible;
    }

    [LibraryImport(LibXrandr, EntryPoint = "XRRGetScreenResourcesCurrent")]
    internal static partial IntPtr XRRGetScreenResourcesCurrent(IntPtr display, IntPtr window);

    [LibraryImport(LibXrandr, EntryPoint = "XRRFreeScreenResources")]
    internal static partial void XRRFreeScreenResources(IntPtr resources);

    [LibraryImport(LibXrandr, EntryPoint = "XRRGetCrtcInfo")]
    internal static partial IntPtr XRRGetCrtcInfo(IntPtr display, IntPtr resources, IntPtr crtc);

    [LibraryImport(LibXrandr, EntryPoint = "XRRFreeCrtcInfo")]
    internal static partial void XRRFreeCrtcInfo(IntPtr crtcInfo);

    [LibraryImport(LibXrandr, EntryPoint = "XRRGetOutputPrimary")]
    internal static partial IntPtr XRRGetOutputPrimary(IntPtr display, IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    internal struct XRROutputInfo
    {
        public nint Timestamp;
        public IntPtr Crtc;
        public IntPtr Name;
        public int NameLen;
        public nuint MmWidth, MmHeight;
        public ushort Connection;
        public ushort SubpixelOrder;
        public int NCrtc;
        public IntPtr Crtcs;
        public int NClone;
        public IntPtr Clones;
        public int NMode, NPreferred;
        public IntPtr Modes;
    }

    [LibraryImport(LibXrandr, EntryPoint = "XRRGetOutputInfo")]
    internal static partial IntPtr XRRGetOutputInfo(IntPtr display, IntPtr resources, IntPtr output);

    [LibraryImport(LibXrandr, EntryPoint = "XRRFreeOutputInfo")]
    internal static partial void XRRFreeOutputInfo(IntPtr outputInfo);

    // ---------- XFixes（光标图像，录屏补绘用） ----------

    /// <summary>XFixesCursorImage 头。像素数组 Pixels 是 <c>unsigned long*</c>
    /// （64 位机上每像素 8 字节！低 32 位为 ARGB，见 XFixes 规范）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XFixesCursorImage
    {
        public short X, Y;
        public ushort Width, Height;
        public ushort XHot, YHot;
        public nint CursorSerial;
        public IntPtr Pixels;
    }

    [LibraryImport(LibXfixes, EntryPoint = "XFixesGetCursorImage")]
    internal static partial IntPtr XFixesGetCursorImage(IntPtr display);

    [LibraryImport(LibXfixes, EntryPoint = "XFixesQueryExtension")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool XFixesQueryExtension(IntPtr display,
        out int eventBase, out int errorBase);

    // ---------- XShape（输入穿透） ----------

    internal const int ShapeInput = 2;
    internal const int ShapeSet = 0;

    [LibraryImport(LibXext, EntryPoint = "XShapeCombineRectangles")]
    internal static partial void XShapeCombineRectangles(IntPtr display, IntPtr window,
        int destKind, int xOff, int yOff, IntPtr rectangles, int nRects, int op, int ordering);

    [LibraryImport(LibXext, EntryPoint = "XShapeQueryExtension")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool XShapeQueryExtension(IntPtr display,
        out int eventBase, out int errorBase);
}
