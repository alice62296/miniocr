using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

int failed = 0;
void Pass(string msg) => Console.WriteLine("  PASS  " + msg);
void Fail(string msg)
{
    Console.WriteLine("  FAIL  " + msg);
    failed++;
}

string repo = FindRepo();
string dotnet = Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root
    ? Path.Combine(root, "dotnet")
    : "dotnet";
if (!File.Exists(dotnet))
    dotnet = "dotnet";

string sample = Path.Combine(repo, "samples", "sample-multipage.pdf");
string work = Path.Combine(Path.GetTempPath(), "miniocr-cluster-live-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
string repeatPdf = Path.Combine(work, "repeat.pdf");
int repeatPages = MakeRepeatedPdf(sample, repeatPdf, copies: 4);
Console.WriteLine($"work={work}");
Console.WriteLine($"sample={new FileInfo(sample).Length} bytes  repeatPages={repeatPages} repeatBytes={new FileInfo(repeatPdf).Length}");

using var server = new PdfServer(18090, new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["/sample-multipage.pdf"] = sample,
    ["/repeat.pdf"] = repeatPdf,
});
server.Start();

string sampleUrl = "http://127.0.0.1:18090/sample-multipage.pdf";
string repeatUrl = "http://127.0.0.1:18090/repeat.pdf";

