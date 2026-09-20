using System.Runtime.InteropServices;
using MiniOcr;
using MiniOcr.Models;
using MiniOcr.Services;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

builder.Services.AddHttpClient<ParallelPdfDownloader>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniOcr/1.0 (+https://github.com/huiyuanai709/miniocr)");
    client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
});

using ILoggerFactory bootstrapLogs = LoggerFactory.Create(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
});

Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
Console.WriteLine("Loading ChineseV6Tiny OCR models...");

OcrEngine engine = await OcrEngine.CreateAsync(bootstrapLogs.CreateLogger<OcrEngine>());
builder.Services.AddSingleton(engine);
builder.Services.AddSingleton<PdfOcrPipeline>();

WebApplication app = builder.Build();
ILogger logger = app.Logger;

IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

app.MapGet("/health", (OcrEngine ocr) => Results.Json(
    new HealthResponse
    {
        Status = "ok",
        ModelsLoaded = ocr.IsLoaded,
        Runtime = RuntimeInformation.FrameworkDescription,
        ProcessorCount = Environment.ProcessorCount,
    },
    AppJsonContext.Default.HealthResponse));

app.MapPost("/ocr", async Task<IResult> (
    HttpRequest httpRequest,
    ParallelPdfDownloader downloader,
    PdfOcrPipeline pipeline,
    CancellationToken ct) =>
{
    OcrUrlRequest? body = await httpRequest.ReadFromJsonAsync(AppJsonContext.Default.OcrUrlRequest, ct)
        .ConfigureAwait(false);
    string? url = body?.Url;
    if (string.IsNullOrWhiteSpace(url))
    {
        return Results.Json(
            new OcrResponse
            {
                Ok = false,
                Error = "Request body must be JSON: { \"url\": \"https://.../file.pdf\" }",
            },
            AppJsonContext.Default.OcrResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    try
    {
        ParallelPdfDownloader.DownloadResult download =
            await downloader.DownloadAsync(url, ct).ConfigureAwait(false);
        using (download.Buffer)
        {
            OcrResponse response = await pipeline
                .ProcessAsync(download.Buffer, download.ElapsedMs, download.Mode, ct)
                .ConfigureAwait(false);
            return Results.Json(response, AppJsonContext.Default.OcrResponse);
        }
    }
    catch (ArgumentException ex)
    {
        return Results.Json(
            new OcrResponse { Ok = false, Error = ex.Message },
            AppJsonContext.Default.OcrResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }
    catch (HttpRequestException ex)
    {
        logger.LogWarning(ex, "Download failed for {Url}", url);
        return Results.Json(
            new OcrResponse { Ok = false, Error = "Failed to download PDF: " + ex.Message },
            AppJsonContext.Default.OcrResponse,
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(
            new OcrResponse { Ok = false, Error = ex.Message },
            AppJsonContext.Default.OcrResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "OCR pipeline failed for {Url}", url);
        return Results.Json(
            new OcrResponse { Ok = false, Error = "OCR failed: " + ex.Message },
            AppJsonContext.Default.OcrResponse,
            statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/", () => Results.Text(
    "MiniOcr AOT API\nPOST /ocr  {\"url\":\"https://.../file.pdf\"}\nGET  /health\n",
    "text/plain; charset=utf-8"));

string urls = string.Join(", ", app.Urls.DefaultIfEmpty("(default http://localhost:5000)"));
logger.LogInformation(
    "MiniOcr ready — engines={Engines}, lineWorkers={LineWorkers}, listening={Urls}",
    engine.EngineCount,
    engine.LineWorkerCount,
    urls);

await app.RunAsync();
