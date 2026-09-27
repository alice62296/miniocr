using MiniOcr.Models;
using MiniOcr.Services;

int failed = 0;

void AssertTrue(bool cond, string msg)
{
    if (cond)
    {
        Console.WriteLine("  PASS  " + msg);
        return;
    }

    Console.WriteLine("  FAIL  " + msg);
    failed++;
}

ClusterPageScheduler NewScheduler(
    int pages,
    int expectedNodes = 1,
    int pagesPerBatch = 0,
    int tail = 4,
    int leaseFloorMs = 1_000,
    int pageTimeoutMs = 1_000)
{
    return new ClusterPageScheduler(new ClusterScheduleOptions
    {
        PageCount = pages,
        LocalNodeId = "local",
        PagesPerBatch = pagesPerBatch,
        LeaseFloorMs = leaseFloorMs,
        PageTimeoutMs = pageTimeoutMs,
        LeaseCapMs = 60_000,
        SpeculativeTailPages = tail,
        ExpectedNodes = expectedNodes,
    });
}

void CommitAll(ClusterPageScheduler sched, ClusterClaim claim)
{
    foreach (int page in claim.Pages)
        AssertTrue(sched.TryCommit(claim.BatchId, page), $"commit page {page} batch {claim.BatchId}");
}

Console.WriteLine("=== config defaults off ===");
ClusterRuntimeConfig off = ClusterRuntimeConfig.Resolve(new AppConfigFile(), _ => null);
AssertTrue(!off.Enabled, "missing cluster section is off");
AssertTrue(!off.IsWorker || off.Role == "coordinator", "default role is coordinator");
AssertTrue(off.Role == "coordinator", "role coordinator");

Console.WriteLine("=== empty token forces off ===");
ClusterRuntimeConfig noToken = ClusterRuntimeConfig.Resolve(
    new AppConfigFile
    {
        Cluster = new ClusterFileConfig { Enabled = true, Token = "  " },
    },
    _ => null);
AssertTrue(!noToken.Enabled, "blank token disables cluster");
AssertTrue(noToken.DisabledReason is not null, "disabled reason set");

Console.WriteLine("=== env overrides file ===");
var env = new Dictionary<string, string?>(StringComparer.Ordinal)
{
    ["MINIOCR_CLUSTER_ENABLED"] = "1",
    ["MINIOCR_CLUSTER_ROLE"] = "worker",
    ["MINIOCR_CLUSTER_TOKEN"] = "s3cret",
    ["MINIOCR_CLUSTER_NODE_ID"] = "mac-1",
    ["MINIOCR_CLUSTER_WORKERS"] = "http://10.0.0.2:5081/, http://10.0.0.3:5082",
    ["MINIOCR_CLUSTER_CAPACITY"] = "3",
    ["MINIOCR_CLUSTER_PAGES_PER_BATCH"] = "5",
    ["MINIOCR_CLUSTER_JOIN_GRACE_MS"] = "2500",
};
ClusterRuntimeConfig on = ClusterRuntimeConfig.Resolve(
    new AppConfigFile
    {
        Cluster = new ClusterFileConfig
        {
            Enabled = false,
            Role = "coordinator",
            Token = "file-token",
            Workers = [new ClusterWorkerFileConfig { Url = "http://ignored", Capacity = 9 }],
        },
    },
    name => env.TryGetValue(name, out string? v) ? v : null);
AssertTrue(on.Enabled && on.IsWorker && !on.IsCoordinator, "env role worker");
AssertTrue(on.Token == "s3cret" && on.NodeId == "mac-1", "env token and node id");
AssertTrue(on.Workers.Count == 2 && on.Workers[0].Url == "http://10.0.0.2:5081", "env worker list trimmed");
AssertTrue(on.Capacity == 3 && on.PagesPerBatch == 5 && on.JoinGraceMs == 2500, "numeric env overrides");
AssertTrue(on.EffectiveCapacity(8, 32, llmMode: false) == 3, "explicit capacity wins over engines");
AssertTrue(
    ClusterRuntimeConfig.Resolve(new AppConfigFile(), _ => null).EffectiveCapacity(0, 32, llmMode: true) == 32,
    "llm capacity falls back to vision concurrency");

Console.WriteLine("=== auth ===");
AssertTrue(ClusterAuth.FixedEquals("s3cret", "s3cret"), "matching token");
AssertTrue(!ClusterAuth.FixedEquals("s3cret", "s3creT"), "case sensitive");
AssertTrue(!ClusterAuth.FixedEquals("", "s3cret"), "empty provided");
AssertTrue(!ClusterAuth.FixedEquals("s3cret", ""), "empty expected");
AssertTrue(!ClusterAuth.FixedEquals(null, "x"), "null provided");