var logs = new List<NodeProc>();
try
{
    Console.WriteLine("=== single-node ===");
    string singleCfg = WriteConfig(work, "single.json", role: "off", nodeId: "single", port: 18079, workers: []);
    NodeProc single = StartNode("single", "0", singleCfg, "http://127.0.0.1:18079");
    logs.Add(single);
    await WaitHealthy(single.BaseUrl, models: true, TimeSpan.FromMinutes(3));
    (string hashSample, long msSample) = await Ocr(single, sampleUrl, TimeSpan.FromMinutes(5));
    (string hashRepeat, long msRepeat) = await Ocr(single, repeatUrl, TimeSpan.FromMinutes(8));
    Console.WriteLine($"  single sample {msSample} ms  sha256={hashSample}");
    Console.WriteLine($"  single repeat {msRepeat} ms  sha256={hashRepeat} pages~{repeatPages}");
    Stop(single);

    Console.WriteLine("=== cluster: coordinator + 2 workers ===");
    string coordCfg = WriteConfig(work, "coord.json", "coordinator", "coord", 18081,
        ["http://127.0.0.1:18082", "http://127.0.0.1:18083"]);
    string workerACfg = WriteConfig(work, "worker-a.json", "worker", "worker-a", 18082, []);
    string workerBCfg = WriteConfig(work, "worker-b.json", "worker", "worker-b", 18083, []);
    NodeProc coord = StartNode("coord", "0", coordCfg, "http://127.0.0.1:18081");
    NodeProc workerA = StartNode("worker-a", "1", workerACfg, "http://127.0.0.1:18082");
    NodeProc workerB = StartNode("worker-b", "2", workerBCfg, "http://127.0.0.1:18083");
    logs.Add(coord);
    logs.Add(workerA);
    logs.Add(workerB);

    await WaitHealthy(coord.BaseUrl, models: true, TimeSpan.FromMinutes(3));
    await WaitHealthy(workerA.BaseUrl, models: true, TimeSpan.FromMinutes(3));
    await WaitHealthy(workerB.BaseUrl, models: true, TimeSpan.FromMinutes(3));
    await WaitForNodes(coord.BaseUrl, ["worker-a", "worker-b"], TimeSpan.FromSeconds(40));

    int hashesBeforeSample = CountHashes(coord);
    var sw = Stopwatch.StartNew();
    (int sampleCode, string sampleBody) = await PostOcr(coord.BaseUrl, sampleUrl, TimeSpan.FromMinutes(5));
    sw.Stop();
    long clusterSampleMs = sw.ElapsedMilliseconds;
    if (sampleCode != 200)
        Fail("cluster sample OCR HTTP " + sampleCode + " " + sampleBody[..Math.Min(400, sampleBody.Length)]);
    string clusterSampleHash = await WaitHash(coord, hashesBeforeSample, TimeSpan.FromSeconds(15));
    string health = await Get(coord.BaseUrl + "/health");
    string breakdown = FormatLastJob(health);
    Console.WriteLine($"  cluster sample {sw.ElapsedMilliseconds} ms  sha256={clusterSampleHash}");
    Console.WriteLine($"  lastJob {breakdown}");
    if (clusterSampleHash == hashSample)
        Pass("sample text hash matches single-node");
    else
        Fail($"sample hash mismatch single={hashSample} cluster={clusterSampleHash}");
    int nodesWithPages = CountNodesWithPages(health);
    if (nodesWithPages >= 2)
        Pass($"sample pages spread across {nodesWithPages} nodes ({breakdown})");
    else
        Fail($"expected pages on >= 2 nodes, got {breakdown}");
    if (coord.Log.Contains("byNode=", StringComparison.Ordinal))
        Pass("job log contains per-node breakdown");
    else
        Fail("missing byNode breakdown in coordinator log");

    Console.WriteLine("=== cluster repeat (no failures) ===");
    int hashesBeforeRepeat = CountHashes(coord);
    var repeatSw = Stopwatch.StartNew();
    (int repeatCode, string repeatBody) = await PostOcr(coord.BaseUrl, repeatUrl, TimeSpan.FromMinutes(5));
    repeatSw.Stop();
    if (repeatCode != 200)
        Fail("cluster repeat OCR HTTP " + repeatCode + " " + repeatBody[..Math.Min(400, repeatBody.Length)]);
    string clusterRepeatHash = await WaitHash(coord, hashesBeforeRepeat, TimeSpan.FromSeconds(15));
    string repeatHealth = await Get(coord.BaseUrl + "/health");
    Console.WriteLine($"  cluster repeat {repeatSw.ElapsedMilliseconds} ms  sha256={clusterRepeatHash}");
    Console.WriteLine($"  lastJob {FormatLastJob(repeatHealth)}");
    if (clusterRepeatHash == hashRepeat)
        Pass("repeat text hash matches single-node");
    else
        Fail($"repeat hash mismatch single={hashRepeat} cluster={clusterRepeatHash}");
    if (CountNodesWithPages(repeatHealth) >= 2)
        Pass("repeat pages spread across nodes (" + FormatLastJob(repeatHealth) + ")");
    else
        Fail("repeat pages did not spread: " + FormatLastJob(repeatHealth));

    Console.WriteLine("=== kill worker-b mid-job ===");
    int hashesBeforeKill = CountHashes(coord);
    var killSw = Stopwatch.StartNew();
    Task<(int Code, string Body)> killCall = PostOcr(coord.BaseUrl, repeatUrl, TimeSpan.FromMinutes(8));
    bool sawProgress = await WaitUntil(async () =>
    {
        if (killCall.IsCompleted)
            return true;
        string h = await Get(coord.BaseUrl + "/health");
        return PagesDone(h, "worker-b") > 0 || InFlight(h, "worker-b") > 0;
    }, TimeSpan.FromMinutes(3));
    if (!killCall.IsCompleted)
    {
        Console.WriteLine($"  killing worker-b (sawProgress={sawProgress})");
        Stop(workerB);
    }
    else
    {
        Fail("repeat job finished before worker-b could be killed");
    }

    (int killCode, string killBody) = await killCall;
    killSw.Stop();
    if (killCode != 200)
        Fail("kill-run HTTP " + killCode + " " + killBody[..Math.Min(500, killBody.Length)]);
    string killHash = await WaitHash(coord, hashesBeforeKill, TimeSpan.FromSeconds(20));
    string killHealth = await Get(coord.BaseUrl + "/health");
    Console.WriteLine($"  kill-run sha256={killHash}");
    Console.WriteLine($"  lastJob {FormatLastJob(killHealth)}");
    if (killHash == hashRepeat)
        Pass("after killing worker-b, repeat text hash matches single-node");
    else
        Fail($"kill-run hash mismatch single={hashRepeat} cluster={killHash}");
    if (killCode == 200)
        Pass("job completed after worker death");

    Console.WriteLine("TIMING");
    Console.WriteLine($"  single-node sample wallMs={msSample}");
    Console.WriteLine($"  single-node repeat wallMs={msRepeat}");
    Console.WriteLine($"  cluster sample wallMs={clusterSampleMs}");
    Console.WriteLine($"  cluster repeat wallMs={repeatSw.ElapsedMilliseconds}");
    Console.WriteLine($"  cluster repeat after killing worker-b wallMs={killSw.ElapsedMilliseconds}");
}
finally
{
    foreach (NodeProc node in logs)
        Stop(node);
    server.Stop();
}

