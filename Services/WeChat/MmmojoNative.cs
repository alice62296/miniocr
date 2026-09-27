using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MiniOcr.Services;

/// <summary>
/// Dynamic load of <c>mmmojo_64.dll</c> from the WeChat install directory.
/// No <c>DllImport</c>: a direct P/Invoke would be baked into every RID, including Linux AOT.
/// Calling convention matches the exports declared in swigger/wechat-ocr <c>mmmojo_funcs.h</c>.
/// </summary>
internal unsafe sealed class MmmojoApi
{
    public required string DllPath { get; init; }
    public required delegate* unmanaged[Cdecl]<nint> CreateEnvironment { get; init; }
    public required delegate* unmanaged[Cdecl]<nint, int, nint, void> SetCallbacks { get; init; }
    public required delegate* unmanaged[Cdecl]<nint, int, int, void> SetInitInt { get; init; }
    public required delegate* unmanaged[Cdecl]<nint, int, nint, void> SetInitPtr { get; init; }
    public required delegate* unmanaged[Cdecl]<nint, nint, nint, void> AppendSwitch { get; init; }
    public required delegate* unmanaged[Cdecl]<nint, void> Start { get; init; }
    public required delegate* unmanaged[Cdecl]<nint, void> Stop { get; init; }
    public required delegate* unmanaged[Cdecl]<nint, void> RemoveEnvironment { get; init; }
    public required delegate* unmanaged[Cdecl]<int, byte, uint, nint> CreateWrite { get; init; }
    public required delegate* unmanaged[Cdecl]<nint, nuint, nint> GetWriteRequest { get; init; }
    public required delegate* unmanaged[Cdecl]<nint, nint, byte> Send { get; init; }
    public required delegate* unmanaged[Cdecl]<nint, void> RemoveWrite { get; init; }
    public required delegate* unmanaged[Cdecl]<nint, uint*, nint> GetReadRequest { get; init; }
    public required delegate* unmanaged[Cdecl]<nint, void> RemoveRead { get; init; }

    public const int CallbackUserData = 0;
    public const int CallbackReadPush = 1;
    public const int CallbackReadPull = 2;
    public const int CallbackReadShared = 3;
    public const int CallbackRemoteConnect = 4;
    public const int CallbackRemoteDisconnect = 5;
    public const int CallbackProcessLaunched = 6;
    public const int CallbackLaunchFailed = 7;
    public const int CallbackMojoError = 8;

    public const int InitHostProcess = 0;
    public const int InitExePath = 2;
}

internal static class MmmojoNative
{
    private static readonly object Gate = new();
    private static MmmojoApi? _api;

    public static MmmojoApi? Current
    {
        get
        {
            lock (Gate)
                return _api;
        }
    }

    public static MmmojoApi GetOrLoad(string wechatDir)
    {
        lock (Gate)
        {
            _api ??= Load(wechatDir);
            return _api;
        }
    }

    private static unsafe MmmojoApi Load(string wechatDir)
    {
        string dll = Path.Combine(wechatDir, "mmmojo_64.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException(
                $"mmmojo_64.dll was not found in the WeChat directory '{wechatDir}'. " +
                "Point ocr.wechatDir at the version folder (the one that contains mmmojo_64.dll).",
                dll);
        }

