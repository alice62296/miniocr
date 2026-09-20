namespace MiniOcr.Services;

/// <summary>
/// Throughput knobs from environment (startup) with sensible 8-core defaults aimed at &lt;5 min / 2000 pages.
/// Override: MINIOCR_ENGINES, MINIOCR_DPI, MINIOCR_LINE_WORKERS, MINIOCR_DET_THREADS,
/// MINIOCR_USE_CLS, MINIOCR_RASTER_WORKERS, MINIOCR_REC_BATCH, MINIOCR_DET_LIMIT_SIDE.
/// Per-request DPI can still override via JSON body <c>dpi</c> or query <c>?dpi=</c>.
/// </summary>
public sealed class OcrRuntimeConfig
{
    public int EngineCount { get; init; }
    public int DefaultDpi { get; init; }
    public int LineWorkerCount { get; init; }
    public int DetIntraOpThreads { get; init; }
    public bool UseDirectionClassification { get; init; }
    public int RasterWorkerCount { get; init; }
    public int RecBatchLines { get; init; }
    public int DetLimitSideLength { get; init; }

    public static OcrRuntimeConfig FromEnvironment()
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        // Page-parallel engines: default half the cores (4 on 8-core), capped at 8.
        int engines = ReadInt("MINIOCR_ENGINES", Math.Clamp(cores / 2, 4, 8));
        engines = Math.Clamp(engines, 1, 16);

        // Keep intra-page threads low so engines × workers does not thrash an 8-core box.
        int lineDefault = engines >= 6 ? 2 : Math.Clamp(cores, 1, 4);
        int detDefault = engines >= 4 ? 2 : Math.Clamp(cores, 1, 8);
        int line = Math.Clamp(ReadInt("MINIOCR_LINE_WORKERS", lineDefault), 1, 16);
        int det = Math.Clamp(ReadInt("MINIOCR_DET_THREADS", detDefault), 1, 16);

        // Default 96 DPI — balanced ZH+EN readability vs throughput; ~(96/150)^2 ≈ 41% pixels vs baseline 150.
        int dpi = Math.Clamp(ReadInt("MINIOCR_DPI", 96), 36, 300);

        bool useCls = ReadBool("MINIOCR_USE_CLS", false);

        // Parallel PDF raster producers (each owns a MemoryStream view of the PDF bytes).
        int raster = Math.Clamp(ReadInt("MINIOCR_RASTER_WORKERS", Math.Min(engines, 4)), 1, 8);
        int recBatch = Math.Clamp(ReadInt("MINIOCR_REC_BATCH", 8), 1, 64);
        int detLimit = Math.Clamp(ReadInt("MINIOCR_DET_LIMIT_SIDE", 960), 64, 4096);

        return new OcrRuntimeConfig
        {
            EngineCount = engines,
            DefaultDpi = dpi,
            LineWorkerCount = line,
            DetIntraOpThreads = det,
            UseDirectionClassification = useCls,
            RasterWorkerCount = raster,
            RecBatchLines = recBatch,
            DetLimitSideLength = detLimit,
        };
    }

    private static int ReadInt(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int v) ? v : fallback;
    }

    private static bool ReadBool(string name, bool fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (bool.TryParse(raw, out bool b))
            return b;
        if (raw is "1" or "yes" or "YES" or "on" or "ON")
            return true;
        if (raw is "0" or "no" or "NO" or "off" or "OFF")
            return false;
        return fallback;
    }
}