if (failed > 0)
{
    Console.WriteLine($"FAILED {failed}");
    foreach (NodeProc node in logs)
    {
        Console.WriteLine($"----- {node.Name} log tail -----");
        string text = node.Log;
        int start = Math.Max(0, text.Length - 2500);
        Console.WriteLine(text[start..]);
    }

    return 1;
}

Console.WriteLine("ALL PASSED");
return 0;

static string FindRepo()
{
    DirectoryInfo? dir = new(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "MiniOcr.csproj")))
            return dir.FullName;
        dir = dir.Parent;
    }

    return Directory.GetCurrentDirectory();
}

static int MakeRepeatedPdf(string src, string dest, int copies)
{
    string py = """
        import sys
        from pypdf import PdfReader, PdfWriter
        src, dest, copies = sys.argv[1], sys.argv[2], int(sys.argv[3])
        reader = PdfReader(src)
        writer = PdfWriter()
        for _ in range(copies):
            for page in reader.pages:
                writer.add_page(page)
        with open(dest, "wb") as f:
            writer.write(f)
        print(len(writer.pages))
        """;
    string script = Path.Combine(Path.GetDirectoryName(dest)!, "repeat.py");
    File.WriteAllText(script, py);
    var psi = new ProcessStartInfo("python3")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add(script);
    psi.ArgumentList.Add(src);
    psi.ArgumentList.Add(dest);
    psi.ArgumentList.Add(copies.ToString());
    using Process? proc = Process.Start(psi) ?? throw new InvalidOperationException("python3 failed to start");
    string stdout = proc.StandardOutput.ReadToEnd();
    string stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    if (proc.ExitCode != 0)
        throw new InvalidOperationException("pypdf failed: " + stderr);
    return int.Parse(stdout.Trim().Split('\n')[^1]);
}

static string WriteConfig(string dir, string name, string role, string nodeId, int port, string[] workers)
{
    string workersJson = workers.Length == 0
        ? "[]"
        : "[" + string.Join(",", workers.Select(w => "{\"url\":\"" + w + "\",\"capacity\":1}")) + "]";
    bool enabled = role != "off";
    string coordinator = role == "worker" ? "http://127.0.0.1:18081" : "";
    string json = $$"""
        {
          "llm": { "enabled": false, "apiKey": "", "fallbackToHeuristics": false },
          "ocr": {
            "mode": "local",
            "dpi": 96,
            "engines": 1,
            "lineWorkers": 1,
            "detThreads": 1,
            "rasterWorkers": 1,
            "useCls": false,
            "autoScaleFromCpu": false
          },
          "cluster": {
            "enabled": {{(enabled ? "true" : "false")}},
            "role": "{{(enabled ? role : "coordinator")}}",
            "nodeId": "{{nodeId}}",
            "advertiseUrl": "http://127.0.0.1:{{port}}",
            "token": "live-test-token",
            "coordinatorUrl": "{{coordinator}}",
            "capacity": 1,
            "pagesPerBatch": 1,
            "leaseSeconds": 8,
            "pageTimeoutSeconds": 8,
            "healthIntervalSeconds": 2,
            "jobDeadlineSeconds": 300,
            "joinGraceMs": 20000,
            "speculativeTailPages": 2,
            "workers": {{workersJson}}
          }
        }
        """;
    string path = Path.Combine(dir, name);
    File.WriteAllText(path, json);
    return path;
}

