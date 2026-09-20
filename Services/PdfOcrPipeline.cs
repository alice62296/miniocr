using System.Diagnostics;
using System.Threading.Channels;
using MiniOcr.Models;
using PDFtoImage;
using Sdcb.SimdPaddleOCR;
using SkiaSharp;

namespace MiniOcr.Services;

public sealed class PdfOcrPipeline
{
    private readonly OcrEngine _engine;
    private readonly ILogger<PdfOcrPipeline> _logger;
    private readonly int _pageWindow;
    private readonly int _defaultDpi;
    private readonly int _rasterWorkers;

    public PdfOcrPipeline(OcrEngine engine, ILogger<PdfOcrPipeline> logger)
    {
        _engine = engine;
        _logger = logger;
        _pageWindow = Math.Max(4, engine.EngineCount * 2);
        _defaultDpi = engine.Config.DefaultDpi;
        _rasterWorkers = engine.Config.RasterWorkerCount;
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
        // Independent MemoryStream over the rented array — producers reset Position locally.
        MemoryStream pdfStream = new(array, index: 0, count: length, writable: false, publiclyVisible: true);
        return ProcessCoreAsync(pdfStream, array, length, downloadMs, downloadMode, totalSw, dpiOverride, ct);
    }

    private async Task<OcrResponse> ProcessCoreAsync(
        Stream pdfStream,
        byte[] pdfBytes,
        int pdfByteCount,
        double downloadMs,
        string downloadMode,
        Stopwatch totalSw,
        int? dpiOverride,
        CancellationToken ct)
    {
        await using (pdfStream.ConfigureAwait(false))
        {
            int pageCount = Conversion.GetPageCount(pdfStream, leaveOpen: true);
            if (pageCount <= 0)
                throw new InvalidOperationException("PDF has no pages.");
            if (pageCount > 2000)
                throw new InvalidOperationException($"PDF has {pageCount} pages; max supported is 2000.");

            int dpi = Math.Clamp(dpiOverride ?? _defaultDpi, 36, 300);
            _logger.LogInformation(
                "OCR pipeline start: {Pages} pages, {Bytes} bytes PDF, dpi={Dpi}, engines={Engines}, rasterWorkers={Raster}",
                pageCount, pdfByteCount, dpi, _engine.EngineCount, _rasterWorkers);

            RenderOptions renderOptions = new(Dpi: dpi);
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

                            PaddleOcrResult result = await _engine.UseAsync(ocr =>
                            {
                                unsafe
                                {
                                    ReadOnlySpan<byte> span = new((void*)pixels, byteCount);
                                    return ocr.Run(span, width, height, stride, ImagePixelFormat.Bgra32);
                                }
                            }, ct).ConfigureAwait(false);
                            ocrSw.Stop();

                            pages[index] = new OcrPageResult
                            {
                                Page = index + 1,
                                Width = width,
                                Height = height,
                                Text = result.Text?.Replace("\r", "").Trim() ?? "",
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

            Task[] consumers = Enumerable.Range(0, _engine.EngineCount)
                .Select(_ => ConsumerAsync())
                .ToArray();

            await producer.ConfigureAwait(false);
            await Task.WhenAll(consumers).ConfigureAwait(false);

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
            };
        }
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
                    // Each raster worker gets its own stream over the same rented bytes (read-only).
                    using MemoryStream local = new(
                        pdfBytes, index: 0, count: pdfLength, writable: false, publiclyVisible: true);
                    for (int i = workerId; i < pageCount; i += workers)
                    {
                        ct.ThrowIfCancellationRequested();
                        Stopwatch sw = Stopwatch.StartNew();
                        SKBitmap bitmap;
                        lock (local)
                        {
                            local.Position = 0;
                            bitmap = Conversion.ToImage(local, i, leaveOpen: true, options: renderOptions);
                        }

                        sw.Stop();
                        await writer.WriteAsync((i, bitmap, sw.Elapsed.TotalMilliseconds), ct)
                            .ConfigureAwait(false);
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
}
