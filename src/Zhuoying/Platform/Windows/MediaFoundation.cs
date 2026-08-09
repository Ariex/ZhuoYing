using System;
using System.Runtime.InteropServices;

namespace Zhuoying.Platform.Windows;

/// <summary>
/// Media Foundation 手写互操作（MP4 录屏用，方式同 DesktopDuplication：
/// vtable 函数指针调用，NativeAOT 兼容零依赖）。
///
/// 编码：SinkWriter 输出 H.264/MP4，输入 RGB32（顶朝下，MF_MT_DEFAULT_STRIDE
/// 正值声明），颜色转换与编码器（优先硬件）由 SinkWriter 自动插入。
/// 解码（自测用）：SourceReader 转 RGB32 逐帧读回采样像素。
/// 注意 Windows N 版本无媒体功能包时 MFStartup 失败，调用方需给出提示。
/// </summary>
internal static unsafe class MediaFoundation
{
    // ---------- 导出函数 ----------

    private const uint MF_VERSION = 0x0002_0070;
    private const uint MFSTARTUP_LITE = 0x1;

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMediaType(out IntPtr type);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateSample(out IntPtr sample);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IntPtr attributes,
        out IntPtr writer);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(
        [MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr attributes, out IntPtr reader);

    public static void Startup() => Check(MFStartup(MF_VERSION, MFSTARTUP_LITE), "MFStartup");

    public static void Shutdown() => MFShutdown();

    // ---------- GUID ----------

    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS =
        new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING =
        new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    public const uint MFVideoInterlace_Progressive = 2;
    public const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;
    public const uint MF_SOURCE_READERF_ENDOFSTREAM = 0x2;

    // ---------- COM 基础 ----------

    public static void* Vt(IntPtr obj, int slot) => (*(void***)obj)[slot];

    public static void Release(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero)
            return;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vt(obj, 2))(obj);
        obj = IntPtr.Zero;
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} 失败 0x{hr:X8}");
    }

    // ---------- IMFAttributes（IMFMediaType 继承其全部槽位） ----------

    /// <summary>IMFAttributes::SetUINT32（槽 21）。</summary>
    public static void SetU32(IntPtr attrs, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Vt(attrs, 21))(
            attrs, &key, value), "SetUINT32");

    /// <summary>IMFAttributes::SetUINT64（槽 22）。</summary>
    public static void SetU64(IntPtr attrs, Guid key, ulong value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Vt(attrs, 22))(
            attrs, &key, value), "SetUINT64");

    /// <summary>IMFAttributes::SetGUID（槽 24）。</summary>
    public static void SetGuid(IntPtr attrs, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Vt(attrs, 24))(
            attrs, &key, &value), "SetGUID");

    /// <summary>IMFAttributes::GetUINT32（槽 7）。失败返回 false。</summary>
    public static bool TryGetU32(IntPtr attrs, Guid key, out uint value)
    {
        uint v;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Vt(attrs, 7))(
            attrs, &key, &v);
        value = v;
        return hr == 0;
    }

    /// <summary>IMFAttributes::GetUINT64（槽 8）。</summary>
    public static bool TryGetU64(IntPtr attrs, Guid key, out ulong value)
    {
        ulong v;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Vt(attrs, 8))(
            attrs, &key, &v);
        value = v;
        return hr == 0;
    }

    /// <summary>构造视频媒体类型（RGB32 输入 / H.264 输出共用的公共字段）。</summary>
    public static IntPtr CreateVideoType(Guid subtype, int width, int height, int fps)
    {
        Check(MFCreateMediaType(out var type), "MFCreateMediaType");
        SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Video);
        SetGuid(type, MF_MT_SUBTYPE, subtype);
        SetU64(type, MF_MT_FRAME_SIZE, ((ulong)(uint)width << 32) | (uint)height);
        SetU64(type, MF_MT_FRAME_RATE, ((ulong)(uint)fps << 32) | 1);
        SetU64(type, MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);
        SetU32(type, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        return type;
    }

    // ---------- IMFSinkWriter ----------

    /// <summary>IMFSinkWriter::AddStream（槽 3）。</summary>
    public static uint AddStream(IntPtr writer, IntPtr mediaType)
    {
        uint index;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Vt(writer, 3))(
            writer, mediaType, &index), "AddStream");
        return index;
    }

    /// <summary>IMFSinkWriter::SetInputMediaType（槽 4）。</summary>
    public static void SetInputMediaType(IntPtr writer, uint stream, IntPtr mediaType) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Vt(writer, 4))(
            writer, stream, mediaType, IntPtr.Zero), "SetInputMediaType");

    /// <summary>IMFSinkWriter::BeginWriting（槽 5）。</summary>
    public static void BeginWriting(IntPtr writer) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vt(writer, 5))(writer), "BeginWriting");

    /// <summary>IMFSinkWriter::WriteSample（槽 6）。</summary>
    public static void WriteSample(IntPtr writer, uint stream, IntPtr sample) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Vt(writer, 6))(
            writer, stream, sample), "WriteSample");

    /// <summary>IMFSinkWriter::Finalize（槽 11）。</summary>
    public static void FinalizeWriter(IntPtr writer) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vt(writer, 11))(writer), "Finalize");

    // ---------- IMFSample / IMFMediaBuffer ----------

    /// <summary>把 top-down BGRA 帧打包为带时间戳的样本（内部拷贝，帧缓冲可复用）。</summary>
    public static IntPtr CreateFrameSample(IntPtr bits, int byteLength, long timeNs100, long durationNs100)
    {
        Check(MFCreateMemoryBuffer((uint)byteLength, out var buffer), "MFCreateMemoryBuffer");
        try
        {
            byte* data;
            uint max, cur;
            // IMFMediaBuffer::Lock（槽 3）
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Vt(buffer, 3))(
                buffer, &data, &max, &cur), "IMFMediaBuffer.Lock");
            new ReadOnlySpan<byte>((void*)bits, byteLength).CopyTo(new Span<byte>(data, byteLength));
            // Unlock（槽 4）
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vt(buffer, 4))(buffer);
            // SetCurrentLength（槽 6）
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Vt(buffer, 6))(
                buffer, (uint)byteLength), "SetCurrentLength");

            Check(MFCreateSample(out var sample), "MFCreateSample");
            // IMFSample::AddBuffer（槽 42）
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vt(sample, 42))(
                sample, buffer), "AddBuffer");
            // SetSampleTime（槽 36）/ SetSampleDuration（槽 38）
            Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Vt(sample, 36))(
                sample, timeNs100), "SetSampleTime");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Vt(sample, 38))(
                sample, durationNs100), "SetSampleDuration");
            return sample;
        }
        finally
        {
            Release(ref buffer); // 样本持有自己的引用
        }
    }

    // ---------- IMFSourceReader（自测解码） ----------

    /// <summary>IMFSourceReader::SetCurrentMediaType（槽 7）。</summary>
    public static void ReaderSetMediaType(IntPtr reader, uint stream, IntPtr mediaType) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Vt(reader, 7))(
            reader, stream, IntPtr.Zero, mediaType), "Reader.SetCurrentMediaType");

    /// <summary>IMFSourceReader::GetCurrentMediaType（槽 6）。</summary>
    public static IntPtr ReaderGetMediaType(IntPtr reader, uint stream)
    {
        IntPtr type;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vt(reader, 6))(
            reader, stream, &type), "Reader.GetCurrentMediaType");
        return type;
    }

    /// <summary>IMFSourceReader::ReadSample（槽 9，同步模式）。返回 sample 可为空（gap）。</summary>
    public static int ReadSample(IntPtr reader, uint stream, out uint streamFlags, out IntPtr sample)
    {
        uint actual, flags;
        long ts;
        IntPtr s;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)
            Vt(reader, 9))(reader, stream, 0, &actual, &flags, &ts, &s);
        streamFlags = flags;
        sample = s;
        return hr;
    }

    /// <summary>IMFSample::ConvertToContiguousBuffer（槽 41）+ Lock，读出帧字节。</summary>
    public static byte[] SampleToBytes(IntPtr sample)
    {
        IntPtr buffer;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vt(sample, 41))(
            sample, &buffer), "ConvertToContiguousBuffer");
        try
        {
            byte* data;
            uint max, cur;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Vt(buffer, 3))(
                buffer, &data, &max, &cur), "IMFMediaBuffer.Lock");
            var bytes = new byte[cur];
            new ReadOnlySpan<byte>(data, (int)cur).CopyTo(bytes);
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vt(buffer, 4))(buffer);
            return bytes;
        }
        finally
        {
            Release(ref buffer);
        }
    }
}
