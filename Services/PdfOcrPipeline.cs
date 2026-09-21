using System.Diagnostics;
using System.Threading.Channels;
using MiniOcr.Models;
using PDFtoImage;
using Sdcb.SimdPaddleOCR;
using SkiaSharp;

namespace MiniOcr.Services;

public sealed class PdfOcrPipeline
{
    private readonly OcrEngine? _engine;
    private readonly ILogger<PdfOcrPipeline> _logger;
    private readonly LlmEntityExtractor? _llm;
    private readonly LlmVisionOcr? _vision;
    private readonly OcrRuntimeConfig _config;
    private readonly int _pageWindow;
    private readonly int _defaultDpi;
    private readonly int _rasterWorkers;

    public PdfOcrPipeline(
        OcrRuntimeConfig config,
        ILogger<PdfOcrPipeline> logger,
        OcrEngine? engine = null,
        LlmEntityExtractor? llm = null,
        LlmVisionOcr? vision = null)
    {
        _config = config;
        _logger = logger;
        _engine = engine;
        _llm = llm;
        _vision = vision;
        _defaultDpi = config.DefaultDpi;
        _rasterWorkers = config.RasterWorkerCount;

        if (config.IsLlmMode)
        {
            if (vision is null || !vision.IsUsable)
                throw new InvalidOperationException(
                    "ocr.mode=llm requires a usable LLM (enabled + apiKey + baseUrl + model).");
            _pageWindow = Math.Max(4, Math.Min(vision.OcrConcurrency, 64));
        }
        else
        {
            if (engine is null)
                throw new InvalidOperationException("ocr.mode=local requires OcrEngine.");
            _pageWindow = Math.Max(4, engine.EngineCount * 2);
        }
    }

    public Task<OcrResponse> ProcessAsync(
        RentedBuffer pdf,
        double downloadMs,
        string downloadMode,
        CancellationToken ct,
        int? dpiOverride = null)
    {
        Stopwatch totalSw = Stopwatch.StartNew();
        byte[] array = pdf.DangerousGetArray();
        int length = pdf.Length;
        return ProcessCoreAsync(array, length, downloadMs, downloadMode, totalSw, dpiOverride, ct);
    }

    private async Task<OcrResponse> ProcessCoreAsync(
        byte[] pdfBytes,
        int pdfByteCount,
        double downloadMs,
        string downloadMode,
        Stopwatch totalSw,
        int? dpiOverride,
        CancellationToken ct)
    {
        int pageCount;
        using (MemoryStream countStream = new(pdfBytes, index: 0, count: pdfByteCount, writable: false, publiclyVisible: true))
            pageCount = Conversion.GetPageCount(countStream, leaveOpen: false);

        if (pageCount <= 0)
            throw new InvalidOperationException("PDF has no pages.");
        if (pageCount > 2000)
            throw new InvalidOperationException($"PDF has {pageCount} pages; max supported is 2000.");

        int dpi = Math.Clamp(dpiOverride ?? _defaultDpi, 36, 300);

        if (_config.IsLlmMode)
        {
            _logger.LogInformation(
                "OCR pipeline (llm vision): {Pages} pages, {Bytes} bytes PDF, dpi={Dpi}, ocrConcurrency={Conc}, rasterWorkers={Raster}",
                pageCount, pdfByteCount, dpi, _vision!.OcrConcurrency, _rasterWorkers);
            return await ProcessWithVisionAsync(
                pdfBytes, pdfByteCount, pageCount, dpi, downloadMs, downloadMode, totalSw, ct)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "OCR pipeline (local): {Pages} pages, {Bytes} bytes PDF, dpi={Dpi}, engines={Engines}, rasterWorkers={Raster}",
            pageCount, pdfByteCount, dpi, _engine!.EngineCount, _rasterWorkers);

        return await ProcessWithLocalAsync(
            pdfBytes, pdfByteCount, pageCount, dpi, downloadMs, downloadMode, totalSw, ct)
            .ConfigureAwait(false);
    }

