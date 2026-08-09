using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;

namespace Zhuoying.Platform.Windows;

/// <summary>
/// DXGI Desktop Duplication 抓屏后端（v0.14）。
///
/// 动机：BitBlt 抓不到真·独占全屏与 MPO 硬件叠加平面的内容，HDR 桌面只能拿到
/// 洗白的 SDR 视图；DDA 在合成/扫描输出层取帧，三者皆正确，且 GPU 内拷贝 +
/// 一次回读比 GDI 全量 CPU 拷贝更快。
///
/// 结构：
///   - D3D11 设备按适配器创建并常驻（托盘进程预热，抓屏只剩取帧 + 回读）；
///   - 输出（显示器）每次抓屏现枚举——GetDesc 反映当前分辨率/布局，热插拔与
///     改分辨率天然正确；duplication 会话即建即弃——常驻会话会让系统全局禁用
///     MPO，还会与其他抓屏软件抢占会话名额；
///   - 任何一步失败只影响该输出：调用方对未覆盖矩形回退 BitBlt（远程桌面、
///     受保护内容、旋转屏、会话占满等场景整体或局部回退）；
///   - HDR（FP16 桌面）按显示器 SDR 白电平归一后做 sRGB 编码，解决 GDI 洗白。
///
/// AOT：全部 COM 调用为手写 vtable 函数指针（delegate* unmanaged），不依赖
/// 内置 COM 互操作，NativeAOT 直接可用。vtable 槽位号以 dxgi1_2.h / d3d11.h
/// 接口声明顺序为准，注释标注在各调用点。
/// </summary>
internal sealed unsafe class DesktopDuplicator : IDisposable
{
    private static DesktopDuplicator? _shared;

    /// <summary>Shared/Reset/CaptureInto 的并发闸：UI 线程（截屏会话）与 MCP
    /// 服务线程可能同时抓屏，实例内部无线程安全设计，调用方持锁使用。</summary>
    public static readonly object Gate = new();

    /// <summary>共享实例（调用方须持有 <see cref="Gate"/>）。设备级异常后调用
    /// <see cref="Reset"/> 重建。</summary>
    public static DesktopDuplicator Shared => _shared ??= new DesktopDuplicator();

    public static void Reset()
    {
        _shared?.Dispose();
        _shared = null;
    }

    private struct AdapterSlot
    {
        public IntPtr Adapter;   // IDXGIAdapter1*
        public IntPtr Device;    // ID3D11Device*
        public IntPtr Context;   // ID3D11DeviceContext*
    }

    private IntPtr _factory; // IDXGIFactory1*
    private readonly List<AdapterSlot> _adapters = new();
    private readonly Dictionary<string, byte[]> _hdrLuts = new(); // 键 = 设备名|白电平

    /// <summary>诊断日志（--test-dda 用，产线为 null 零开销）。</summary>
    internal static System.Text.StringBuilder? Diag;

