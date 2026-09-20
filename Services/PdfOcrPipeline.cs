using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using MiniOcr.Models;
using PDFtoImage;
using Sdcb.SimdPaddleOCR;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;

namespace MiniOcr.Services;

public sealed class PdfOcrPipeline
{
    private readonly OcrEngine _engine;
    private readonly ILogger<PdfOcrPipeline> _logger;
    private readonly int _pageWindow;

    public PdfOcrPipeline(OcrEngine engine, ILogger<PdfOcrPipeline> logger)
    {
        _engine = engine;
        _logger = logger;
        _pageWindow = Math.Max(2, engine.EngineCount * 2);
    }

    public Task<OcrResponse> ProcessAsync(
        RentedBuffer pdf,
        double downloadMs,
        string downloadMode,
        CancellationToken ct)
    {
        Stopwatch totalSw = Stopwatch.StartNew();
        // Wrap rented array with exact Length — no copy; PDFium reads via Stream.
        MemoryStream pdfStream = new(
            pdf.DangerousGetArray(),
            index: 0,
            count: pdf.Length,
            writable: false,
            publiclyVisible: true);
        return ProcessCoreAsync(pdfStream, pdf.Length, downloadMs, downloadMode, totalSw, ct);
    }

    private async Task<OcrResponse> ProcessCoreAsync(
        Stream pdfStream,
        long pdfByteCount,
        double downloadMs,
        string downloadMode,
        Stopwatch totalSw,
        CancellationToken ct)
    {
        await using (pdfStream.ConfigureAwait(false))
        {
            int pageCount = Conversion.GetPageCount(pdfStream, leaveOpen: true);
            if (pageCount <= 0)
                throw new InvalidOperationException("PDF has no pages.");
            if (pageCount > 2000)
                throw new InvalidOperationException($"PDF has {pageCount} pages; max supported is 2000.");

            _logger.LogInformation("OCR pipeline start: {Pages} pages, {Bytes} bytes PDF", pageCount, pdfByteCount);

            RenderOptions renderOptions = new(Dpi: 150);
            OcrPageResult[] pages = new OcrPageResult[pageCount];

            Channel<(int Index, SKBitmap Bitmap, double RasterMs)> rasterized =
                Channel.CreateBounded<(int, SKBitmap, double)>(new BoundedChannelOptions(_pageWindow)
                {
                    SingleWriter = true,
                    SingleReader = false,
                    FullMode = BoundedChannelFullMode.Wait,
                });

            double rasterTotal = 0;
            double ocrTotal = 0;
            object timingLock = new();

            Task producer = ProduceAsync(pdfStream, pageCount, renderOptions, rasterized.Writer, ct);

            async Task ConsumerAsync()
            {
                Configuration imgConfig = ContiguousConfig();
                await foreach (var (index, bitmap, rasterMs) in rasterized.Reader.ReadAllAsync(ct)
                                   .ConfigureAwait(false))
                {
                    using (bitmap)
                    using (Image<Rgba32> image = SkBitmapToRgba32(bitmap, imgConfig))
                    {
                        if (!image.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> memory))
                            throw new InvalidDataException($"Page {index + 1}: pixels are not contiguous.");

                        Stopwatch ocrSw = Stopwatch.StartNew();
                        PaddleOcrResult result = await _engine.UseAsync(ocr => ocr.Run(
                            MemoryMarshal.AsBytes(memory.Span),
                            image.Width,
                            image.Height,
                            format: ImagePixelFormat.Rgba32), ct).ConfigureAwait(false);
                        ocrSw.Stop();

                        pages[index] = new OcrPageResult
                        {
                            Page = index + 1,
                            Width = image.Width,
                            Height = image.Height,
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

    private static async Task ProduceAsync(
        Stream pdfStream,
        int pageCount,
        RenderOptions renderOptions,
        ChannelWriter<(int, SKBitmap, double)> writer,
        CancellationToken ct)
    {
        try
        {
            for (int i = 0; i < pageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                Stopwatch sw = Stopwatch.StartNew();
                SKBitmap bitmap;
                lock (pdfStream)
                {
                    pdfStream.Position = 0;
                    bitmap = Conversion.ToImage(pdfStream, i, leaveOpen: true, options: renderOptions);
                }
                sw.Stop();
                await writer.WriteAsync((i, bitmap, sw.Elapsed.TotalMilliseconds), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static Configuration ContiguousConfig()
    {
        Configuration config = Configuration.Default.Clone();
        config.PreferContiguousImageBuffers = true;
        return config;
    }

    private static Image<Rgba32> SkBitmapToRgba32(SKBitmap bitmap, Configuration config)
    {
        using SKBitmap copy = bitmap.Copy(SKColorType.Rgba8888)
            ?? throw new InvalidOperationException("Failed to convert SKBitmap to RGBA8888.");

        Image<Rgba32> image = new(config, copy.Width, copy.Height);
        if (!image.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> memory))
        {
            Rgba32[] buffer = GC.AllocateUninitializedArray<Rgba32>(copy.Width * copy.Height);
            Span<byte> dest = MemoryMarshal.AsBytes(buffer.AsSpan());
            IntPtr pixels = copy.GetPixels();
            unsafe
            {
                new ReadOnlySpan<byte>((void*)pixels, copy.ByteCount).CopyTo(dest);
            }
            image.Dispose();
            image = Image.LoadPixelData<Rgba32>(config, buffer, copy.Width, copy.Height);
            if (!image.DangerousTryGetSinglePixelMemory(out _))
                throw new InvalidDataException("ImageSharp pixels are not contiguous after fallback.");
            return image;
        }

        Span<byte> destBytes = MemoryMarshal.AsBytes(memory.Span);
        IntPtr srcPixels = copy.GetPixels();
        unsafe
        {
            new ReadOnlySpan<byte>((void*)srcPixels, copy.ByteCount).CopyTo(destBytes);
        }
        return image;
    }
}
