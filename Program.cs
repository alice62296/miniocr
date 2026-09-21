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

builder.Services.AddHttpClient(ChallengeJobService.CallbackHttpClientName, client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MiniOcr/1.0 (+challenge-callback)");
});

using ILoggerFactory bootstrapLogs = LoggerFactory.Create(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
});
ILogger bootstrapLogger = bootstrapLogs.CreateLogger("MiniOcr.Startup");

AppConfigStore.LoadResult configLoad = AppConfigStore.LoadOrCreate(bootstrapLogger);
AppConfigFile appConfig = configLoad.Config;
string configPath = configLoad.ConfigPath;
bool configFileExisted = configLoad.ConfigFileExisted;
LlmRuntimeConfig llmConfig = AppConfigStore.ResolveLlm(appConfig);
OcrRuntimeConfig runtimeConfig = OcrRuntimeConfig.FromAppConfig(appConfig);
string apiKeyStatus = string.IsNullOrEmpty(llmConfig.ApiKey) ? "(empty)" : "(set)";

Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
Console.WriteLine(
    $"Config: path={configPath} existed={configFileExisted} source={configLoad.PathSource}");
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
    $"fallbackToHeuristics={llmConfig.FallbackToHeuristics}, apiKey={apiKeyStatus}");
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
builder.Services.AddSingleton<ChallengeJobService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChallengeJobService>());

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
        ConfigFileExisted = configFileExisted,
        ConfigPathSource = configLoad.PathSource,
        LlmEnabled = llm.Enabled,
        LlmUsable = llm.IsUsable,
        LlmModel = llm.Model,
        LlmBaseUrl = llm.BaseUrl,
        LlmFallbackToHeuristics = llm.FallbackToHeuristics,
        LlmApiKey = apiKeyStatus,
    },
    AppJsonContext.Default.HealthResponse));

