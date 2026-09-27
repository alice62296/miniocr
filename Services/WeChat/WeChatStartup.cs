using System.Runtime.InteropServices;

namespace MiniOcr.Services;

public readonly record struct WeChatStartupDecision(
    bool Ready,
    bool FailProcess,
    string Message,
    WeChatOcrLocation? Location)
{
    public static WeChatStartupDecision Use(WeChatOcrLocation location) =>
        new(true, false, location.Report, location);

    public static WeChatStartupDecision Fallback(string message) =>
        new(false, false, message, null);

    public static WeChatStartupDecision Fail(string message) =>
        new(false, true, message, null);
}

public static class WeChatStartup
{
    public static bool IsWindowsX64() =>
        OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

    /// <summary>
    /// Host and detection check before any mmmojo call.
    /// <paramref name="fallbackToLocal"/> matches llm mode: warn and continue on local,
    /// or fail the process when the operator asked not to fall back.
    /// </summary>
    public static WeChatStartupDecision Decide(bool windowsX64, WeChatOcrLocation location, bool fallbackToLocal)
    {
        if (!windowsX64)
        {
            const string msg =
                "ocr.mode=wechat requires Windows x64 (WeChat 3.9 WeChatOCR.exe or WeChat 4.x wxocr.dll, plus mmmojo_64.dll). " +
                "This process is not Windows x64.";
            return fallbackToLocal
                ? WeChatStartupDecision.Fallback(msg)
                : WeChatStartupDecision.Fail(msg);
        }

        if (!location.Found)
        {
            string msg =
                "ocr.mode=wechat but the OCR plugin or WeChat install directory was not found." +
                Environment.NewLine + location.Report;
            return fallbackToLocal
                ? WeChatStartupDecision.Fallback(msg)
                : WeChatStartupDecision.Fail(msg);
        }

        return WeChatStartupDecision.Use(location);
    }
}
