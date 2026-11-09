using System.Text.Json.Nodes;
using OptimizationTool.Engine.Actions;
using OptimizationTool.Engine.Agent;
using OptimizationTool.Engine.Bench;

namespace OptimizationTool.Engine;

public sealed class EngineOptions
{
    public required string DataDir { get; init; }

    /// <summary>
    /// Read-only Windows audit modules (M2). Null runs the M1 fixture: fixture collectors, rules and inventory.
    /// </summary>
    public IReadOnlyList<IAuditModule>? Modules { get; init; }

    /// <summary>Budgets handed to module collectors and inventory steps.</summary>
    public CollectorOptions CollectorOptions { get; init; } = new();

    /// <summary>Multiplier for fixture delays: 1 = fixture timings, 0 = instant. Fixture mode only.</summary>
    public double FixtureSpeed { get; init; } = 1;

    /// <summary>Overall deadline for one scan (docs/backend.md: 120 s quick-scan budget).</summary>
    public TimeSpan ScanDeadline { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Wall clock for timestamps and disposition expiry (tests move it forward).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>M3 agent wiring (providers, key store, projection). Defaults share nothing.</summary>
    public AgentOptions Agent { get; init; } = new();

    /// <summary>M4 compiled action handlers and switches. Defaults: no handlers.</summary>
    public ActionOptions Actions { get; init; } = new();

    /// <summary>M6a benchmark seams (sensors, hardware, worker launcher) and timings. Defaults: benchmarks unsupported.</summary>
    public BenchOptions Bench { get; init; } = new();

    public Action<string>? Log { get; init; }
}

/// <summary>
/// The per-user backend engine: store, scan coordinator, report and follow-up queue, dispositions, onboarding,
/// starters, inventory and events. <see cref="InvokeAsync"/> dispatches the protocol v1.1 methods (except
/// session.hello, which the transport owns).
/// </summary>
public sealed class AuditEngine : IAsyncDisposable
{
    public const string Version = "0.4.0";

    /// <summary>Desktop (and test) methods: protocol §6, §9 and m3-agent §4, plus events.read. provider.setKey and provider.setClient are shell-only by policy.</summary>
    public static readonly IReadOnlyList<string> DesktopMethods =
    [
        "onboarding.get", "onboarding.save", "inventory.start", "inventory.get", "inventory.cancel", "starter.list",
        "scan.start", "scan.get", "scan.cancel", "scan.skipCollector", "report.get", "report.list", "report.compare",
        "evidence.get", "finding.get", "finding.setDisposition", "finding.clearDisposition",
        "followup.get", "followup.respond", "events.read",
        "provider.list", "provider.setKey", "provider.setClient", "provider.connect", "provider.disconnect",
        "agent.start", "agent.get", "agent.active", "agent.ask", "agent.stop", "agent.cancel", "agent.approvePairing",
        "review.preview", "review.submit", "review.export", "review.import",
        "action.catalog", "action.preview", "action.get", "action.decline", "action.undo", "action.challenge", "action.approve",
        "operation.get", "operation.list", "manual.recheck",
        "bench.capabilities", "bench.start", "bench.get", "bench.cancel", "bench.list", "bench.evidence",
    ];

    private readonly OnboardingService _onboarding;
    private readonly InventoryService _inventory;
    private readonly ScanCoordinator _scans;
    private readonly ReportService _reports;
    private readonly Followups _followups;
    private readonly EventService _events;

    /// <summary>Agent connections (m3-agent §1): pairing and tool calls only.</summary>
    public static readonly IReadOnlyList<string> AgentMethods = ["agent.pair", "agent.pairStatus", "tool.call"];

    /// <summary>Desktop methods (kept for callers of the v1.1 name).</summary>
    public static IReadOnlyList<string> Methods => DesktopMethods;

    public AgentService Agent { get; }
    public ActionService Actions { get; }
    public BenchService Bench { get; }

    /// <summary>m6 §2: the one per-user lease shared by scans, benchmarks, sensors.snapshot and performance actions.</summary>
    public WorkLease Lease { get; } = new();

    private static readonly Lazy<string?> LocalSid = new(() => { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); return id.User?.Value; });

    public Store Store { get; }
    public Fixture Fixture { get; }
    public AuditCatalog Catalog { get; }

    /// <summary>True when running live Windows modules rather than the fixture.</summary>
    public bool IsLive => !Catalog.IsFixture;

    private AuditEngine(Store store, Fixture fixture, AuditCatalog catalog, EngineOptions options, Action<string> log)
    {
        Store = store;
        Fixture = fixture;
        Catalog = catalog;
        _onboarding = new OnboardingService(store, fixture);
        _inventory = new InventoryService(store, fixture, catalog, options.FixtureSpeed, log);
        _scans = new ScanCoordinator(store, fixture, catalog, options.ScanDeadline, options.Time, log, Lease);
        _reports = new ReportService(store);
        _followups = new Followups(store, fixture);
        _events = new EventService(store);
        Actions = new ActionService(store, catalog, options.Time, options.Actions, log) { Lease = Lease };
        Bench = new BenchService(store, Lease, options.Bench, options.DataDir, log);
        Agent = new AgentService(store, catalog, options.Time, options.Agent, log) { Actions = Actions, DataDir = options.DataDir, Bench = Bench };
    }

