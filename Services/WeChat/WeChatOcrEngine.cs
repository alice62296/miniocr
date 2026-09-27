using SkiaSharp;

namespace MiniOcr.Services;

/// <summary>
/// Pool of WeChat OCR subprocesses. Page bitmaps are written to a temp PNG
/// because the plugin reads a filesystem path (UTF-8, absolute).
/// </summary>
public sealed class WeChatOcrEngine : IAsyncDisposable
{
    private readonly WeChatOcrSession[] _sessions;
    private readonly WeChatOcrPool _pool;
    private readonly string _tempDir;
    private readonly WeChatOcrLocation _location;

    private WeChatOcrEngine(WeChatOcrSession[] sessions, WeChatOcrLocation location, string tempDir)
    {
        _sessions = sessions;
        _location = location;
        _tempDir = tempDir;
        _pool = new WeChatOcrPool(sessions);
    }

    public bool IsReady { get; private set; } = true;
    public int InstanceCount => _sessions.Length;
    public string PluginPath => _location.PluginPath;
    public string WeChatDir => _location.WeChatDir;
    public string LaunchExe => _location.LaunchExe;
    public string MmmojoPath => _location.MmmojoPath;
    public WeChatOcrKind Kind => _location.Kind;
    public string KindName => _location.KindName;

    public static async Task<WeChatOcrEngine> ConnectAsync(
        WeChatOcrLocation location,
        OcrRuntimeConfig config,
        ILogger logger,
        CancellationToken cancellationToken,
        int? instanceCount = null)
    {
        if (!location.Found)
            throw new InvalidOperationException("WeChat OCR plugin was not found." + Environment.NewLine + location.Report);

        int count = Math.Clamp(instanceCount ?? config.WeChatInstances, 1, 8);
        string temp = Path.Combine(Path.GetTempPath(), "MiniOcr", "wechat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        var sessions = new WeChatOcrSession[count];
        try
        {
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sessions[i] = new WeChatOcrSession(location, config, logger, i);
                await sessions[i].ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                if (sessions[i] is not null)
                    await sessions[i].DisposeAsync().ConfigureAwait(false);
            }

            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }

            throw;
        }

        logger.LogInformation(
            "WeChat OCR ready: kind={Kind} instances={Instances} (one in-flight request per process) plugin={Plugin} dir={Dir}",
            location.KindName,
            count,
            location.PluginPath,
            location.WeChatDir);

        return new WeChatOcrEngine(sessions, location, temp) { IsReady = true };
    }

    public async Task<string> RecognizeBitmapAsync(SKBitmap bitmap, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".png");
        WritePng(bitmap, path);
        try
        {
            return await _pool.RecognizeAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        IsReady = false;
        foreach (WeChatOcrSession session in _sessions)
            await session.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void WritePng(SKBitmap bitmap, string path)
    {
        using SKData? encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        if (encoded is not null)
        {
            using FileStream stream = File.Create(path);
            encoded.SaveTo(stream);
            return;
        }

        using SKBitmap bgra = bitmap.Copy(SKColorType.Bgra8888)
            ?? throw new InvalidOperationException("Failed to convert a page bitmap to BGRA for WeChat OCR.");
        using SKData data = bgra.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Failed to encode a page PNG for WeChat OCR.");
        using FileStream fallback = File.Create(path);
        data.SaveTo(fallback);
    }
}