        SetDllDirectory(wechatDir);
        nint module;
        try
        {
            module = NativeLibrary.Load(dll);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to load '{dll}'. WeChat OCR needs the 64-bit mmmojo that ships with WeChat. {ex.Message}",
                ex);
        }
        finally
        {
            SetDllDirectory(null);
        }

        var api = new MmmojoApi
        {
            DllPath = dll,
            CreateEnvironment = (delegate* unmanaged[Cdecl]<nint>)Export(module, "CreateMMMojoEnvironment"),
            SetCallbacks = (delegate* unmanaged[Cdecl]<nint, int, nint, void>)Export(module, "SetMMMojoEnvironmentCallbacks"),
            SetInitInt = (delegate* unmanaged[Cdecl]<nint, int, int, void>)Export(module, "SetMMMojoEnvironmentInitParams"),
            SetInitPtr = (delegate* unmanaged[Cdecl]<nint, int, nint, void>)Export(module, "SetMMMojoEnvironmentInitParams"),
            AppendSwitch = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)Export(module, "AppendMMSubProcessSwitchNative"),
            Start = (delegate* unmanaged[Cdecl]<nint, void>)Export(module, "StartMMMojoEnvironment"),
            Stop = (delegate* unmanaged[Cdecl]<nint, void>)Export(module, "StopMMMojoEnvironment"),
            RemoveEnvironment = (delegate* unmanaged[Cdecl]<nint, void>)Export(module, "RemoveMMMojoEnvironment"),
            CreateWrite = (delegate* unmanaged[Cdecl]<int, byte, uint, nint>)Export(module, "CreateMMMojoWriteInfo"),
            GetWriteRequest = (delegate* unmanaged[Cdecl]<nint, nuint, nint>)Export(module, "GetMMMojoWriteInfoRequest"),
            Send = (delegate* unmanaged[Cdecl]<nint, nint, byte>)Export(module, "SendMMMojoWriteInfo"),
            RemoveWrite = (delegate* unmanaged[Cdecl]<nint, void>)Export(module, "RemoveMMMojoWriteInfo"),
            GetReadRequest = (delegate* unmanaged[Cdecl]<nint, uint*, nint>)Export(module, "GetMMMojoReadInfoRequest"),
            RemoveRead = (delegate* unmanaged[Cdecl]<nint, void>)Export(module, "RemoveMMMojoReadInfo"),
        };

        // Once per process. argc=0, argv=null, same as the upstream client.
        var init = (delegate* unmanaged[Cdecl]<int, nint, void>)Export(module, "InitializeMMMojo");
        init(0, 0);
        return api;
    }

    private static nint Export(nint module, string name)
    {
        if (!NativeLibrary.TryGetExport(module, name, out nint ptr) || ptr == 0)
        {
            throw new InvalidOperationException(
                $"mmmojo_64.dll is missing the export '{name}'. The WeChat install may be too old or not x64.");
        }

        return ptr;
    }

    private static unsafe void SetDllDirectory(string? directory)
    {
        if (!OperatingSystem.IsWindows())
            return;
        nint kernel = NativeLibrary.Load("kernel32");
        nint proc = NativeLibrary.GetExport(kernel, "SetDllDirectoryW");
        var fn = (delegate* unmanaged[Cdecl]<nint, int>)proc;
        if (directory is null)
        {
            fn(0);
            return;
        }

        nint buffer = Marshal.StringToHGlobalUni(directory);
        try
        {
            fn(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>
/// Reverse P/Invoke entry points. <c>userData</c> is a session id, not a managed pointer.
/// </summary>
internal static class MmmojoCallbacks
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void ReadOnPush(uint requestId, nint requestInfo, nint userData)
    {
        byte[]? payload = CopyAndRelease(requestInfo);
        Dispatch(userData, session => session.HandlePush(requestId, payload));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void ReadOnPull(uint requestId, nint requestInfo, nint userData)
    {
        CopyAndRelease(requestInfo);
        _ = requestId;
        _ = userData;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void ReadOnShared(uint requestId, nint requestInfo, nint userData)
    {
        CopyAndRelease(requestInfo);
        _ = requestId;
        _ = userData;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnConnect(byte connected, nint userData) =>
        Dispatch(userData, session => session.HandleConnect(connected != 0));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnDisconnect(nint userData) =>
        Dispatch(userData, session => session.HandleDisconnect());

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnLaunched(nint userData)
    {
        _ = userData;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnLaunchFailed(int errorCode, nint userData) =>
        Dispatch(userData, session => session.HandleLaunchFailed(errorCode));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnError(nint buffer, int size, nint userData)
    {
        string message = "";
        if (buffer != 0 && size > 0)
        {
            int n = Math.Min(size, 1024);
            byte[] bytes = new byte[n];
            Marshal.Copy(buffer, bytes, 0, n);
            message = System.Text.Encoding.UTF8.GetString(bytes);
        }

        string copy = message;
        Dispatch(userData, session => session.HandleRemoteError(copy));
    }

    private static unsafe byte[]? CopyAndRelease(nint requestInfo)
    {
        MmmojoApi? api = MmmojoNative.Current;
        if (api is null || requestInfo == 0)
            return null;

        byte[]? payload = null;
        try
        {
            uint size = 0;
            unsafe
            {
                nint ptr = api.GetReadRequest(requestInfo, &size);
                if (ptr != 0 && size is > 0 and <= 32 * 1024 * 1024)
                {
                    payload = new byte[size];
                    Marshal.Copy(ptr, payload, 0, (int)size);
                }
            }
        }
        catch
        {
            payload = null;
        }
        finally
        {
            try
            {
                api.RemoveRead(requestInfo);
            }
            catch
            {
                // The native side owns this object; leaking one info is better than crashing the callback.
            }
        }

        return payload;
    }

    private static void Dispatch(nint userData, Action<WeChatOcrSession> action)
    {
        try
        {
            if (!WeChatOcrSession.TryGet(userData, out WeChatOcrSession? session) || session is null)
                return;
            action(session);
        }
        catch
        {
            // Never throw out of a native callback.
        }
    }
}
