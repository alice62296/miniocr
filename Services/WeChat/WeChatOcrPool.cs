using System.Collections.Concurrent;

namespace MiniOcr.Services;

/// <summary>One WeChat OCR process. Calls are serialized by <see cref="WeChatOcrPool"/>.</summary>
public interface IWeChatOcrBackend
{
    Task<string> RecognizeAsync(string absoluteImagePath, CancellationToken cancellationToken);
}

/// <summary>
/// Leases N backends. Each backend is expected to be a single-threaded WeChatOCR
/// process, so the pool never hands the same instance to two callers at once.
/// </summary>
public sealed class WeChatOcrPool
{
    private readonly IWeChatOcrBackend[] _backends;
    private readonly ConcurrentBag<IWeChatOcrBackend> _free;
    private readonly SemaphoreSlim _gate;

    public WeChatOcrPool(IReadOnlyList<IWeChatOcrBackend> backends)
    {
        if (backends.Count == 0)
            throw new ArgumentException("Need at least one WeChat OCR instance.", nameof(backends));
        _backends = backends.ToArray();
        _free = [];
        foreach (IWeChatOcrBackend backend in _backends)
            _free.Add(backend);
        _gate = new SemaphoreSlim(_backends.Length, _backends.Length);
    }

    public int InstanceCount => _backends.Length;

    public async Task<string> RecognizeAsync(string absoluteImagePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!_free.TryTake(out IWeChatOcrBackend? backend))
        {
            _gate.Release();
            throw new InvalidOperationException("WeChat OCR pool exhausted unexpectedly.");
        }

        try
        {
            return await backend.RecognizeAsync(absoluteImagePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _free.Add(backend);
            _gate.Release();
        }
    }
}
