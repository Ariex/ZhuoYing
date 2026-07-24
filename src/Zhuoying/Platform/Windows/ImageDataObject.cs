using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Zhuoying.Platform.Windows;

/// <summary>
/// 提供 CF_DIB + PNG 两种 HGLOBAL 格式的最小 IDataObject 实现，
/// 供 OleSetClipboard 使用（与 Qt 系截屏工具相同的剪贴板写入路径）。
/// </summary>
internal sealed class ImageDataObject : System.Runtime.InteropServices.ComTypes.IDataObject
{
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int DV_E_TYMED = unchecked((int)0x80040069);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int S_OK = 0;

    private readonly (short Format, byte[] Data)[] _entries;

    public ImageDataObject(params (short Format, byte[] Data)[] entries) => _entries = entries;

    private int Find(short format)
    {
        for (var i = 0; i < _entries.Length; i++)
            if (_entries[i].Format == format)
                return i;
        return -1;
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        var index = Find(format.cfFormat);
        if (index < 0)
            throw new COMException("不支持的剪贴板格式", DV_E_FORMATETC);
        if ((format.tymed & TYMED.TYMED_HGLOBAL) == 0)
            throw new COMException("仅支持 TYMED_HGLOBAL", DV_E_TYMED);

        var data = _entries[index].Data;
        var hGlobal = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (nuint)data.Length);
        if (hGlobal == IntPtr.Zero)
            throw new OutOfMemoryException();
        var ptr = Win32.GlobalLock(hGlobal);
        Marshal.Copy(data, 0, ptr, data.Length);
        Win32.GlobalUnlock(hGlobal);

        medium = new STGMEDIUM
        {
            tymed = TYMED.TYMED_HGLOBAL,
            unionmember = hGlobal,
            pUnkForRelease = null,
        };
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
        throw new COMException("未实现", DV_E_FORMATETC);

    public int QueryGetData(ref FORMATETC format)
    {
        if (Find(format.cfFormat) < 0)
            return DV_E_FORMATETC;
        return (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? S_OK : DV_E_TYMED;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return 0x00040130; // DATA_S_SAMEFORMATETC
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) =>
        throw new COMException("只读数据对象", DV_E_FORMATETC);

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
            throw new COMException("仅支持 DATADIR_GET", unchecked((int)0x80004001) /* E_NOTIMPL */);
        var formats = new FORMATETC[_entries.Length];
        for (var i = 0; i < _entries.Length; i++)
            formats[i] = new FORMATETC
            {
                cfFormat = _entries[i].Format,
                ptd = IntPtr.Zero,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL,
            };
        return new FormatEtcEnumerator(formats);
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) =>
        throw new COMException("不支持通知", OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    private sealed class FormatEtcEnumerator : IEnumFORMATETC
    {
        private readonly FORMATETC[] _formats;
        private int _position;

        public FormatEtcEnumerator(FORMATETC[] formats) => _formats = formats;

        public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
        {
            var fetched = 0;
            while (fetched < celt && _position < _formats.Length)
                rgelt[fetched++] = _formats[_position++];
            if (pceltFetched is { Length: > 0 })
                pceltFetched[0] = fetched;
            return fetched == celt ? 0 : 1; // S_OK / S_FALSE
        }

        public int Skip(int celt)
        {
            _position = Math.Min(_position + celt, _formats.Length);
            return _position < _formats.Length ? 0 : 1;
        }

        public int Reset()
        {
            _position = 0;
            return 0;
        }

        public void Clone(out IEnumFORMATETC newEnum) =>
            newEnum = new FormatEtcEnumerator(_formats) { _position = _position };
    }
}
