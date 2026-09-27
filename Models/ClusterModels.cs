namespace MiniOcr.Models;

/// <summary>
/// <c>config.json</c> <c>cluster</c> section. Absent or <c>enabled: false</c> → single-node OCR.
/// </summary>
public sealed class ClusterFileConfig
{
    public bool Enabled { get; set; }
    /// <summary><c>coordinator</c>, <c>worker</c>, or <c>both</c>.</summary>
    public string Role { get; set; } = "coordinator";
    public string NodeId { get; set; } = "";
    /// <summary>URL other nodes use to reach this process (no trailing path).</summary>
    public string AdvertiseUrl { get; set; } = "";
    /// <summary>Shared bearer secret. Never logged. Required when enabled.</summary>
    public string Token { get; set; } = "";
    /// <summary>Worker: coordinator base URL to register with and pull work from.</summary>
    public string CoordinatorUrl { get; set; } = "";
    /// <summary>0 / null = this node's engine count (llm mode: vision concurrency).</summary>
    public int? Capacity { get; set; }
    /// <summary>0 / null = auto from capacity, clamped to 1–16, shrunk on the tail.</summary>
    public int? PagesPerBatch { get; set; }
    /// <summary>Minimum lease for a claimed batch. Floor for one page; longer batches add <see cref="PageTimeoutSeconds"/>.</summary>
    public int LeaseSeconds { get; set; } = 20;
    /// <summary>Extra lease seconds per page in a batch.</summary>
    public int PageTimeoutSeconds { get; set; } = 20;
    public int HealthIntervalSeconds { get; set; } = 5;
    /// <summary>Backstop: after this, remote leases are dropped and the coordinator finishes leftovers locally.</summary>
    public int JobDeadlineSeconds { get; set; } = 300;
    /// <summary>
    /// While remotes are expected, the coordinator holds itself to one local window
    /// until this many milliseconds pass or the grace elapses. Stops a fast coordinator
    /// from finishing a tiny PDF before workers download it. Big jobs are unaffected
    /// once the window is full and the grace (default 500ms) has passed.
    /// </summary>
    public int JoinGraceMs { get; set; } = 500;
    /// <summary>When this many pages are still leased and nothing is pending, idle nodes may copy the tail.</summary>
    public int SpeculativeTailPages { get; set; } = 4;
    public List<ClusterWorkerFileConfig>? Workers { get; set; }
}

public sealed class ClusterWorkerFileConfig
{
    public string Url { get; set; } = "";
    /// <summary>0 / null = discover from the worker's <c>/cluster/info</c>.</summary>
    public int? Capacity { get; set; }
}

public sealed class ClusterHealthInfo
{
    public bool Enabled { get; set; }
    public string Role { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string AdvertiseUrl { get; set; } = "";
    public string OcrMode { get; set; } = "";
    public string Model { get; set; } = "";
    public int Dpi { get; set; }
    public int Capacity { get; set; }
    public bool TokenSet { get; set; }
    public List<ClusterNodeHealth> Nodes { get; set; } = [];
    public ClusterLastJobHealth? LastJob { get; set; }
}

public sealed class ClusterNodeHealth
{
    public string NodeId { get; set; } = "";
    public string? Url { get; set; }
    public bool Local { get; set; }
    public bool Healthy { get; set; }
    public int Capacity { get; set; }
    public int InFlight { get; set; }
    public int PagesDone { get; set; }
    public string OcrMode { get; set; } = "";
    public string Model { get; set; } = "";
    public int Dpi { get; set; }
    public string? Warning { get; set; }
}

public sealed class ClusterLastJobHealth
{
    public string JobId { get; set; } = "";
    public int PageCount { get; set; }
    public double ElapsedMs { get; set; }
    public string PageTextSha256 { get; set; } = "";
    public List<ClusterNodePages> Nodes { get; set; } = [];
}

public sealed class ClusterNodePages
{
    public string NodeId { get; set; } = "";
    public int Pages { get; set; }
}

public sealed class ClusterRegisterRequest
{
    public string NodeId { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public int Capacity { get; set; }
    public string OcrMode { get; set; } = "";
    public string Model { get; set; } = "";
    public int Dpi { get; set; }
    public int EngineCount { get; set; }
}

public sealed class ClusterRegisterResponse
{
    public bool Ok { get; set; }
    public string? Warning { get; set; }
    public string OcrMode { get; set; } = "";
    public string Model { get; set; } = "";
    public int Dpi { get; set; }
}

public sealed class ClusterHeartbeatRequest
{
    public string NodeId { get; set; } = "";
    public int Capacity { get; set; }
    public int InFlight { get; set; }
    public bool Healthy { get; set; } = true;
}

public sealed class ClusterInfoResponse
{
    public string NodeId { get; set; } = "";
    public string Role { get; set; } = "";
    public int Capacity { get; set; }
    public string OcrMode { get; set; } = "";
    public string Model { get; set; } = "";
    public int Dpi { get; set; }
    public int EngineCount { get; set; }
    public bool Healthy { get; set; } = true;
    public int ActiveSessions { get; set; }
    public int PagesDone { get; set; }
}

public sealed class ClusterNotifyRequest
{
    public string JobId { get; set; } = "";
    public string CoordinatorUrl { get; set; } = "";
    public int Dpi { get; set; }
    public int PageCount { get; set; }
}

public sealed class ClusterDispatchRequest
{
    public string NodeId { get; set; } = "";
    public int Capacity { get; set; }
    /// <summary>Jobs this worker is already downloading or OCR-ing. The coordinator will offer a different open job.</summary>
    public List<string>? ActiveJobs { get; set; }
}

public sealed class ClusterDispatchResponse
{
    public bool Wait { get; set; }
    public int RetryAfterMs { get; set; } = 300;
    public string? JobId { get; set; }
    public int Dpi { get; set; }
    public int PageCount { get; set; }
    /// <summary>Absolute path on the coordinator, e.g. <c>/cluster/jobs/{id}/pdf</c>.</summary>
    public string? PdfPath { get; set; }
}

public sealed class ClusterJoinRequest
{
    public string NodeId { get; set; } = "";
    public int Capacity { get; set; }
}

public sealed class ClusterClaimRequest
{
    public string NodeId { get; set; } = "";
    public int MaxPages { get; set; }
}

public sealed class ClusterClaimResponse
{
    public bool Done { get; set; }
    public bool Wait { get; set; }
    public string? BatchId { get; set; }
    /// <summary>1-based page numbers.</summary>
    public List<int>? Pages { get; set; }
    public int LeaseMs { get; set; }
    public int RetryAfterMs { get; set; }
    public bool Speculative { get; set; }
}

public sealed class ClusterResultRequest
{
    public string NodeId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public List<OcrPageResult>? Pages { get; set; }
}

public sealed class ClusterFailRequest
{
    public string NodeId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string? Error { get; set; }
}

public sealed class ClusterAck
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int Accepted { get; set; }
}
