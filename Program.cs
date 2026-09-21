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
ILogger bootstrapLogger = bootstrapLogs.CreateLogger("MiniOcr.Startup");

string configPath = AppConfigStore.GetConfigPath();
AppConfigFile appConfig = AppConfigStore.LoadOrCreate(bootstrapLogger);
LlmRuntimeConfig llmConfig = AppConfigStore.ResolveLlm(appConfig);
OcrRuntimeConfig runtimeConfig = OcrRuntimeConfig.FromAppConfig(appConfig);

Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
Console.WriteLine($"Config: {configPath}");
Console.WriteLine(
    $"CPU auto-scale: ProcessorCount={runtimeConfig.ProcessorCount}, autoScaleFromCpu={runtimeConfig.AutoScaleFromCpu}");
Console.WriteLine(
    $"OCR knobs: engines={runtimeConfig.EngineCount}, dpi={runtimeConfig.DefaultDpi}, " +
    $"lineWorkers={runtimeConfig.LineWorkerCount}, detThreads={runtimeConfig.DetIntraOpThreads}, " +
    $"useCls={runtimeConfig.UseDirectionClassification}, rasterWorkers={runtimeConfig.RasterWorkerCount}");
Console.WriteLine(
    $"LLM NER: enabled={llmConfig.Enabled}, usable={llmConfig.IsUsable}, " +
    $"model={llmConfig.Model}, baseUrl={llmConfig.BaseUrl}, " +
    $"maxConcurrency={llmConfig.MaxConcurrency}, " +
    $"fallbackToHeuristics={llmConfig.FallbackToHeuristics}, apiKey={(string.IsNullOrEmpty(llmConfig.ApiKey) ? "(empty)" : "(set)")}");
Console.WriteLine("Loading ChineseV6Tiny OCR models...");

OcrEngine engine = await OcrEngine.CreateAsync(
    bootstrapLogs.CreateLogger<OcrEngine>(),
    runtimeConfig);
builder.Services.AddSingleton(runtimeConfig);
builder.Services.AddSingleton(llmConfig);
builder.Services.AddSingleton(engine);

builder.Services.AddHttpClient(nameof(LlmEntityExtractor), (sp, client) =>
{
    LlmRuntimeConfig cfg = sp.GetRequiredService<LlmRuntimeConfig>();
    client.Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniOcr/1.0 (+LLM-NER)");
});
builder.Services.AddSingleton<LlmEntityExtractor>(sp =>
{
    IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();
    HttpClient http = factory.CreateClient(nameof(LlmEntityExtractor));
    return new LlmEntityExtractor(
        http,
        sp.GetRequiredService<LlmRuntimeConfig>(),
        sp.GetRequiredService<ILogger<LlmEntityExtractor>>());
});

builder.Services.AddSingleton<PdfOcrPipeline>();

WebApplication app = builder.Build();
ILogger logger = app.Logger;

IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
});

app.MapGet("/health", (OcrEngine ocr, LlmRuntimeConfig llm, OcrRuntimeConfig cfg) => Results.Json(
    new HealthResponse
    {
        Status = "ok",
        ModelsLoaded = ocr.IsLoaded,
        Runtime = RuntimeInformation.FrameworkDescription,
        ProcessorCount = cfg.ProcessorCount,
        EngineCount = ocr.EngineCount,
        LineWorkerCount = ocr.LineWorkerCount,
        DetIntraOpThreads = ocr.DetIntraOpThreads,
        DefaultDpi = ocr.Config.DefaultDpi,
        UseDirectionClassification = ocr.UseDirectionClassification,
        RasterWorkerCount = ocr.Config.RasterWorkerCount,
        RecBatchLines = ocr.Config.RecBatchLines,
        DetLimitSideLength = ocr.Config.DetLimitSideLength,
        AutoScaleFromCpu = cfg.AutoScaleFromCpu,
        ConfigPath = configPath,
        LlmEnabled = llm.Enabled,
        LlmUsable = llm.IsUsable,
        LlmModel = llm.Model,
        LlmBaseUrl = llm.BaseUrl,
        LlmFallbackToHeuristics = llm.FallbackToHeuristics,
    },
    AppJsonContext.Default.HealthResponse));

app.MapPost("/ocr", async Task<IResult> (
    HttpRequest httpRequest,
    ParallelPdfDownloader downloader,
    PdfOcrPipeline pipeline,
    OcrRuntimeConfig config,
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
                Error = "Request body must be JSON: { \"url\": \"https://.../file.pdf\", \"dpi\": 96 }",
            },
            AppJsonContext.Default.OcrResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    int? dpi = body?.Dpi;
    if (dpi is null &&
        httpRequest.Query.TryGetValue("dpi", out var dpiQuery) &&
        int.TryParse(dpiQuery.FirstOrDefault(), out int dpiFromQuery))
    {
        dpi = dpiFromQuery;
    }

    dpi ??= config.DefaultDpi;

    try
    {
        ParallelPdfDownloader.DownloadResult download =
            await downloader.DownloadAsync(url, ct).ConfigureAwait(false);
        using (download.Buffer)
        {
            OcrResponse response = await pipeline
                .ProcessAsync(download.Buffer, download.ElapsedMs, download.Mode, ct, dpi)
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
    "MiniOcr AOT API\n" +
    "POST /ocr  {\"url\":\"https://.../file.pdf\",\"dpi\":96}\n" +
    "GET  /health\n" +
    $"Config: {configPath}\n" +
    "Env OCR: MINIOCR_ENGINES MINIOCR_DPI MINIOCR_LINE_WORKERS MINIOCR_DET_THREADS MINIOCR_USE_CLS MINIOCR_RASTER_WORKERS\n" +
    "Env LLM: MINIOCR_LLM_API_KEY MINIOCR_LLM_BASE_URL MINIOCR_LLM_MODEL MINIOCR_LLM_MAX_CONCURRENCY\n",
    "text/plain; charset=utf-8"));

string urls = string.Join(", ", app.Urls.DefaultIfEmpty("(default http://localhost:5000)"));
logger.LogInformation(
    "MiniOcr ready — ProcessorCount={Cores}, engines={Engines}, lineWorkers={LineWorkers}, det={Det}, raster={Raster}, dpi={Dpi}, useCls={UseCls}, llmUsable={Llm}, listening={Urls}",
    runtimeConfig.ProcessorCount,
    engine.EngineCount,
    engine.LineWorkerCount,
    engine.DetIntraOpThreads,
    engine.Config.RasterWorkerCount,
    engine.Config.DefaultDpi,
    engine.UseDirectionClassification,
    llmConfig.IsUsable,
    urls);

await app.RunAsync();
