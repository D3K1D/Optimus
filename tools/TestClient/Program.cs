using System.Text.Json.Nodes;
using OptimizationTool.Transport;

// Runs the M1 path against a running host and prints what it sees.
//   OptimizationTool.TestClient [--pipe-name <name>] [--key <idempotencyKey>] [--intent audit.full|rescan] [--text "Begin scan"] [--base <reportId>]
// Run it twice with the same --key to see a reconnect replay return the same scanId.
var pipeName = PipeNames.ForCurrentSession();
var key = Guid.NewGuid().ToString();
var intent = "audit.full";
var text = "Begin scan";
string? baseReportId = null;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
    switch (args[i])
    {
        case "--pipe-name": pipeName = Next(); break;
        case "--key": key = Next(); break;
        case "--intent": intent = Next(); break;
        case "--text": text = Next(); break;
        case "--base": baseReportId = Next(); break;
        default: Console.Error.WriteLine($"Unknown argument {args[i]}."); return 64;
    }
}

await using var client = await JsonRpcClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(5));
var finished = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
string? scanStream = null;
client.Notification += (method, p) =>
{
    if (method != "events.notify" || p is null) return;
    Console.WriteLine($"  event {p["stream"]} #{p["seq"]} {p["type"]} {p["payload"]?.ToJsonString()}");
    if ((string?)p["type"] == "scan.finished" && (string?)p["stream"] == Volatile.Read(ref scanStream)) finished.TrySetResult(p["payload"]!);
};

async Task<JsonNode> Call(string method, JsonObject? p = null)
{
    var result = await client.CallAsync(method, p ?? []);
    return result!;
}

try
{
    var hello = await Call("session.hello", new JsonObject
    {
        ["protocol"] = new JsonObject { ["major"] = 1, ["minor"] = 0 },
        ["client"] = new JsonObject { ["kind"] = "test", ["version"] = "0.1.0" },
        ["features"] = new JsonArray(),
    });
    Console.WriteLine($"hello: protocol {hello["protocol"]!["major"]}.{hello["protocol"]!["minor"]}, connection {hello["connectionId"]}");

    var onboarding = await Call("onboarding.get");
    if ((string?)onboarding["step"] != "complete")
    {
        onboarding = await Call("onboarding.save", new JsonObject
        {
            ["expectedRevision"] = (long)onboarding["revision"]!,
            ["patch"] = new JsonObject { ["provider"] = "local", ["inventoryConsent"] = "skipped", ["step"] = "complete" },
        });
    }
    Console.WriteLine($"onboarding: revision {onboarding["revision"]}, step {onboarding["step"]}, resume report {onboarding["resume"]!["reportId"]}");

    var start = new JsonObject { ["idempotencyKey"] = key, ["intent"] = intent, ["text"] = text };
    if (baseReportId is not null) start["baseReportId"] = baseReportId; // with --intent rescan
    var scan = await Call("scan.start", start);
    var scanId = (string)scan["scanId"]!;
    Volatile.Write(ref scanStream, "scan:" + scanId);
    Console.WriteLine($"scan.start key={key} -> {scanId} ({scan["status"]})");

    if ((string?)scan["status"] is "running" or "queued")
        await finished.Task.WaitAsync(TimeSpan.FromMinutes(3));
    scan = await Call("scan.get", new JsonObject { ["scanId"] = scanId });
    var reportId = (string?)scan["reportId"];
    Console.WriteLine($"scan {scan["status"]}: {(scan["status"]!.ToString() == "complete" ? "clean, complete check" : "incomplete check")}");
    foreach (var cov in scan["coverage"]!.AsArray())
        Console.WriteLine($"  {cov!["collectorId"],-22} {cov["status"],-10} {cov["reason"]}");
    if (reportId is null) return 1;

    var report = await Call("report.get", new JsonObject { ["reportId"] = reportId });
    Console.WriteLine($"report {reportId}: {report["status"]}, {report["counts"]!["findings"]} findings, {report["counts"]!["gaps"]} gaps");
    foreach (var cat in report["categories"]?.AsArray() ?? [])
        Console.WriteLine($"  category {cat!["id"],-18} {cat["status"],-8} checked {cat["checked"]}/{cat["planned"]}, {cat["findings"]} findings");
    if (report["comparison"] is JsonObject cmp)
        Console.WriteLine($"  vs {report["baseReportId"]}: {cmp["new"]!.AsArray().Count} new, {cmp["resolved"]!.AsArray().Count} resolved, {cmp["unchanged"]} unchanged");
    foreach (var f in report["findings"]!.AsArray())
        Console.WriteLine($"  [{f!["severity"]}] {f["title"]} cites {string.Join(", ", f["evidenceIds"]!.AsArray())}");

    var first = report["findings"]!.AsArray().FirstOrDefault();
    if (first is not null)
    {
        var evidence = await Call("evidence.get", new JsonObject { ["reportId"] = reportId, ["evidenceId"] = (string)first["evidenceIds"]![0]! });
        Console.WriteLine($"evidence {evidence["id"]}: {evidence["display"]} ({evidence["source"]})");
    }

    var followup = await Call("followup.get", new JsonObject { ["reportId"] = reportId });
    Console.WriteLine($"followup: revision {followup["revision"]}, {followup["pendingCount"]}/{followup["total"]} pending, card: {followup["card"]?["title"]}");
    return 0;
}
catch (JsonRpcException e)
{
    Console.Error.WriteLine($"error {e.Code}: {e.Message} {e.ErrorData?.ToJsonString()}");
    return 1;
}
