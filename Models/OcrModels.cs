namespace MiniOcr.Models;

public sealed class OcrUrlRequest
{
    public string? Url { get; set; }
    /// <summary>Optional per-request raster DPI override (36–300). Defaults to server MINIOCR_DPI.</summary>
    public int? Dpi { get; set; }
}

public sealed class OcrPageResult
{
    public int Page { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Text { get; set; } = "";
    public double RasterizeMs { get; set; }
    public double OcrMs { get; set; }
}

public sealed class OcrTimings
{
    public double DownloadMs { get; set; }
    public double RasterizeMs { get; set; }
    public double OcrMs { get; set; }
    public double TotalMs { get; set; }
}

public sealed class OcrResponse
{
    public bool Ok { get; set; }
    public int PageCount { get; set; }
    public long PdfBytes { get; set; }
    public string DownloadMode { get; set; } = "";
    public int Dpi { get; set; }
    public OcrTimings Timings { get; set; } = new();
    public List<OcrPageResult> Pages { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class HealthResponse
{
    public string Status { get; set; } = "ok";
    public bool ModelsLoaded { get; set; }
    public string Runtime { get; set; } = "";
    public int ProcessorCount { get; set; }
    public int EngineCount { get; set; }
    public int LineWorkerCount { get; set; }
    public int DetIntraOpThreads { get; set; }
    public int DefaultDpi { get; set; }
    public bool UseDirectionClassification { get; set; }
    public int RasterWorkerCount { get; set; }
    public int RecBatchLines { get; set; }
    public int DetLimitSideLength { get; set; }
}

