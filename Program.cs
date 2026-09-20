using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using PDFtoImage;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;

static string ResolvePdfPath(string[] args)
{
    if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
    {
        string path = Path.GetFullPath(args[0]);
        if (!File.Exists(path))
            throw new FileNotFoundException($"PDF not found: {path}");
        return path;
    }

    string? embedded = ExtractEmbeddedSample();
    if (embedded is not null)
        return embedded;

    string fallback = Path.Combine(AppContext.BaseDirectory, "samples", "sample-multipage.pdf");
    if (File.Exists(fallback))
        return fallback;

    throw new FileNotFoundException("No PDF argument and embedded sample not found.");
}

static string? ExtractEmbeddedSample()
{
    Assembly asm = Assembly.GetExecutingAssembly();
    const string resourceName = "MiniOcr.samples.sample-multipage.pdf";
    using Stream? stream = asm.GetManifestResourceStream(resourceName);
    if (stream is null)
        return null;

    string temp = Path.Combine(Path.GetTempPath(), "miniocr-sample-multipage.pdf");
    using (FileStream fs = File.Create(temp))
        stream.CopyTo(fs);
    return temp;
}

static Configuration ContiguousConfig()
{
    Configuration config = Configuration.Default.Clone();
    config.PreferContiguousImageBuffers = true;
    return config;
}

static Image<Rgba32> SkBitmapToRgba32(SKBitmap bitmap)
{
    using SKBitmap copy = bitmap.Copy(SKColorType.Rgba8888)
        ?? throw new InvalidOperationException("Failed to convert SKBitmap to RGBA8888.");

    Image<Rgba32> image = new(ContiguousConfig(), copy.Width, copy.Height);
    if (!image.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> memory))
    {
        // Fallback: allocate contiguous managed buffer and wrap via LoadPixelData path
        Rgba32[] buffer = GC.AllocateUninitializedArray<Rgba32>(copy.Width * copy.Height);
        Span<byte> dest = MemoryMarshal.AsBytes(buffer.AsSpan());
        IntPtr pixels = copy.GetPixels();
        unsafe
        {
            new ReadOnlySpan<byte>((void*)pixels, copy.ByteCount).CopyTo(dest);
        }
        image.Dispose();
        image = Image.LoadPixelData<Rgba32>(ContiguousConfig(), buffer, copy.Width, copy.Height);
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

string pdfPath = ResolvePdfPath(args);
Console.WriteLine($"PDF: {pdfPath}");
Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
Console.WriteLine();

byte[] pdfBytes = await File.ReadAllBytesAsync(pdfPath);
int pageCount = Conversion.GetPageCount(pdfBytes);
Console.WriteLine($"Pages: {pageCount}");

RenderOptions renderOptions = new(Dpi: 150);

Console.WriteLine("Loading ChineseV6Tiny models...");
Stopwatch loadSw = Stopwatch.StartNew();
using PaddleOcrAll ocr = await PaddleOcrAll.LoadAsync(ChineseV6TinyModels.Default);
loadSw.Stop();
double modelLoadMs = loadSw.Elapsed.TotalMilliseconds;
Console.WriteLine($"Model load: {modelLoadMs:F1} ms");
Console.WriteLine();

double totalRasterizeMs = 0;
double totalOcrMs = 0;
var pageOcrMs = new List<double>(pageCount);

for (int i = 0; i < pageCount; i++)
{
    Stopwatch rasterSw = Stopwatch.StartNew();
    using SKBitmap skBitmap = Conversion.ToImage(pdfBytes, i, options: renderOptions);
    using Image<Rgba32> image = SkBitmapToRgba32(skBitmap);
    rasterSw.Stop();
    double rasterMs = rasterSw.Elapsed.TotalMilliseconds;
    totalRasterizeMs += rasterMs;

    if (!image.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> memory))
        throw new InvalidDataException($"Page {i + 1}: pixels are not contiguous.");

    Stopwatch ocrSw = Stopwatch.StartNew();
    PaddleOcrResult result = ocr.Run(
        MemoryMarshal.AsBytes(memory.Span),
        image.Width,
        image.Height,
        format: ImagePixelFormat.Rgba32);
    ocrSw.Stop();
    double ocrMs = ocrSw.Elapsed.TotalMilliseconds;
    totalOcrMs += ocrMs;
    pageOcrMs.Add(ocrMs);

    string text = result.Text?.Replace("\r", "").Trim() ?? "";
    string preview = text.Length <= 200 ? text : text[..200] + "…";
    preview = preview.Replace("\n", " / ");

    Console.WriteLine($"--- Page {i + 1}/{pageCount} ({image.Width}x{image.Height}) ---");
    Console.WriteLine($"  Rasterize: {rasterMs:F1} ms");
    Console.WriteLine($"  OCR:       {ocrMs:F1} ms");
    Console.WriteLine($"  Preview:   {preview}");
    Console.WriteLine();
}

double totalOcrSec = totalOcrMs / 1000.0;
double msPerPage = pageCount > 0 ? totalOcrMs / pageCount : 0;
double pagesPerSec = totalOcrSec > 0 ? pageCount / totalOcrSec : 0;
double totalPipelineMs = totalRasterizeMs + totalOcrMs;

Console.WriteLine("========== Summary ==========");
Console.WriteLine($"Pages:              {pageCount}");
Console.WriteLine($"Model load:         {modelLoadMs:F1} ms");
Console.WriteLine($"Rasterize total:    {totalRasterizeMs:F1} ms ({totalRasterizeMs / 1000.0:F3} s)");
Console.WriteLine($"OCR total:          {totalOcrMs:F1} ms ({totalOcrSec:F3} s)");
Console.WriteLine($"Pipeline (r+ocr):   {totalPipelineMs:F1} ms ({totalPipelineMs / 1000.0:F3} s)");
Console.WriteLine($"OCR ms/page:        {msPerPage:F1} ms");
Console.WriteLine($"OCR pages/sec:      {pagesPerSec:F2}");
Console.WriteLine("Per-page OCR ms:     " + string.Join(", ", pageOcrMs.Select(m => m.ToString("F1"))));

Console.WriteLine();
Console.WriteLine(
    $"BENCH_JSON:{{\"pages\":{pageCount},\"model_load_ms\":{modelLoadMs:F1},\"rasterize_ms\":{totalRasterizeMs:F1},\"ocr_ms\":{totalOcrMs:F1},\"ocr_sec\":{totalOcrSec:F3},\"ms_per_page\":{msPerPage:F1},\"pages_per_sec\":{pagesPerSec:F2}}}");