static NodeProc StartNode(string name, string cores, string configPath, string baseUrl)
{
    string dotnet = Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root
        ? Path.Combine(root, "dotnet")
        : "dotnet";
    string repo = FindRepo();
    var psi = new ProcessStartInfo("taskset")
    {
        WorkingDirectory = repo,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add(cores);
    psi.ArgumentList.Add(dotnet);
    psi.ArgumentList.Add("run");
    psi.ArgumentList.Add("--no-build");
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add("Release");
    psi.ArgumentList.Add("--project");
    psi.ArgumentList.Add(Path.Combine(repo, "MiniOcr.csproj"));
    psi.Environment["ASPNETCORE_URLS"] = baseUrl;
    psi.Environment["MINIOCR_CONFIG_PATH"] = configPath;
    psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    psi.Environment["DOTNET_NOLOGO"] = "1";
    var node = new NodeProc(name, baseUrl);
    Process proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start " + name);
    node.Process = proc;
    proc.OutputDataReceived += (_, e) =>
    {
        if (e.Data is not null)
            node.Append(e.Data);
    };
    proc.ErrorDataReceived += (_, e) =>
    {
        if (e.Data is not null)
            node.Append(e.Data);
    };
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();
    Console.WriteLine($"  started {name} pid={proc.Id} {baseUrl} cores={cores}");
    return node;
}

static void Stop(NodeProc node)
{
    try
    {
        if (node.Process is { HasExited: false })
            node.Process.Kill(entireProcessTree: true);
    }
    catch
    {
    }
}

static async Task WaitHealthy(string baseUrl, bool models, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    string last = "";
    while (DateTime.UtcNow < until)
    {
        try
        {
            last = await Get(baseUrl + "/health");
            using JsonDocument doc = JsonDocument.Parse(last);
            bool loaded = doc.RootElement.TryGetProperty("modelsLoaded", out JsonElement m) && m.GetBoolean();
            string status = doc.RootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() ?? "" : "";
            if (status == "ok" && (!models || loaded))
                return;
        }
        catch
        {
        }

        await Task.Delay(500);
    }

    throw new TimeoutException($"health timeout {baseUrl}: {last}");
}

static async Task WaitForNodes(string baseUrl, string[] ids, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    string last = "";
    while (DateTime.UtcNow < until)
    {
        last = await Get(baseUrl + "/health");
        if (ids.All(id => last.Contains("\"nodeId\":\"" + id + "\"", StringComparison.Ordinal)))
            return;
        await Task.Delay(400);
    }

    throw new TimeoutException("nodes not visible: " + last);
}

static async Task<(string Hash, long Ms)> Ocr(NodeProc node, string pdfUrl, TimeSpan timeout)
{
    int before = CountHashes(node);
    var sw = Stopwatch.StartNew();
    (int code, string body) = await PostOcr(node.BaseUrl, pdfUrl, timeout);
    sw.Stop();
    if (code != 200)
        throw new InvalidOperationException("OCR " + code + " " + body[..Math.Min(500, body.Length)]);
    string hash = await WaitHash(node, before, TimeSpan.FromSeconds(15));
    return (hash, sw.ElapsedMilliseconds);
}

static async Task<(int Code, string Body)> PostOcr(string baseUrl, string pdfUrl, TimeSpan timeout)
{
    using var http = new HttpClient { Timeout = timeout + TimeSpan.FromSeconds(5) };
    string json = "{\"teamId\":0,\"key\":\"debug\",\"files\":[{\"fileId\":\"f1\",\"url\":\"" + pdfUrl + "\"}]}";
    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    using HttpResponseMessage resp = await http.PostAsync(baseUrl + "/ocr", content);
    string body = await resp.Content.ReadAsStringAsync();
    return ((int)resp.StatusCode, body);
}

static async Task<string> Get(string url)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    return await http.GetStringAsync(url);
}

static int CountHashes(NodeProc node) =>
    Regex.Matches(node.Log, @"OCR_TEXT_SHA256=([0-9a-f]+)", RegexOptions.CultureInvariant).Count;

