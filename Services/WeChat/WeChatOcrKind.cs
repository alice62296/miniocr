namespace MiniOcr.Services;

/// <summary>Which WeChat OCR wire protocol a plugin binary speaks.</summary>
public enum WeChatOcrKind
{
    Unknown = 0,
    /// <summary>WeChat 3.9.x: <c>WeChatOCR.exe</c>, request id 1.</summary>
    Wx3 = 3,
    /// <summary>WeChat 4.x: <c>wxocr.dll</c> launched via <c>weixin.exe</c>.</summary>
    Wx4 = 4,
}