    private DesktopDuplicator()
    {
        if (CreateDXGIFactory1(IID_IDXGIFactory1, out _factory) < 0)
            return; // _adapters 为空 → CaptureInto 全量交回 BitBlt
        for (uint i = 0; ; i++)
        {
            IntPtr adapter;
            // IDXGIFactory1::EnumAdapters1（槽 12）
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vt(_factory, 12))(
                    _factory, i, &adapter) != 0)
                break;
            // 纯渲染卡（无输出）不建设备
            if (!AdapterHasOutput(adapter))
            {
                Release(ref adapter);
                continue;
            }
            if (D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, IntPtr.Zero, 0,
                    IntPtr.Zero, 0, D3D11_SDK_VERSION,
                    out var device, out _, out var context) < 0)
            {
                Release(ref adapter);
                continue;
            }
            _adapters.Add(new AdapterSlot { Adapter = adapter, Device = device, Context = context });
        }
    }

    /// <summary>
    /// 用 DDA 把 region（虚拟屏幕物理像素）中各显示器覆盖的部分写入 dst
    ///（Bgra8888、alpha 置 FF），返回未能覆盖的剩余矩形（调用方 BitBlt 兜底）。
    /// 抓取失败的显示器同样留在返回值里。设备级异常直接抛出，调用方应 Reset()。
    /// </summary>
    public List<PixelRect> CaptureInto(PixelRect region, byte* dst, int dstStride)
    {
        // 适配器集合变化（插拔显卡/驱动重置）→ 由调用方重建实例
        // IDXGIFactory1::IsCurrent（槽 13）
        if (_factory == IntPtr.Zero
            || ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vt(_factory, 13))(_factory) == 0)
            throw new InvalidOperationException("DXGI 工厂过期");

        var pending = new List<PixelRect> { region };
        foreach (var slot in _adapters)
        {
            for (uint i = 0; ; i++)
            {
                IntPtr output;
                // IDXGIAdapter::EnumOutputs（槽 7）
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vt(slot.Adapter, 7))(
                        slot.Adapter, i, &output) != 0)
                    break;
                try
                {
                    DXGI_OUTPUT_DESC desc;
                    // IDXGIOutput::GetDesc（槽 7）
                    if (((delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC*, int>)Vt(output, 7))(
                            output, &desc) != 0 || desc.AttachedToDesktop == 0)
                        continue;
                    var outRect = new PixelRect(
                        desc.DesktopCoordinates.Left, desc.DesktopCoordinates.Top,
                        desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                        desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top);
                    var inter = Intersect(region, outRect);
                    if (inter.Width <= 0 || inter.Height <= 0)
                        continue;
                    // 旋转屏交给 BitBlt——GDI 虚拟桌面已呈现旋转后坐标，无需自己转置像素
                    if (desc.Rotation > DXGI_MODE_ROTATION_IDENTITY)
                        continue;
                    var name = new string((char*)desc.DeviceName); // 形如 \\.\DISPLAY1
                    if (TryCaptureOutput(slot, output, outRect, inter, name, dst, dstStride, region))
                        pending = Subtract(pending, inter);
                }
                finally
                {
                    Release(ref output);
                }
            }
        }
        return pending;
    }

    /// <summary>抓取单个输出的 inter 区域写入目标缓冲。失败返回 false（调用方回退）。</summary>
    private bool TryCaptureOutput(AdapterSlot slot, IntPtr output, PixelRect outRect, PixelRect inter,
        string deviceName, byte* dst, int dstStride, PixelRect origin)
    {
        IntPtr output1 = IntPtr.Zero, dup = IntPtr.Zero, resource = IntPtr.Zero,
            tex = IntPtr.Zero, staging = IntPtr.Zero;
        var frameHeld = false;
        try
        {
            if (QueryInterface(output, IID_IDXGIOutput1, out output1) != 0)
                return false;
            IntPtr d;
            // IDXGIOutput1::DuplicateOutput（槽 22）；受保护会话/占满/远程桌面在此失败
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Vt(output1, 22))(
                    output1, slot.Device, &d) != 0)
                return false;
            dup = d;

            // 只接受 LastPresentTime≠0 的真实合成帧。踩坑实录：新会话的首个
            // AcquireNextFrame 常常"成功"返回 LastPresentTime=0 的仅指针帧，
            // 此时桌面纹理未播种、内容全黑（是否播种取决于会话创建与 DWM 合成
            // 的时序，不可依赖）。仅指针帧放回重试；超时说明桌面完全静止，
            // 回退 BitBlt 本就像素正确——而 DDA 真正的价值场景（独占全屏游戏、
            // 视频 MPO——会话创建会触发 MPO 拆除重合成）必有合成帧很快到达
            for (var attempt = 0; attempt < 3 && !frameHeld; attempt++)
            {
                DXGI_OUTDUPL_FRAME_INFO info;
                IntPtr res;
                // IDXGIOutputDuplication::AcquireNextFrame（槽 8）
                var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, DXGI_OUTDUPL_FRAME_INFO*, IntPtr*, int>)
                    Vt(dup, 8))(dup, 80, &info, &res);
                Diag?.AppendLine($"{deviceName} 尝试{attempt}: hr=0x{hr:X8} " +
                    $"LPT={info.LastPresentTime} AF={info.AccumulatedFrames} meta={info.TotalMetadataBufferSize}");
                if (hr == DXGI_ERROR_WAIT_TIMEOUT)
                    break; // 等待期内无新合成帧：静止桌面，走 BitBlt
                if (hr != 0)
                    return false;
                if (info.LastPresentTime != 0)
                {
                    resource = res;
                    frameHeld = true;
                    break;
                }
                Release(ref res);
                // IDXGIOutputDuplication::ReleaseFrame（槽 14）
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vt(dup, 14))(dup);
            }
            if (!frameHeld)
                return false;

            if (QueryInterface(resource, IID_ID3D11Texture2D, out tex) != 0)
                return false;

            D3D11_TEXTURE2D_DESC texDesc;
            // ID3D11Texture2D::GetDesc（槽 10）
            ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Vt(tex, 10))(tex, &texDesc);
            if (texDesc.Width != (uint)outRect.Width || texDesc.Height != (uint)outRect.Height)
                return false;
            byte[]? hdrLut = null;
            if (texDesc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT)
                hdrLut = GetHdrLut(deviceName); // HDR 桌面
            else if (texDesc.Format != DXGI_FORMAT_B8G8R8A8_UNORM)
                return false;

            var stagingDesc = texDesc;
            stagingDesc.MipLevels = 1;
            stagingDesc.ArraySize = 1;
            stagingDesc.SampleCount = 1;
            stagingDesc.SampleQuality = 0;
            stagingDesc.Usage = D3D11_USAGE_STAGING;
            stagingDesc.BindFlags = 0;
            stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            stagingDesc.MiscFlags = 0;
            IntPtr st;
            // ID3D11Device::CreateTexture2D（槽 5）
            if (((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)
                    Vt(slot.Device, 5))(slot.Device, &stagingDesc, IntPtr.Zero, &st) != 0)
                return false;
            staging = st;

            // ID3D11DeviceContext::CopyResource（槽 47）
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Vt(slot.Context, 47))(
                slot.Context, staging, tex);

            D3D11_MAPPED_SUBRESOURCE mapped;
            // ID3D11DeviceContext::Map（槽 14），隐式等待拷贝完成
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)
                    Vt(slot.Context, 14))(slot.Context, staging, 0, D3D11_MAP_READ, 0, &mapped) != 0)
                return false;
            try
            {
                if (Diag != null)
                {
                    long nz = 0;
                    var probe = (byte*)mapped.pData;
                    for (var k = 0; k < 4096; k++)
                        if (probe[k] != 0)
                            nz++;
                    Diag.AppendLine($"{deviceName} 首4KB非零字节={nz} pitch={mapped.RowPitch}");
                }
                CopyPixels(mapped, inter, outRect, origin, dst, dstStride, hdrLut);
            }
            finally
            {
                // ID3D11DeviceContext::Unmap（槽 15）
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Vt(slot.Context, 15))(
                    slot.Context, staging, 0);
            }
            return true;
        }
        finally
        {
            Release(ref staging);
            Release(ref tex);
            Release(ref resource);
            if (frameHeld && dup != IntPtr.Zero)
                // IDXGIOutputDuplication::ReleaseFrame（槽 14）——须在 Map 读完之后
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vt(dup, 14))(dup);
            Release(ref dup);
            Release(ref output1);
        }
    }

    /// <summary>把映射的桌面图像中 inter 区域写入目标（BGRA8 直拷 / FP16 查表色调映射）。</summary>
    private static void CopyPixels(D3D11_MAPPED_SUBRESOURCE mapped, PixelRect inter, PixelRect outRect,
        PixelRect origin, byte* dst, int dstStride, byte[]? hdrLut)
    {
        int w = inter.Width, h = inter.Height;
        int srcX = inter.X - outRect.X, srcY = inter.Y - outRect.Y;
        var dstBase = dst + (long)(inter.Y - origin.Y) * dstStride + (long)(inter.X - origin.X) * 4;
        if (hdrLut == null)
        {
            var srcBase = (byte*)mapped.pData + (long)srcY * mapped.RowPitch + (long)srcX * 4;
            for (var y = 0; y < h; y++)
            {
                var s = (uint*)(srcBase + (long)y * mapped.RowPitch);
                var d2 = (uint*)(dstBase + (long)y * dstStride);
                // 桌面图像 alpha 不可靠，与 BitBlt 路径同样强制不透明
                for (var x = 0; x < w; x++)
                    d2[x] = s[x] | 0xFF000000u;
            }
        }
        else
        {
            fixed (byte* lut = hdrLut)
            {
                var srcBase = (byte*)mapped.pData + (long)srcY * mapped.RowPitch + (long)srcX * 8;
                for (var y = 0; y < h; y++)
                {
                    var s = (ushort*)(srcBase + (long)y * mapped.RowPitch);
                    var d2 = (byte*)(dstBase + (long)y * dstStride);
                    for (var x = 0; x < w; x++)
                    {
                        // FP16 scRGB：R16G16B16A16 → BGRA8
                        d2[x * 4 + 0] = lut[s[x * 4 + 2]];
                        d2[x * 4 + 1] = lut[s[x * 4 + 1]];
                        d2[x * 4 + 2] = lut[s[x * 4 + 0]];
                        d2[x * 4 + 3] = 0xFF;
                    }
                }
            }
        }
    }

    /// <summary>
    /// scRGB half → sRGB 字节查找表（65536 项，按显示器 + 白电平缓存）。
    /// SDR 白电平把"HDR 桌面上的 SDR 白"（如 240 nits = 线性 3.0）归一回 1.0
    /// 再做 sRGB 编码——与 SDR 显示器上看到的观感一致，解决 GDI 抓 HDR 洗白。
    /// </summary>
    private byte[] GetHdrLut(string deviceName)
    {
        var white = SdrWhiteLevel.GetMilli(deviceName); // 千分数：1000 = 80 nits
        var key = deviceName + "|" + white;
        if (_hdrLuts.TryGetValue(key, out var cached))
            return cached;
        var lut = new byte[65536];
        var scale = 1000f / white;
        for (var i = 0; i < 65536; i++)
        {
            var linear = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            if (float.IsNaN(linear))
                continue; // 0
            linear = Math.Clamp(linear * scale, 0f, 1f);
            var c = linear <= 0.0031308f
                ? linear * 12.92f
                : MathF.Pow(linear, 1f / 2.4f) * 1.055f - 0.055f;
            lut[i] = (byte)(c * 255f + 0.5f);
        }
        _hdrLuts[key] = lut;
        return lut;
    }

    public void Dispose()
    {
        foreach (var slot in _adapters)
        {
            var c = slot.Context;
            Release(ref c);
            var dev = slot.Device;
            Release(ref dev);
            var a = slot.Adapter;
            Release(ref a);
        }
        _adapters.Clear();
        Release(ref _factory);
        _hdrLuts.Clear();
    }

    // ---------- 矩形工具 ----------

    private static PixelRect Intersect(PixelRect a, PixelRect b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.Right, b.Right);
        var y2 = Math.Min(a.Bottom, b.Bottom);
        return x2 <= x1 || y2 <= y1 ? default : new PixelRect(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>从矩形集中减去 cut（拆成上/下/左/右至多四条）。</summary>
    private static List<PixelRect> Subtract(List<PixelRect> from, PixelRect cut)
    {
        var result = new List<PixelRect>();
        foreach (var r in from)
        {
            var i = Intersect(r, cut);
            if (i.Width <= 0 || i.Height <= 0)
            {
                result.Add(r);
                continue;
            }
            if (i.Y > r.Y)
                result.Add(new PixelRect(r.X, r.Y, r.Width, i.Y - r.Y));
            if (i.Bottom < r.Bottom)
                result.Add(new PixelRect(r.X, i.Bottom, r.Width, r.Bottom - i.Bottom));
            if (i.X > r.X)
                result.Add(new PixelRect(r.X, i.Y, i.X - r.X, i.Height));
            if (i.Right < r.Right)
                result.Add(new PixelRect(i.Right, i.Y, r.Right - i.Right, i.Height));
        }
        return result;
    }

    // ---------- COM 基础 ----------

    private static void* Vt(IntPtr obj, int slot) => (*(void***)obj)[slot];

    private static void Release(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero)
            return;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vt(obj, 2))(obj);
        obj = IntPtr.Zero;
    }

    private static int QueryInterface(IntPtr obj, Guid iid, out IntPtr result)
    {
        IntPtr r;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vt(obj, 0))(obj, &iid, &r);
        result = hr == 0 ? r : IntPtr.Zero;
        return hr;
    }

    private static bool AdapterHasOutput(IntPtr adapter)
    {
        IntPtr output;
        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vt(adapter, 7))(
                adapter, 0, &output) != 0)
            return false;
        Release(ref output);
        return true;
    }

    // ---------- DXGI / D3D11 声明 ----------

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const uint DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint DXGI_MODE_ROTATION_IDENTITY = 1;
    private const uint D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const uint D3D11_MAP_READ = 1;
    private const uint D3D11_SDK_VERSION = 7;
    private const int D3D_DRIVER_TYPE_UNKNOWN = 0;

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_OUTPUT_DESC
    {
        public fixed char DeviceName[32];
        public Win32.RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public Win32.POINT PointerPosition;
        public int PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    // ---------- SDR 白电平（HDR 色调映射用） ----------

    private static class SdrWhiteLevel
    {
        /// <summary>
        /// 查询 GDI 设备名（\\.\DISPLAYn）对应显示器的 SDR 白电平（千分数，
        /// 1000 = 80 nits）。查询失败按 240 nits（Windows HDR 默认亮度附近）。
        /// </summary>
        public static uint GetMilli(string gdiDeviceName)
        {
            try
            {
                const uint QDC_ONLY_ACTIVE_PATHS = 2;
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var nPath, out var nMode) != 0)
                    return 3000;
                var paths = new DISPLAYCONFIG_PATH_INFO[nPath];
                var modes = new DISPLAYCONFIG_MODE_INFO[nMode];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref nPath, paths, ref nMode, modes,
                        IntPtr.Zero) != 0)
                    return 3000;
                for (var i = 0; i < nPath; i++)
                {
                    var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    src.header.type = 1; // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                    src.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
                    src.header.adapterId = paths[i].sourceAdapterId;
                    src.header.id = paths[i].sourceId;
                    if (DisplayConfigGetDeviceInfo(&src) != 0)
                        continue;
                    if (!string.Equals(new string((char*)src.viewGdiDeviceName), gdiDeviceName,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    var sdr = new DISPLAYCONFIG_SDR_WHITE_LEVEL();
                    sdr.header.type = 11; // DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL
                    sdr.header.size = (uint)sizeof(DISPLAYCONFIG_SDR_WHITE_LEVEL);
                    sdr.header.adapterId = paths[i].targetAdapterId;
                    sdr.header.id = paths[i].targetId;
                    if (DisplayConfigGetDeviceInfo(&sdr) == 0 && sdr.SDRWhiteLevel > 0)
                        return sdr.SDRWhiteLevel;
                    break;
                }
            }
            catch
            {
                // 落到默认值
            }
            return 3000;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint Low;
            public int High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public LUID sourceAdapterId;
            public uint sourceId;
            public uint sourceModeInfoIdx;
            public uint sourceStatusFlags;
            public LUID targetAdapterId;
            public uint targetId;
            public uint targetModeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public uint refreshNum;
            public uint refreshDen;
            public uint scanLineOrdering;
            public uint targetAvailable;
            public uint targetStatusFlags;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public fixed byte data[64];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public fixed char viewGdiDeviceName[32];
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint SDRWhiteLevel;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPaths,
            [In, Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint numModes,
            [In, Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(void* packet);
    }
}