    private async Task<OcrResponse> ProcessWithVisionAsync(
        byte[] pdfBytes,
        int pdfByteCount,
        int pageCount,
        int dpi,
        double downloadMs,
        string downloadMode,
        Stopwatch totalSw,
        CancellationToken ct)
    {
        RenderOptions renderOptions = CreateRenderOptions(dpi);

        // Phase 1: parallel rasterize → JPEG (dispose bitmaps immediately; keep memory low).
        PageJpeg[] images = new PageJpeg[pageCount];
        Channel<(int Index, SKBitmap Bitmap, double RasterMs)> rasterized =
            Channel.CreateBounded<(int, SKBitmap, double)>(new BoundedChannelOptions(_pageWindow)
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        double rasterTotal = 0;
        object timingLock = new();

        Task producer = ProduceParallelAsync(
            pdfBytes, pdfByteCount, pageCount, renderOptions, rasterized.Writer, ct);

        async Task EncodeConsumerAsync()
        {
            await foreach (var (index, bitmap, rasterMs) in rasterized.Reader.ReadAllAsync(ct)
                               .ConfigureAwait(false))
            {
                using (bitmap)
                {
                    int width = bitmap.Width;
                    int height = bitmap.Height;
                    byte[] jpeg = LlmVisionOcr.EncodeJpeg(bitmap);
                    images[index] = new PageJpeg(index, width, height, jpeg, rasterMs);
                    lock (timingLock)
                        rasterTotal += rasterMs;
                }
            }
        }

        // A few encoders is enough; vision I/O concurrency comes later.
        int encodeWorkers = Math.Clamp(_rasterWorkers, 1, 4);
        Task[] encoders = Enumerable.Range(0, encodeWorkers)
            .Select(_ => EncodeConsumerAsync())
            .ToArray();

        await producer.ConfigureAwait(false);
        await Task.WhenAll(encoders).ConfigureAwait(false);

        for (int i = 0; i < pageCount; i++)
        {
            if (images[i].Jpeg is null)
                throw new InvalidOperationException($"Missing rasterized page index {i}.");
        }

        // Phase 2: high-concurrency vision OCR (I/O bound).
        OcrPageResult[] pages = new OcrPageResult[pageCount];
        double ocrTotal = 0;
        int concurrency = _vision!.OcrConcurrency;

        await Parallel.ForEachAsync(
            images,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = ct,
            },
            async (img, token) =>
            {
                OcrPageResult page = await _vision
                    .RecognizePageAsync(img.Index + 1, img.Width, img.Height, img.Jpeg!, img.RasterMs, token)
                    .ConfigureAwait(false);
                pages[img.Index] = page;
                lock (timingLock)
                    ocrTotal += page.OcrMs;
            }).ConfigureAwait(false);

        // Entities derived from vision ruleList (for any consumer of OcrResponse.Entities).
        OcrEntities entities = EntitiesFromVisionPages(pages);

        totalSw.Stop();

        return new OcrResponse
        {
            Ok = true,
            PageCount = pageCount,
            PdfBytes = pdfByteCount,
            DownloadMode = downloadMode,
            Dpi = dpi,
            Timings = new OcrTimings
            {
                DownloadMs = Math.Round(downloadMs, 1),
                RasterizeMs = Math.Round(rasterTotal, 1),
                OcrMs = Math.Round(ocrTotal, 1),
                TotalMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 1),
            },
            Pages = pages.ToList(),
            Entities = entities,
        };
    }

    private async Task<OcrResponse> ProcessWithLocalAsync(
        byte[] pdfBytes,
        int pdfByteCount,
        int pageCount,
        int dpi,
        double downloadMs,
        string downloadMode,
        Stopwatch totalSw,
        CancellationToken ct)
    {
        RenderOptions renderOptions = CreateRenderOptions(dpi);

        OcrPageResult[] pages = new OcrPageResult[pageCount];

        Channel<(int Index, SKBitmap Bitmap, double RasterMs)> rasterized =
            Channel.CreateBounded<(int, SKBitmap, double)>(new BoundedChannelOptions(_pageWindow)
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        double rasterTotal = 0;
        double ocrTotal = 0;
        object timingLock = new();

        Task producer = ProduceParallelAsync(
            pdfBytes, pdfByteCount, pageCount, renderOptions, rasterized.Writer, ct);

        async Task ConsumerAsync()
        {
            await foreach (var (index, bitmap, rasterMs) in rasterized.Reader.ReadAllAsync(ct)
                               .ConfigureAwait(false))
            {
                using (bitmap)
                {
                    EnsureBgra8888(bitmap, out SKBitmap working, out bool ownedWorking);
                    try
                    {
                        Stopwatch ocrSw = Stopwatch.StartNew();
                        int width = working.Width;
                        int height = working.Height;
                        int stride = working.RowBytes;
                        IntPtr pixels = working.GetPixels();
                        int byteCount = stride * height;

                        PaddleOcrResult result = await _engine!.UseAsync(ocr =>
                        {
                            unsafe
                            {
                                ReadOnlySpan<byte> span = new((void*)pixels, byteCount);
                                return ocr.Run(span, width, height, stride, ImagePixelFormat.Bgra32);
                            }
                        }, ct).ConfigureAwait(false);
                        ocrSw.Stop();

                        string pageText = result.Text?.Replace("\r", "").Trim() ?? "";
                        pages[index] = new OcrPageResult
                        {
                            Page = index + 1,
                            Width = width,
                            Height = height,
                            Text = pageText,
                            RasterizeMs = Math.Round(rasterMs, 1),
                            OcrMs = Math.Round(ocrSw.Elapsed.TotalMilliseconds, 1),
                        };

                        lock (timingLock)
                        {
                            rasterTotal += rasterMs;
                            ocrTotal += ocrSw.Elapsed.TotalMilliseconds;
                        }
                    }
                    finally
                    {
                        if (ownedWorking)
                            working.Dispose();
                    }
                }
            }
        }

        Task[] consumers = Enumerable.Range(0, _engine!.EngineCount)
            .Select(_ => ConsumerAsync())
            .ToArray();

        await producer.ConfigureAwait(false);
        await Task.WhenAll(consumers).ConfigureAwait(false);

        OcrEntities entities = await ExtractEntitiesAsync(pages, ct).ConfigureAwait(false);

        totalSw.Stop();

        return new OcrResponse
        {
            Ok = true,
            PageCount = pageCount,
            PdfBytes = pdfByteCount,
            DownloadMode = downloadMode,
            Dpi = dpi,
            Timings = new OcrTimings
            {
                DownloadMs = Math.Round(downloadMs, 1),
                RasterizeMs = Math.Round(rasterTotal, 1),
                OcrMs = Math.Round(ocrTotal, 1),
                TotalMs = Math.Round(totalSw.Elapsed.TotalMilliseconds, 1),
            },
            Pages = pages.ToList(),
            Entities = entities,
        };
    }

    private static RenderOptions CreateRenderOptions(int dpi) =>
        new(
            Dpi: dpi,
            WithAnnotations: false,
            WithFormFill: false,
            AntiAliasing: PdfAntiAliasing.None,
            Grayscale: true);

    private static OcrEntities EntitiesFromVisionPages(OcrPageResult[] pages)
    {
        EntityAccumulator acc = new();
        foreach (OcrPageResult page in pages)
        {
            if (page.RuleList is null)
                continue;
            foreach (ChallengeRule rule in page.RuleList)
            {
                string code = (rule.RuleCode ?? "").Trim().ToUpperInvariant();
                foreach (ChallengeRuleItem item in rule.RuleItemList)
                {
                    if (code == "B04" && !string.IsNullOrWhiteSpace(item.PersonName))
                    {
                        int n = Math.Max(1, item.Count);
                        for (int i = 0; i < n; i++)
                            acc.AddPerson(item.PersonName.Trim(), page.Page);
                    }
                    else if (code == "B06" && !string.IsNullOrWhiteSpace(item.CompanyName))
                    {
                        int n = Math.Max(1, item.Count);
                        for (int i = 0; i < n; i++)
                            acc.AddCompany(item.CompanyName.Trim(), page.Page);
                    }
                }
            }
        }

        return EntityExtractor.ToEntities(acc);
    }

    private async Task<OcrEntities> ExtractEntitiesAsync(OcrPageResult[] pages, CancellationToken ct)
    {
        bool preferLlm = _llm is { IsUsable: true };
        bool fallback = _llm?.Config.FallbackToHeuristics ?? true;

        if (preferLlm)
        {
            try
            {
                OcrEntities llmEntities = await _llm!.ExtractAsync(pages, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "LLM NER done: companies={Companies}, persons={Persons}",
                    llmEntities.Companies.Count,
                    llmEntities.Persons.Count);
                return llmEntities;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM NER failed; fallbackToHeuristics={Fallback}", fallback);
                if (!fallback)
                    return new OcrEntities();
            }
        }

        List<string> texts = pages.Select(p => p.Text ?? "").ToList();
        return EntityExtractor.ExtractFromPages(texts);
    }

    private async Task ProduceParallelAsync(
        byte[] pdfBytes,
        int pdfLength,
        int pageCount,
        RenderOptions renderOptions,
        ChannelWriter<(int, SKBitmap, double)> writer,
        CancellationToken ct)
    {
        int workers = Math.Clamp(_rasterWorkers, 1, Math.Max(1, pageCount));
        try
        {
            Task[] tasks = new Task[workers];
            for (int w = 0; w < workers; w++)
            {
                int workerId = w;
                tasks[w] = Task.Run(async () =>
                {
                    using MemoryStream local = new(
                        pdfBytes, index: 0, count: pdfLength, writable: false, publiclyVisible: true);

                    List<int> pageIndices = new((pageCount + workers - 1) / workers);
                    for (int i = workerId; i < pageCount; i += workers)
                        pageIndices.Add(i);

                    IEnumerator<SKBitmap> enumerator =
                        Conversion.ToImages(local, pageIndices, leaveOpen: true, options: renderOptions)
                            .GetEnumerator();
                    try
                    {
                        int idx = 0;
                        while (idx < pageIndices.Count)
                        {
                            ct.ThrowIfCancellationRequested();
                            Stopwatch sw = Stopwatch.StartNew();
                            if (!enumerator.MoveNext())
                            {
                                throw new InvalidOperationException(
                                    $"PDFtoImage yielded {idx} pages, expected {pageIndices.Count}.");
                            }

                            SKBitmap bitmap = enumerator.Current;
                            sw.Stop();
                            int pageIndex = pageIndices[idx++];
                            try
                            {
                                await writer.WriteAsync((pageIndex, bitmap, sw.Elapsed.TotalMilliseconds), ct)
                                    .ConfigureAwait(false);
                            }
                            catch
                            {
                                bitmap.Dispose();
                                while (enumerator.MoveNext())
                                    enumerator.Current.Dispose();
                                throw;
                            }
                        }
                    }
                    finally
                    {
                        enumerator.Dispose();
                    }
                }, ct);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static void EnsureBgra8888(SKBitmap source, out SKBitmap working, out bool owned)
    {
        if (source.ColorType == SKColorType.Bgra8888)
        {
            working = source;
            owned = false;
            return;
        }

        working = source.Copy(SKColorType.Bgra8888)
            ?? throw new InvalidOperationException("Failed to convert SKBitmap to BGRA8888.");
        owned = true;
    }

    private readonly record struct PageJpeg(int Index, int Width, int Height, byte[]? Jpeg, double RasterMs);
}