Console.WriteLine("=== pull balance: larger capacity takes more pages ===");
{
    ClusterPageScheduler sched = NewScheduler(20, expectedNodes: 2);
    sched.SetCapacity("slow", 1);
    sched.SetCapacity("fast", 4);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    int guard = 0;
    while (!sched.IsComplete && guard++ < 100)
    {
        ClusterClaim slow = sched.Claim("slow", 8, now);
        if (slow.Kind == ClusterClaimKind.Batch)
            CommitAll(sched, slow);
        ClusterClaim fast = sched.Claim("fast", 8, now);
        if (fast.Kind == ClusterClaimKind.Batch)
            CommitAll(sched, fast);
        if (slow.Kind != ClusterClaimKind.Batch && fast.Kind != ClusterClaimKind.Batch)
            break;
    }

    ClusterScheduleSnapshot snap = sched.Snapshot();
    int slowPages = snap.Nodes.FirstOrDefault(n => n.NodeId == "slow")?.PagesCommitted ?? 0;
    int fastPages = snap.Nodes.FirstOrDefault(n => n.NodeId == "fast")?.PagesCommitted ?? 0;
    AssertTrue(sched.IsComplete, "fairness run completed");
    AssertTrue(slowPages + fastPages == 20, $"all 20 pages attributed ({slowPages}+{fastPages})");
    AssertTrue(fastPages > slowPages, $"fast node got more pages (fast={fastPages} slow={slowPages})");
}

Console.WriteLine("=== in-flight cap and tail shrink ===");
{
    ClusterPageScheduler sched = NewScheduler(6, expectedNodes: 6, pagesPerBatch: 8);
    sched.SetCapacity("a", 2);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    ClusterClaim first = sched.Claim("a", 8, now);
    AssertTrue(first.Kind == ClusterClaimKind.Batch && first.Pages.Length == 1, "tail shrink hands out 1 page when pending <= nodes");
    ClusterClaim second = sched.Claim("a", 8, now);
    AssertTrue(second.Kind == ClusterClaimKind.Batch && second.Pages.Length == 1, "second page while room remains");
    ClusterClaim third = sched.Claim("a", 8, now);
    AssertTrue(third.Kind == ClusterClaimKind.Wait, "in-flight cap blocks a third page");
    AssertTrue(!sched.TryCommit("nope", first.Pages[0]), "unknown batch rejected");
    AssertTrue(sched.TryCommit(first.BatchId, first.Pages[0]), "first commit");
    AssertTrue(!sched.TryCommit(first.BatchId, first.Pages[0]), "duplicate commit ignored");
}

Console.WriteLine("=== expired lease is retried by another node ===");
{
    DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    ClusterPageScheduler sched = NewScheduler(3, expectedNodes: 1, pagesPerBatch: 3, tail: 1, leaseFloorMs: 5_000, pageTimeoutMs: 1_000);
    sched.SetCapacity("dead", 3);
    sched.SetCapacity("live", 3);
    ClusterClaim leased = sched.Claim("dead", 3, now);
    AssertTrue(leased.Kind == ClusterClaimKind.Batch && leased.Pages.Length == 3, "dead node took 3 pages");
    ClusterClaim blocked = sched.Claim("live", 3, now.AddSeconds(1));
    AssertTrue(blocked.Kind == ClusterClaimKind.Wait, "live node waits while lease holds");
    ClusterClaim retry = sched.Claim("live", 3, now.AddMilliseconds(leased.LeaseMs + 1));
    AssertTrue(retry.Kind == ClusterClaimKind.Batch, "after expiry the other node gets the pages");
    AssertTrue(retry.Pages.OrderBy(p => p).SequenceEqual(leased.Pages.OrderBy(p => p)), "same pages retried");
    CommitAll(sched, retry);
    AssertTrue(sched.IsComplete, "job completes on the retry node");
    AssertTrue(!sched.TryCommit(leased.BatchId, leased.Pages[0]), "late owner cannot overwrite");
    ClusterScheduleSnapshot snap = sched.Snapshot();
    int livePages = snap.Nodes.First(n => n.NodeId == "live").PagesCommitted;
    int deadPages = snap.Nodes.FirstOrDefault(n => n.NodeId == "dead")?.PagesCommitted ?? 0;
    AssertTrue(livePages == 3 && deadPages == 0, "only the node that finished is credited");
}

Console.WriteLine("=== drop node requeues immediately ===");
{
    ClusterPageScheduler sched = NewScheduler(2, pagesPerBatch: 2);
    sched.SetCapacity("gone", 2);
    sched.SetCapacity("local", 2);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    ClusterClaim claim = sched.Claim("gone", 2, now);
    AssertTrue(claim.Pages.Length == 2, "node took both pages");
    sched.DropNode("gone", now);
    ClusterClaim local = sched.Claim("local", 2, now);
    AssertTrue(local.Kind == ClusterClaimKind.Batch && local.Pages.Length == 2, "local picked up dropped pages");
    CommitAll(sched, local);
    AssertTrue(sched.IsComplete, "complete after drop");
}

