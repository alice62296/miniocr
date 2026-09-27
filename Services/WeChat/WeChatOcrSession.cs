using System.Collections.Concurrent;
using System.Text;

namespace MiniOcr.Services;

/// <summary>
/// One mmmojo environment and therefore one WeChatOCR subprocess.
/// Requests are serialized: a single plugin process is treated as single-threaded.
/// Several sessions (several processes) are how <see cref="WeChatOcrEngine"/> gets concurrency.
/// </summary>
public sealed class WeChatOcrSession : IWeChatOcrBackend, IAsyncDisposable
{
    private static readonly ConcurrentDictionary<long, WeChatOcrSession> Live = new();
    private static long _nextId;

    private readonly long _id;
    private readonly WeChatOcrLocation _location;
    private readonly ILogger _logger;
    private readonly int _connectTimeoutSeconds;
    private readonly int _requestTimeoutSeconds;
    private readonly List<nint> _nativeStrings = [];
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<WeChatPush.Result>> _pending = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly int _ordinal;

    private MmmojoApi? _api;
    private nint _env;
    private int _taskSeq = 1;
    private int _disposed;

    public WeChatOcrSession(
        WeChatOcrLocation location,
        OcrRuntimeConfig config,
        ILogger logger,
        int ordinal)
    {
        _location = location;
        _logger = logger;
        _ordinal = ordinal;
        _connectTimeoutSeconds = config.WeChatConnectTimeoutSeconds;
        _requestTimeoutSeconds = config.WeChatRequestTimeoutSeconds;
        _id = Interlocked.Increment(ref _nextId);
        Live[_id] = this;
    }

    internal static bool TryGet(nint id, out WeChatOcrSession? session) =>
        Live.TryGetValue(id, out session);

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (!_location.Found)
            throw new InvalidOperationException("WeChat OCR location is incomplete.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WeChat OCR mmmojo client runs on Windows x64 only.");

        _api = MmmojoNative.GetOrLoad(_location.WeChatDir);
        AttachEnvironment();

        try
        {
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(_connectTimeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(Describe(
                $"did not finish handshake within {_connectTimeoutSeconds}s. " +
                "Check that WeChat is installed and the plugin matches the install (3.9 exe vs 4.x wxocr.dll)."));
        }

        _logger.LogInformation(
            "WeChat OCR session {Ordinal} connected ({Kind})",
            _ordinal + 1,
            _location.KindName);
    }

