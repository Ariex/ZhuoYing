using Avalonia.Media.Imaging;

namespace Zhuoying.Platform;

public interface IClipboardImage
{
    /// <summary>把位图以 CF_DIB + PNG 两种格式写入系统剪贴板。</summary>
    void SetImage(WriteableBitmap bitmap);
}