    public static AuditEngine Open(EngineOptions options)
    {
        var log = options.Log ?? (_ => { });
        var fixture = Fixture.Default;
        var catalog = options.Modules is { } modules
            ? AuditCatalog.FromModules(modules, options.CollectorOptions)
            : AuditCatalog.FromFixture(fixture, options.FixtureSpeed);
        var store = Store.Open(options.DataDir, () => Time.Format(options.Time.GetUtcNow()));
        if (store.MovedAsidePath is { } aside) log($"unreadable database moved to {aside}; started a new one");
        if (store.WalAsidePath is { } walAside) log($"damaged write-ahead log moved to {walAside}; kept the database file");
        var engine = new AuditEngine(store, fixture, catalog, options, log);
        engine._onboarding.EnsureRow();
        engine._scans.RecoverInterrupted();
        engine._inventory.RecoverInterrupted();
        engine.Bench.RecoverInterrupted(); // m6 §2: starting|running|stopping → interrupted
        engine.Agent.RecoverAfterRestart();
        engine.Actions.ReconcileAsync().GetAwaiter().GetResult(); // m4 §5: never repeats a write
        log(catalog.IsFixture
            ? "source: fixture"
            : $"source: windows ({catalog.Collectors.Count} collectors, {catalog.Rules.Count} rules, {catalog.InventorySteps!.Count} inventory steps)");
        return engine;
    }

    public bool HasActiveWork => _scans.HasActiveWork || _inventory.HasActiveWork || Agent.HasActiveWork || Actions.HasActiveWork || Bench.HasActiveWork;

    public JsonObject UserCursor() => new() { ["stream"] = "user", ["seq"] = Store.Read(db => db.LastSeq("user")) };

    public async Task<JsonNode?> InvokeAsync(string method, JsonNode? parameters, CallContext? context = null)
    {
        var p = P.Obj(parameters);
        var ctx = context ?? new CallContext("local", "test", Environment.ProcessId, LocalSid.Value, System.Diagnostics.Process.GetCurrentProcess().SessionId);
        return method switch
        {
            "onboarding.get" => _onboarding.Get(),
            "onboarding.save" => _onboarding.Save(p),
            "starter.list" => _onboarding.Starters(p),
            "inventory.start" => _inventory.Start(p),
            "inventory.get" => _inventory.Get(P.Req(p, "inventoryId", 64)),
            "inventory.cancel" => await _inventory.CancelAsync(P.Req(p, "inventoryId", 64)).ConfigureAwait(false),
            "scan.start" => _scans.Start(p),
            "scan.get" => _scans.Get(P.Req(p, "scanId", 64)),
            "scan.cancel" => await _scans.CancelAsync(P.Req(p, "scanId", 64)).ConfigureAwait(false),
            "scan.skipCollector" => await _scans.SkipAsync(p).ConfigureAwait(false),
            "report.get" => _reports.Get(P.Req(p, "reportId", 64)),
            "report.list" => _reports.List(p),
            "report.compare" => _reports.Compare(p),
            "evidence.get" => _reports.Evidence(p),
            "finding.get" => _reports.Finding(p),
            "finding.setDisposition" => _followups.SetDisposition(p),
            "finding.clearDisposition" => _followups.ClearDisposition(p),
            "followup.get" => _followups.Get(P.Req(p, "reportId", 64)),
            "followup.respond" => _followups.Respond(p),
            "events.read" => _events.Read(p),
            "provider.list" => Agent.ListProviders(),
            "provider.setKey" => await Agent.SetKeyAsync(p).ConfigureAwait(false),
            "provider.setClient" => Agent.SetClient(p),
            "provider.connect" => await Agent.ConnectAsync(p).ConfigureAwait(false),
            "provider.disconnect" => Agent.Disconnect(p),
            "agent.start" => Agent.Start(p),
            "agent.get" => Agent.Get(p),
            "agent.active" => Agent.Active(),
            "agent.ask" => Agent.Ask(p),
            "agent.stop" => Agent.Stop(p),
            "agent.cancel" => await Agent.CancelAsync(p).ConfigureAwait(false),
            "agent.approvePairing" => Agent.ApprovePairing(p),
            "agent.pair" => Agent.Pair(p, ctx),
            "agent.pairStatus" => Agent.PairStatus(p, ctx),
            "tool.call" => await Agent.ToolCallAsync(p, ctx).ConfigureAwait(false),
            "review.preview" => Agent.Preview(p),
            "review.submit" => await Agent.SubmitAsync(p).ConfigureAwait(false),
            "review.export" => Agent.Export(p),
            "review.import" => Agent.Import(p),
            "action.catalog" => Actions.Catalog(),
            "action.preview" => await Actions.PreviewAsync(p).ConfigureAwait(false),
            "action.get" => Actions.Get(p),
            "action.decline" => Actions.Decline(p),
            "action.undo" => await Actions.UndoAsync(p).ConfigureAwait(false),
            "action.challenge" => Actions.Challenge(p, ctx),
            "action.approve" => await Actions.ApproveAsync(p, ctx).ConfigureAwait(false),
            "operation.get" => Actions.OperationGet(p),
            "operation.list" => Actions.OperationList(p),
            "manual.recheck" => await Actions.ManualRecheckAsync(p).ConfigureAwait(false),
            "bench.capabilities" => await Bench.CapabilitiesAsync().ConfigureAwait(false),
            "bench.start" => await Bench.StartAsync(p).ConfigureAwait(false),
            "bench.get" => Bench.Get(p),
            "bench.cancel" => Bench.Cancel(p),
            "bench.list" => Bench.List(p),
            "bench.evidence" => Bench.Evidence(p),
            _ => throw new RpcError(ErrorCodes.MethodNotFound, $"Unknown method {method}."),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Bench.StopAllAsync().ConfigureAwait(false);
        await _scans.StopAllAsync().ConfigureAwait(false);
        await _inventory.StopAllAsync().ConfigureAwait(false);
        await Agent.StopAllAsync().ConfigureAwait(false);
        await Actions.StopAllAsync().ConfigureAwait(false);
        Store.Dispose();
    }
}