    public async Task<string> RecognizeAsync(string absoluteImagePath, CancellationToken cancellationToken)
    {
        if (_api is null || _env == 0 || !_ready.Task.IsCompletedSuccessfully)
            throw new InvalidOperationException("WeChat OCR session is not connected.");

        string full = Path.GetFullPath(absoluteImagePath);
        if (!File.Exists(full))
            throw new FileNotFoundException("WeChat OCR image does not exist: " + full, full);

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool release = true;
        ulong taskId = 0;
        try
        {
            taskId = NextTaskId();
            var tcs = new TaskCompletionSource<WeChatPush.Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[taskId] = tcs;

            byte[] body = WeChatProtobuf.EncodeRequest(_location.Kind, taskId, WindowsPath(full));
            uint mojoId = _location.Kind == WeChatOcrKind.Wx4 ? WeChatProtobuf.Wx4Request : WeChatProtobuf.Wx3Push;
            Send(body, mojoId);

            Task finished = await Task.WhenAny(
                    tcs.Task,
                    Task.Delay(TimeSpan.FromSeconds(_requestTimeoutSeconds), cancellationToken))
                .ConfigureAwait(false);
            if (finished != tcs.Task)
            {
                // Keep the process single-flight until the late result arrives.
                release = false;
                ulong lateId = taskId;
                _ = ObserveLateAsync(tcs, lateId);
                throw new TimeoutException(Describe(
                    $"timed out after {_requestTimeoutSeconds}s on '{full}'."));
            }

            WeChatPush.Result result = await tcs.Task.ConfigureAwait(false);
            _pending.TryRemove(taskId, out _);
            if (result.ErrCode != 0 && result.Lines.Count == 0)
            {
                throw new InvalidOperationException(Describe(
                    $"err_code={result.ErrCode} for '{full}'."));
            }

            if (result.ErrCode != 0)
            {
                _logger.LogWarning(
                    "WeChat OCR err_code={Code} but returned {Lines} line(s) for {Path}",
                    result.ErrCode,
                    result.Lines.Count,
                    full);
            }

            return WeChatProtobuf.JoinLines(result.Lines);
        }
        finally
        {
            if (release)
                _oneAtATime.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Live.TryRemove(_id, out _);
        FailPending("WeChat OCR session disposed.");
        MmmojoApi? api = _api;
        nint env = _env;
        _env = 0;
        if (api is not null && env != 0)
            StopEnvironment(api, env);

        foreach (nint ptr in _nativeStrings)
        {
            if (ptr != 0)
                System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
        }

        _nativeStrings.Clear();
        _oneAtATime.Dispose();
        await Task.CompletedTask;
    }

    internal void HandlePush(uint requestId, byte[]? payload)
    {
        if (payload is null || payload.Length == 0)
            return;
        WeChatPush push = WeChatProtobuf.ParsePush(_location.Kind, requestId, payload);
        switch (push)
        {
            case WeChatPush.Handshake handshake:
                if (handshake.Ok)
                    _ready.TrySetResult(true);
                else
                    Fail(Describe($"handshake failed (err_code={handshake.ErrCode})."));
                break;
            case WeChatPush.Result result:
                if (_pending.TryRemove(result.TaskId, out TaskCompletionSource<WeChatPush.Result>? tcs))
                    tcs.TrySetResult(result);
                break;
        }
    }

    internal void HandleConnect(bool connected)
    {
        if (!connected)
            Fail(Describe("remote side reported not connected."));
    }

    internal void HandleDisconnect()
    {
        if (!_ready.Task.IsCompleted)
            Fail(Describe("process disconnected before handshake."));
        else
            FailPending(Describe("process disconnected."));
    }

    internal void HandleLaunchFailed(int errorCode) =>
        Fail(Describe($"process failed to launch (Win32 {errorCode}). launch={_location.LaunchExe}"));

    internal void HandleRemoteError(string message) =>
        Fail(Describe("mmmojo error: " + message));

    private async Task ObserveLateAsync(TaskCompletionSource<WeChatPush.Result> tcs, ulong taskId)
    {
        try
        {
            await tcs.Task.WaitAsync(TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        }
        catch
        {
            // The caller already timed out.
        }
        finally
        {
            _pending.TryRemove(taskId, out _);
            try
            {
                _oneAtATime.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private unsafe void AttachEnvironment()
    {
        MmmojoApi api = _api ?? throw new InvalidOperationException("mmmojo is not loaded.");
        _env = api.CreateEnvironment();
        if (_env == 0)
            throw new InvalidOperationException("CreateMMMojoEnvironment returned null.");

        api.SetCallbacks(_env, MmmojoApi.CallbackUserData, (nint)_id);
        api.SetCallbacks(_env, MmmojoApi.CallbackReadPush, (nint)(delegate* unmanaged[Cdecl]<uint, nint, nint, void>)&MmmojoCallbacks.ReadOnPush);
        api.SetCallbacks(_env, MmmojoApi.CallbackReadPull, (nint)(delegate* unmanaged[Cdecl]<uint, nint, nint, void>)&MmmojoCallbacks.ReadOnPull);
        api.SetCallbacks(_env, MmmojoApi.CallbackReadShared, (nint)(delegate* unmanaged[Cdecl]<uint, nint, nint, void>)&MmmojoCallbacks.ReadOnShared);
        api.SetCallbacks(_env, MmmojoApi.CallbackRemoteConnect, (nint)(delegate* unmanaged[Cdecl]<byte, nint, void>)&MmmojoCallbacks.OnConnect);
        api.SetCallbacks(_env, MmmojoApi.CallbackRemoteDisconnect, (nint)(delegate* unmanaged[Cdecl]<nint, void>)&MmmojoCallbacks.OnDisconnect);
        api.SetCallbacks(_env, MmmojoApi.CallbackProcessLaunched, (nint)(delegate* unmanaged[Cdecl]<nint, void>)&MmmojoCallbacks.OnLaunched);
        api.SetCallbacks(_env, MmmojoApi.CallbackLaunchFailed, (nint)(delegate* unmanaged[Cdecl]<int, nint, void>)&MmmojoCallbacks.OnLaunchFailed);
        api.SetCallbacks(_env, MmmojoApi.CallbackMojoError, (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&MmmojoCallbacks.OnError);

        api.SetInitInt(_env, MmmojoApi.InitHostProcess, 1);
        api.SetInitPtr(_env, MmmojoApi.InitExePath, PinUtf16(WindowsPath(_location.LaunchExe)));

        Switch("no-sandbox", "");
        Switch("user-lib-dir", WindowsPath(_location.WeChatDir));
        if (_location.Kind == WeChatOcrKind.Wx4)
        {
            Switch("type", Path.GetFileNameWithoutExtension(_location.PluginPath));
            string? appPath = Path.GetDirectoryName(_location.PluginPath);
            if (string.IsNullOrEmpty(appPath))
                throw new InvalidOperationException($"wxocr.dll path has no directory: {_location.PluginPath}");
            Switch("app-path", WindowsPath(appPath));
        }

        _logger.LogInformation(
            "Starting WeChat OCR session {Ordinal} kind={Kind} launch={Launch} plugin={Plugin} dir={Dir}",
            _ordinal + 1,
            _location.KindName,
            _location.LaunchExe,
            _location.PluginPath,
            _location.WeChatDir);

        try
        {
            api.Start(_env);
        }
        catch (Exception ex)
        {
            Fail("StartMMMojoEnvironment threw: " + ex.Message);
            throw;
        }
    }

    private unsafe void StopEnvironment(MmmojoApi api, nint env)
    {
        try
        {
            api.Stop(env);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StopMMMojoEnvironment failed");
        }

        try
        {
            api.RemoveEnvironment(env);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RemoveMMMojoEnvironment failed");
        }
    }

    private unsafe void Send(byte[] body, uint requestId)
    {
        MmmojoApi api = _api ?? throw new InvalidOperationException("mmmojo is not loaded.");
        nint write = api.CreateWrite(WeChatProtobuf.MethodPush, 0, requestId);
        if (write == 0)
            throw new InvalidOperationException("CreateMMMojoWriteInfo returned null.");
        nint dest = api.GetWriteRequest(write, (nuint)body.Length);
        if (dest == 0)
        {
            api.RemoveWrite(write);
            throw new InvalidOperationException("GetMMMojoWriteInfoRequest returned null.");
        }

        System.Runtime.InteropServices.Marshal.Copy(body, 0, dest, body.Length);
        if (api.Send(_env, write) == 0)
            throw new InvalidOperationException("SendMMMojoWriteInfo returned false.");
    }

    private unsafe void Switch(string name, string value)
    {
        MmmojoApi api = _api ?? throw new InvalidOperationException("mmmojo is not loaded.");
        api.AppendSwitch(_env, PinUtf8(name), PinUtf16(value));
    }

    private ulong NextTaskId()
    {
        int next = Interlocked.Increment(ref _taskSeq);
        if (next < 2 || next == int.MaxValue)
        {
            Interlocked.Exchange(ref _taskSeq, 2);
            return 2;
        }

        return (ulong)next;
    }

    private void Fail(string message)
    {
        _ready.TrySetException(new InvalidOperationException(message));
        FailPending(message);
    }

    private void FailPending(string message)
    {
        foreach (KeyValuePair<ulong, TaskCompletionSource<WeChatPush.Result>> pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out TaskCompletionSource<WeChatPush.Result>? tcs))
                tcs.TrySetException(new InvalidOperationException(message));
        }
    }

    private string Describe(string detail) =>
        $"WeChat OCR ({_location.KindName}) {detail} plugin={_location.PluginPath} wechatDir={_location.WeChatDir} launch={_location.LaunchExe} mmmojo={_location.MmmojoPath}";

    private static string WindowsPath(string path) =>
        path.Replace('/', '\\');

    private nint PinUtf16(string value)
    {
        nint ptr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(value);
        _nativeStrings.Add(ptr);
        return ptr;
    }

    private nint PinUtf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        nint ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length + 1);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, ptr, bytes.Length);
        System.Runtime.InteropServices.Marshal.WriteByte(ptr, bytes.Length, 0);
        _nativeStrings.Add(ptr);
        return ptr;
    }

}
