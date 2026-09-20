using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;

namespace MiniOcr.Services;

/// <summary>
/// Owns one or more reusable <see cref="PaddleOcrAll"/> instances.
/// Page-level work is bounded; each engine uses LineWorkerCount for intra-page CLS/REC parallelism.
/// </summary>
public sealed class OcrEngine : IAsyncDisposable
{
    private readonly PaddleOcrAll[] _engines;
    private readonly SemaphoreSlim _gate;
    private readonly ILogger<OcrEngine> _logger;
    private int _rr;

    public bool IsLoaded { get; private set; }
    public int EngineCount => _engines.Length;
    public int LineWorkerCount { get; }

    private OcrEngine(PaddleOcrAll[] engines, int lineWorkerCount, ILogger<OcrEngine> logger)
    {
        _engines = engines;
        LineWorkerCount = lineWorkerCount;
        _logger = logger;
        _gate = new SemaphoreSlim(engines.Length, engines.Length);
        IsLoaded = true;
    }

    public static async Task<OcrEngine> CreateAsync(ILogger<OcrEngine> logger, CancellationToken ct = default)
    {
        // Keep page workers low to avoid thrashing RAM on large PDFs (models × N + page bitmaps).
        int pageWorkers = Math.Clamp(Environment.ProcessorCount / 2, 1, 2);
        // Intra-page line workers: default SimdPaddleOCR is min(ProcessorCount, 4) when 0.
        int lineWorkers = Math.Clamp(Environment.ProcessorCount, 1, 4);

        var options = new PaddleOcrOptions
        {
            LineWorkerCount = lineWorkers,
            DetIntraOpThreads = Math.Clamp(Environment.ProcessorCount, 1, 8),
        };

        logger.LogInformation(
            "Loading ChineseV6Tiny × {Engines} (LineWorkerCount={LineWorkers}, DetIntraOpThreads={DetThreads})",
            pageWorkers, options.LineWorkerCount, options.DetIntraOpThreads);

        PaddleOcrAll[] engines = new PaddleOcrAll[pageWorkers];
        try
        {
            for (int i = 0; i < pageWorkers; i++)
            {
                ct.ThrowIfCancellationRequested();
                engines[i] = await PaddleOcrAll.LoadAsync(ChineseV6TinyModels.Default, options)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            foreach (PaddleOcrAll? e in engines)
                e?.Dispose();
            throw;
        }

        return new OcrEngine(engines, lineWorkers, logger);
    }

    public async Task<T> UseAsync<T>(Func<PaddleOcrAll, T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int idx = Math.Abs(Interlocked.Increment(ref _rr)) % _engines.Length;
            return work(_engines[idx]);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!IsLoaded)
            return ValueTask.CompletedTask;
        IsLoaded = false;
        foreach (PaddleOcrAll e in _engines)
            e.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
