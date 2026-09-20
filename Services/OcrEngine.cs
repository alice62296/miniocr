using System.Collections.Concurrent;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.ModelProvider;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;

namespace MiniOcr.Services;

/// <summary>
/// Owns one or more reusable <see cref="PaddleOcrAll"/> instances as a pool.
/// Page-level work borrows an engine exclusively; LineWorkerCount handles intra-page parallelism.
/// </summary>
public sealed class OcrEngine : IAsyncDisposable
{
    private readonly PaddleOcrAll[] _engines;
    private readonly ConcurrentBag<(PaddleOcrAll Engine, int Index)> _pool;
    private readonly SemaphoreSlim _gate;
    private readonly ILogger<OcrEngine> _logger;

    public bool IsLoaded { get; private set; }
    public int EngineCount => _engines.Length;
    public int LineWorkerCount { get; }
    public int DetIntraOpThreads { get; }
    public bool UseDirectionClassification { get; }
    public OcrRuntimeConfig Config { get; }

    private OcrEngine(
        PaddleOcrAll[] engines,
        OcrRuntimeConfig config,
        ILogger<OcrEngine> logger)
    {
        _engines = engines;
        Config = config;
        LineWorkerCount = config.LineWorkerCount;
        DetIntraOpThreads = config.DetIntraOpThreads;
        UseDirectionClassification = config.UseDirectionClassification;
        _logger = logger;
        _pool = [];
        for (int i = 0; i < engines.Length; i++)
            _pool.Add((engines[i], i));
        _gate = new SemaphoreSlim(engines.Length, engines.Length);
        IsLoaded = true;
    }

    public static async Task<OcrEngine> CreateAsync(
        ILogger<OcrEngine> logger,
        OcrRuntimeConfig? config = null,
        CancellationToken ct = default)
    {
        config ??= OcrRuntimeConfig.FromEnvironment();
        int pageWorkers = config.EngineCount;

        var options = new PaddleOcrOptions
        {
            LineWorkerCount = config.LineWorkerCount,
            DetIntraOpThreads = config.DetIntraOpThreads,
            UseDirectionClassification = config.UseDirectionClassification,
            RecBatchLines = config.RecBatchLines,
            Detector = new PaddleOcrDetectorOptions
            {
                LimitSideLength = config.DetLimitSideLength,
            },
        };

        PaddleOcrModelBundle bundle = ChineseV6TinyModels.Default;
        if (!config.UseDirectionClassification)
        {
            // Skip loading CLS weights entirely — saves RAM × engine count and avoids CLS compute.
            bundle = new PaddleOcrModelBundle(
                bundle.Name + "-nocls",
                bundle.LanguageCode,
                bundle.Detection,
                bundle.Recognition,
                bundle.Dictionary,
                classification: null!);
        }

        logger.LogInformation(
            "Loading ChineseV6Tiny × {Engines} (LineWorkerCount={LineWorkers}, DetIntraOpThreads={DetThreads}, UseCls={UseCls}, DpiDefault={Dpi})",
            pageWorkers,
            options.LineWorkerCount,
            options.DetIntraOpThreads,
            options.UseDirectionClassification,
            config.DefaultDpi);

        PaddleOcrAll[] engines = new PaddleOcrAll[pageWorkers];
        try
        {
            Task<PaddleOcrAll>[] loads = new Task<PaddleOcrAll>[pageWorkers];
            for (int i = 0; i < pageWorkers; i++)
            {
                ct.ThrowIfCancellationRequested();
                loads[i] = PaddleOcrAll.LoadAsync(bundle, options, ct);
            }

            PaddleOcrAll[] loaded = await Task.WhenAll(loads).ConfigureAwait(false);
            for (int i = 0; i < pageWorkers; i++)
                engines[i] = loaded[i];
        }
        catch
        {
            foreach (PaddleOcrAll? e in engines)
                e?.Dispose();
            throw;
        }

        return new OcrEngine(engines, config, logger);
    }

    public async Task<T> UseAsync<T>(Func<PaddleOcrAll, T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        if (!_pool.TryTake(out var leased))
        {
            _gate.Release();
            throw new InvalidOperationException("OCR engine pool exhausted unexpectedly.");
        }

        try
        {
            return work(leased.Engine);
        }
        finally
        {
            _pool.Add(leased);
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