async Task<IResult> HandleChallengeAsync(
    HttpRequest httpRequest,
    ChallengeJobService jobs,
    CancellationToken ct)
{
    ChallengeRequest? body;
    try
    {
        body = await httpRequest.ReadFromJsonAsync(AppJsonContext.Default.ChallengeRequest, ct)
            .ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Challenge JSON parse failed");
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "Invalid JSON body." },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (body is null)
    {
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "Empty body." },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (string.IsNullOrWhiteSpace(body.Key) ||
        string.IsNullOrWhiteSpace(body.CallbackUrl) ||
        body.Files is null ||
        body.Files.Count == 0)
    {
        return Results.Json(
            new ChallengeAckResponse
            {
                Ok = false,
                Error = "Required: key, callbackUrl, files[] (each with fileId + url).",
            },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (!Uri.TryCreate(body.CallbackUrl, UriKind.Absolute, out Uri? cb) ||
        (cb.Scheme != Uri.UriSchemeHttp && cb.Scheme != Uri.UriSchemeHttps))
    {
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "callbackUrl must be absolute http(s)." },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    List<ChallengeFileRef> files = [];
    foreach (ChallengeFileRef f in body.Files)
    {
        if (string.IsNullOrWhiteSpace(f.Url) || string.IsNullOrWhiteSpace(f.FileId))
            continue;
        if (!Uri.TryCreate(f.Url, UriKind.Absolute, out Uri? fu) ||
            (fu.Scheme != Uri.UriSchemeHttp && fu.Scheme != Uri.UriSchemeHttps))
            continue;
        files.Add(f);
    }

    if (files.Count == 0)
    {
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "No valid files (need fileId + http(s) url)." },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    ChallengeJob job = new()
    {
        TeamId = body.TeamId,
        Key = body.Key.Trim(),
        CallbackUrl = body.CallbackUrl.Trim(),
        Files = files,
    };

    // Non-blocking enqueue when possible; if queue full, brief wait then still ack
    // (platform requires fast 200 — do not run OCR on this thread).
    if (!jobs.TryEnqueue(job))
    {
        using CancellationTokenSource waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await jobs.EnqueueAsync(job, waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Challenge queue full; rejecting teamId={TeamId}, keyPresent=true, files={Count}",
                job.TeamId,
                job.Files.Count);
            return Results.Json(
                new ChallengeAckResponse { Ok = false, Error = "Server busy; retry shortly." },
                AppJsonContext.Default.ChallengeAckResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    logger.LogInformation(
        "Challenge accepted: teamId={TeamId}, keyPresent=true, files={Count}, queuedApprox={Queued}",
        job.TeamId,
        job.Files.Count,
        jobs.QueuedApprox);

    return Results.Json(
        new ChallengeAckResponse { Ok = true },
        AppJsonContext.Default.ChallengeAckResponse);
}

// Competition primary serviceUrl path (no auth).
app.MapPost("/challenge", HandleChallengeAsync);
// Also accept POST / so serviceUrl can be the bare base URL.
app.MapPost("/", HandleChallengeAsync);

app.MapPost("/ocr", async Task<IResult> (
    HttpRequest httpRequest,
    ParallelPdfDownloader downloader,
    PdfOcrPipeline pipeline,
    OcrRuntimeConfig config,
    CancellationToken ct) =>
{
    // Sync debug OCR: competition-compatible request/response shapes (same builders as callback).
    OcrDebugRequest? body;
    try
    {
        body = await httpRequest.ReadFromJsonAsync(AppJsonContext.Default.OcrDebugRequest, ct)
            .ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Debug /ocr JSON parse failed");
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "Invalid JSON body." },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (body is null)
    {
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "Empty body." },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    List<ChallengeFileRef> files = [];
    if (body.Files is { Count: > 0 })
    {
        foreach (ChallengeFileRef f in body.Files)
        {
            if (string.IsNullOrWhiteSpace(f.Url) || string.IsNullOrWhiteSpace(f.FileId))
                continue;
            if (!Uri.TryCreate(f.Url, UriKind.Absolute, out Uri? fu) ||
                (fu.Scheme != Uri.UriSchemeHttp && fu.Scheme != Uri.UriSchemeHttps))
                continue;
            files.Add(f);
        }
    }
    else if (!string.IsNullOrWhiteSpace(body.Url))
    {
        // Legacy { "url", "dpi" } → files:[{fileId:"f1",url}]
        if (!Uri.TryCreate(body.Url, UriKind.Absolute, out Uri? fu) ||
            (fu.Scheme != Uri.UriSchemeHttp && fu.Scheme != Uri.UriSchemeHttps))
        {
            return Results.Json(
                new ChallengeAckResponse { Ok = false, Error = "url must be absolute http(s)." },
                AppJsonContext.Default.ChallengeAckResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        files.Add(new ChallengeFileRef { FileId = "f1", Url = body.Url.Trim() });
    }

    if (files.Count == 0)
    {
        return Results.Json(
            new ChallengeAckResponse
            {
                Ok = false,
                Error =
                    "Required: files[{fileId,url}] (competition shape), or legacy { url, dpi? }. " +
                    "Optional: teamId, key, callbackUrl (ignored for sync).",
            },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }

    int? dpi = body.Dpi;
    if (dpi is null &&
        httpRequest.Query.TryGetValue("dpi", out var dpiQuery) &&
        int.TryParse(dpiQuery.FirstOrDefault(), out int dpiFromQuery))
    {
        dpi = dpiFromQuery;
    }

    dpi ??= config.DefaultDpi;

    List<ChallengeFileResult> results = [];
    try
    {
        foreach (ChallengeFileRef file in files)
        {
            string fileId = file.FileId ?? "f1";
            string url = file.Url!;
            ParallelPdfDownloader.DownloadResult download =
                await downloader.DownloadAsync(url, ct).ConfigureAwait(false);
            using (download.Buffer)
            {
                OcrResponse ocr = await pipeline
                    .ProcessAsync(download.Buffer, download.ElapsedMs, download.Mode, ct, dpi)
                    .ConfigureAwait(false);
                results.Add(ChallengeResultMapper.BuildFileResult(fileId, ocr));
            }
        }
    }
    catch (ArgumentException ex)
    {
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = ex.Message },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }
    catch (HttpRequestException ex)
    {
        logger.LogWarning(ex, "Debug /ocr download failed");
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "Failed to download PDF: " + ex.Message },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = ex.Message },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Debug /ocr pipeline failed");
        return Results.Json(
            new ChallengeAckResponse { Ok = false, Error = "OCR failed: " + ex.Message },
            AppJsonContext.Default.ChallengeAckResponse,
            statusCode: StatusCodes.Status500InternalServerError);
    }

    ChallengeCallbackBody callbackShaped = new()
    {
        TeamId = body.TeamId,
        Key = string.IsNullOrWhiteSpace(body.Key) ? "debug" : body.Key.Trim(),
        Result = results,
    };
    return Results.Json(callbackShaped, AppJsonContext.Default.ChallengeCallbackBody);
});

app.MapGet("/", () => Results.Text(
    "MiniOcr AOT API\n" +
    "POST /challenge  ← competition serviceUrl (also POST /)\n" +
    "  {\"teamId\":123,\"key\":\"...\",\"callbackUrl\":\"https://...\",\"files\":[{\"fileId\":\"f1\",\"url\":\"https://...pdf\"}]}\n" +
    "  → HTTP 200 {\"ok\":true} immediately; results POSTed async to callbackUrl\n" +
    "POST /ocr        debug sync OCR (competition shapes; response = callback body)\n" +
    "  {\"teamId\":0,\"key\":\"debug\",\"files\":[{\"fileId\":\"f1\",\"url\":\"https://...pdf\"}]}\n" +
    "  or legacy {\"url\":\"https://.../file.pdf\"} / ?dpi=96\n" +
    "GET  /health\n" +
    $"Config: path={configPath} existed={configFileExisted} source={configLoad.PathSource} llm.usable={llmConfig.IsUsable} apiKey={apiKeyStatus}\n" +
    "Env CONFIG: MINIOCR_CONFIG_PATH\n" +
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