static async Task<string> WaitHash(NodeProc node, int previousCount, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    var rx = new Regex(@"OCR_TEXT_SHA256=([0-9a-f]+)", RegexOptions.CultureInvariant);
    while (DateTime.UtcNow < until)
    {
        MatchCollection matches = rx.Matches(node.Log);
        if (matches.Count > previousCount)
            return matches[^1].Groups[1].Value;

        try
        {
            string health = await Get(node.BaseUrl + "/health");
            using JsonDocument doc = JsonDocument.Parse(health);
            if (doc.RootElement.TryGetProperty("cluster", out JsonElement cluster) &&
                cluster.ValueKind == JsonValueKind.Object &&
                cluster.TryGetProperty("lastJob", out JsonElement last) &&
                last.ValueKind == JsonValueKind.Object &&
                last.TryGetProperty("pageTextSha256", out JsonElement sha))
            {
                string? value = sha.GetString();
                if (!string.IsNullOrEmpty(value) && matches.Count > previousCount)
                    return value;
            }
        }
        catch
        {
        }

        await Task.Delay(200);
    }

    throw new TimeoutException("hash not found in " + node.Name + " log");
}

static string FormatLastJob(string health)
{
    using JsonDocument doc = JsonDocument.Parse(health);
    if (!doc.RootElement.TryGetProperty("cluster", out JsonElement cluster) ||
        !cluster.TryGetProperty("lastJob", out JsonElement last) ||
        last.ValueKind != JsonValueKind.Object)
        return "(no lastJob)";
    if (!last.TryGetProperty("nodes", out JsonElement nodes))
        return "(no nodes)";
    var parts = new List<string>();
    foreach (JsonElement node in nodes.EnumerateArray())
    {
        string id = node.GetProperty("nodeId").GetString() ?? "?";
        int pages = node.GetProperty("pages").GetInt32();
        parts.Add(id + "=" + pages);
    }

    return string.Join(", ", parts);
}

static int CountNodesWithPages(string health)
{
    using JsonDocument doc = JsonDocument.Parse(health);
    if (!doc.RootElement.TryGetProperty("cluster", out JsonElement cluster) ||
        !cluster.TryGetProperty("lastJob", out JsonElement last) ||
        !last.TryGetProperty("nodes", out JsonElement nodes))
        return 0;
    int n = 0;
    foreach (JsonElement node in nodes.EnumerateArray())
    {
        if (node.GetProperty("pages").GetInt32() > 0)
            n++;
    }

    return n;
}

static int PagesDone(string health, string nodeId) => NodeInt(health, nodeId, "pagesDone");
static int InFlight(string health, string nodeId) => NodeInt(health, nodeId, "inFlight");

static int NodeInt(string health, string nodeId, string field)
{
    using JsonDocument doc = JsonDocument.Parse(health);
    if (!doc.RootElement.TryGetProperty("cluster", out JsonElement cluster) ||
        !cluster.TryGetProperty("nodes", out JsonElement nodes))
        return 0;
    foreach (JsonElement node in nodes.EnumerateArray())
    {
        if (node.GetProperty("nodeId").GetString() == nodeId)
            return node.TryGetProperty(field, out JsonElement value) ? value.GetInt32() : 0;
    }

    return 0;
}

static async Task<bool> WaitUntil(Func<Task<bool>> probe, TimeSpan timeout)
{
    var until = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < until)
    {
        if (await probe())
            return true;
        await Task.Delay(250);
    }

    return false;
}

sealed class NodeProc(string name, string baseUrl)
{
    private readonly StringBuilder _log = new();
    private readonly object _gate = new();
    public string Name { get; } = name;
    public string BaseUrl { get; } = baseUrl;
    public Process? Process { get; set; }
    public string Log
    {
        get { lock (_gate) return _log.ToString(); }
    }

    public void Append(string line)
    {
        lock (_gate)
        {
            _log.AppendLine(line);
        }
    }
}

sealed class PdfServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, byte[]> _files;
    private CancellationTokenSource? _cts;

    public PdfServer(int port, Dictionary<string, string> paths)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _files = paths.ToDictionary(kv => kv.Key, kv => File.ReadAllBytes(kv.Value));
    }

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
    }

    public void Dispose() => Stop();

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            string path = ctx.Request.Url?.AbsolutePath ?? "";
            if (_files.TryGetValue(path, out byte[]? bytes))
            {
                ctx.Response.ContentType = "application/pdf";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }

            ctx.Response.Close();
        }
    }
}