Console.WriteLine("=== speculative tail ===");
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    ClusterPageScheduler sched = NewScheduler(2, expectedNodes: 1, pagesPerBatch: 2, tail: 2, leaseFloorMs: 30_000);
    sched.SetCapacity("slow", 2);
    sched.SetCapacity("idle", 2);
    ClusterClaim slow = sched.Claim("slow", 2, now);
    AssertTrue(slow.Kind == ClusterClaimKind.Batch && slow.Pages.Length == 2, "slow node holds the tail");
    ClusterClaim copy = sched.Claim("idle", 2, now);
    AssertTrue(copy.Kind == ClusterClaimKind.Batch && copy.Speculative, "idle node speculatively copies the tail");
    AssertTrue(copy.Pages.OrderBy(p => p).SequenceEqual(slow.Pages.OrderBy(p => p)), "speculative pages match");
    CommitAll(sched, copy);
    AssertTrue(sched.IsComplete, "speculative finish completes the job");
    foreach (int page in slow.Pages)
        AssertTrue(!sched.TryCommit(slow.BatchId, page), $"original lease page {page} lost the race");
    ClusterScheduleSnapshot snap = sched.Snapshot();
    AssertTrue(snap.Nodes.First(n => n.NodeId == "idle").PagesCommitted == 2, "idle node credited");
    AssertTrue((snap.Nodes.FirstOrDefault(n => n.NodeId == "slow")?.PagesCommitted ?? 0) == 0, "slow node not credited");
}

Console.WriteLine("=== local window hold leaves pages for remotes ===");
{
    ClusterPageScheduler sched = NewScheduler(5, expectedNodes: 3, pagesPerBatch: 4);
    sched.SetCapacity("local", 1);
    sched.SetCapacity("w", 1);
    sched.SetHoldLocalWindow(true);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    ClusterClaim local = sched.Claim("local", 4, now);
    AssertTrue(local.Kind == ClusterClaimKind.Batch && local.Pages.Length == 1, "local takes one page");
    CommitAll(sched, local);
    ClusterClaim held = sched.Claim("local", 4, now);
    AssertTrue(held.Kind == ClusterClaimKind.Wait, "local held at its window");
    ClusterClaim remote = sched.Claim("w", 4, now);
    AssertTrue(remote.Kind == ClusterClaimKind.Batch, "remote still receives pages during the hold");
    sched.SetHoldLocalWindow(false);
    ClusterClaim again = sched.Claim("local", 4, now);
    AssertTrue(again.Kind == ClusterClaimKind.Batch, "clearing the hold lets local continue");
}

Console.WriteLine("=== takeover drops only remote leases ===");
{
    ClusterPageScheduler sched = NewScheduler(4, pagesPerBatch: 2);
    sched.SetCapacity("local", 2);
    sched.SetCapacity("remote", 2);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    ClusterClaim local = sched.Claim("local", 2, now);
    ClusterClaim remote = sched.Claim("remote", 2, now);
    AssertTrue(local.Kind == ClusterClaimKind.Batch && remote.Kind == ClusterClaimKind.Batch, "both nodes leased work");
    sched.TakeOverLocal(now);
    AssertTrue(sched.TryCommit(local.BatchId, local.Pages[0]), "local lease survived takeover");
    ClusterClaim rest = sched.Claim("local", 4, now);
    AssertTrue(rest.Kind == ClusterClaimKind.Batch, "remote pages came back to local");
}

Console.WriteLine("=== model mismatch warning ===");
AssertTrue(
    ClusterNodeRegistry.Mismatch("llm", "gpt-4o", 72, "local", "ChineseV6Tiny", 96) is not null,
    "mode, model, and dpi differences are reported");
AssertTrue(
    ClusterNodeRegistry.Mismatch("local", "ChineseV6Tiny", 96, "local", "ChineseV6Tiny", 96) is null,
    "identical nodes produce no warning");

Console.WriteLine("=== NER groups stay in document order when OCR finishes out of order ===");
{
    var buffer = new LlmPageGrouper.OrderedBuffer(12, pagesPerRequest: 10, maxChars: 100_000);
    List<LlmPageGrouper.PageBatch> emitted = [];
    int[] order = [3, 1, 5, 2, 4, 8, 6, 10, 7, 9, 12, 11];
    foreach (int page in order)
    {
        emitted.AddRange(buffer.Add(new OcrPageResult
        {
            Page = page,
            Text = "page-" + page,
        }));
    }

    AssertTrue(emitted.Count == 1, "one group emitted once pages 1..10 exist");
    AssertTrue(
        emitted[0].PageNumbers.SequenceEqual(Enumerable.Range(1, 10)),
        "group page numbers are 1..10 in order");
    List<LlmPageGrouper.PageBatch> tail = buffer.FlushRemainder();
    AssertTrue(tail.Count == 1 && tail[0].PageNumbers.SequenceEqual([11, 12]), "tail keeps 11 then 12");
    AssertTrue(!emitted[0].Text.Contains("--- page 11 ---", StringComparison.Ordinal), "later pages are not pulled forward");
}

if (failed > 0)
{
    Console.WriteLine($"FAILED {failed}");
    return 1;
}

Console.WriteLine("ALL PASSED");
return 0;
