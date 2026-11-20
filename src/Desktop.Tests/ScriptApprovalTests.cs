using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using OptimizationTool.Desktop.Bridge;

namespace OptimizationTool.Desktop.Tests;

/// <summary>
/// The native script approval (m9-ai-powers.md §5.2): the view built from the host's plan (and refused when the text is not
/// exactly what was approved), the dialog (Approve only with the tick, after the delay, with the script read to its end;
/// the two choices), and the shell flow that carries the choices to action.challenge and action.approve. The renderer never
/// supplies them.
/// </summary>
public sealed class ScriptApprovalTests
{
    private const string Doc = "https://app.optimizationtool.invalid/index.html";
    private const string PlanId = "plan-0123456789abcdef";
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    internal static JsonObject Plan(string text = "# Clear the DNS cache\r\nClear-DnsClientCache\r\n\tWrite-Output \"done\"\r\n", int? lines = 3, bool rp = true,
        string? reason = null, bool bypass = true, bool elevated = false, JsonArray? parts = null, DateTimeOffset? expires = null) => new()
    {
        ["planId"] = PlanId, ["kind"] = "script", ["actionId"] = null, ["label"] = null, ["handlerVersion"] = null,
        ["title"] = "Clear the DNS cache", ["why"] = new JsonObject { ["text"] = "Some sites still open at their old address.", ["by"] = "Claude" },
        ["target"] = null, ["before"] = null, ["after"] = null, ["rows"] = new JsonArray(), ["context"] = new JsonArray(), ["tradeoffs"] = new JsonArray(),
        ["privilege"] = elevated ? "admin" : "none", ["flags"] = new JsonArray("irreversible", "runs_code"), ["resources"] = new JsonArray(),
        ["undo"] = new JsonObject { ["supported"] = false, ["conditions"] = null }, ["steps"] = null,
        ["requestedBy"] = new JsonObject { ["kind"] = "in_app", ["taskId"] = "task-1" },
        ["planHash"] = new string('a', 64), ["expiresAt"] = (expires ?? T0.AddMinutes(5)).ToString("O"), ["status"] = "awaiting_user", ["ineligible"] = null,
        ["script"] = new JsonObject
        {
            ["language"] = "powershell", ["text"] = text, ["sha256"] = new string('b', 64), ["bytes"] = text.Length + 3, ["lines"] = lines,
            ["parts"] = parts ?? new JsonArray(
                new JsonObject { ["fromLine"] = 1, ["toLine"] = 2, ["explains"] = "Empties the list of remembered addresses." },
                new JsonObject { ["fromLine"] = 3, ["toLine"] = 3, ["explains"] = "Says it is done." }),
            ["elevated"] = elevated,
            ["guard"] = new JsonObject
            {
                ["version"] = "sg-1",
                ["warnings"] = new JsonArray(new JsonObject { ["rule"] = "deletes_files", ["line"] = 2, ["text"] = "Deletes files or folders" }),
            },
            ["restorePoint"] = new JsonObject { ["available"] = rp, ["reason"] = rp ? null : reason ?? "flag_off" },
            ["bypassesPolicy"] = bypass,
        },
    };

    // ---------------------------------------------------------------- the view

    [Fact]
    public void The_view_shows_the_exact_script_its_parts_warnings_and_fixed_lines()
    {
        var v = PlanView.From(Plan())!;
        Assert.True(v.IsScript);
        Assert.True(v.NeedsTick);
        Assert.Equal(PlanView.FlaggedApproveDelay, v.Delay);
        Assert.Equal("Clear the DNS cache", v.Title);
        Assert.Equal("From Claude: Some sites still open at their old address.", v.Why);
        var s = v.Script!;
        Assert.Equal(["# Clear the DNS cache", "Clear-DnsClientCache", "    Write-Output \"done\""], s.Lines); // a tab as four spaces
        Assert.Equal(["Line 2: deletes files or folders"], s.Warnings);
        Assert.Equal("Claude", s.AiName);
        Assert.True(s.RestorePointAvailable);
        Assert.True(s.BypassesPolicy);
        Assert.False(s.Elevated);
        var box = s.BoxText();
        Assert.Contains("1  # Clear the DNS cache", box);
        Assert.Contains("Lines 1–2 · Claude’s explanation, not checked by Optimus", box);
        Assert.Contains("    Empties the list of remembered addresses.\r\n", box); // the explanation itself, unquoted
        Assert.Contains("Line 3 · Claude’s explanation, not checked by Optimus", box);
        Assert.Equal("Let Claude see the last lines of output", PlanView.ShareText(s.AiName));

        var off = PlanView.From(Plan(rp: false, reason: "protection_off", bypass: false, elevated: true))!.Script!;
        Assert.Equal((false, PlanView.RestorePointOff, false, true), (off.RestorePointAvailable, off.RestorePointReason, off.BypassesPolicy, off.Elevated));
        Assert.Equal(PlanView.RestorePointUnsupported, PlanView.From(Plan(rp: false))!.Script!.RestorePointReason);
    }

    [Theory]
    [InlineData("Write-Output 'café'\r\n")]          // not plain ASCII
    [InlineData("a‮b\r\n")]                          // bidi override
    [InlineData("a\u0007\r\n")]                           // control
    [InlineData("a\nb\r\n")]                              // a bare LF: not the approved serialisation
    public void A_text_that_is_not_the_approved_serialisation_refuses_the_view(string text) => Assert.Null(PlanView.From(Plan(text, lines: null)));

    [Fact]
    public void Over_limit_or_inconsistent_scripts_refuse_the_view_nothing_is_cut()
    {
        Assert.Null(PlanView.From(Plan(new string('x', 201) + "\r\n", lines: 1)));
        Assert.Null(PlanView.From(Plan(string.Concat(Enumerable.Repeat("x\r\n", 201)), lines: 201)));
        Assert.Null(PlanView.From(Plan(lines: 4)));                                                               // the host said 4 lines
        Assert.Null(PlanView.From(Plan(parts: new JsonArray(new JsonObject { ["fromLine"] = 2, ["toLine"] = 9, ["explains"] = "x" }))));
        Assert.Null(PlanView.From(Plan(parts: new JsonArray(new JsonObject { ["fromLine"] = 1, ["toLine"] = 1, ["explains"] = new string('e', 201) }))));
        Assert.Null(PlanView.From(Plan(parts: new JsonArray())));
        var p = Plan();
        p["script"] = null;
        Assert.Null(PlanView.From(p));
        Assert.NotNull(PlanView.From(Plan(string.Concat(Enumerable.Repeat(new string('y', 200) + "\r\n", 200)), lines: 200,
            parts: new JsonArray(new JsonObject { ["fromLine"] = 1, ["toLine"] = 200, ["explains"] = "All of it." }))));
    }

    // ---------------------------------------------------------------- the dialog

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void ShowOffscreen(Form f)
    {
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-4000, -4000);
        f.Show();
        Application.DoEvents();
    }

    [Fact]
    public void Approve_needs_the_tick_the_delay_and_the_script_read_to_its_end() => RunSta(() =>
    {
        var now = DateTime.UtcNow;
        var text = string.Concat(Enumerable.Range(1, 120).Select(i => $"Write-Output 'line {i}'\r\n"));
        var plan = Plan(text, lines: 120, parts: new JsonArray(new JsonObject { ["fromLine"] = 1, ["toLine"] = 120, ["explains"] = "Prints lines." }));
        using var d = new ScriptApprovalDialog(PlanView.From(plan)!, () => T0, () => now);
        Assert.Same(d.DeclineButton, d.AcceptButton);
        Assert.Same(d.DeclineButton, d.CancelButton);
        Assert.False(d.ApproveButton.Enabled);
        Assert.False(d.TickBox.Checked);                         // never pre-ticked
        Assert.True(d.ShareBox.Checked);                         // pre-ticked
        Assert.True(d.RestorePointBox.Checked && d.RestorePointBox.Enabled);
        Assert.Equal(PlanDecision.NotTicked, d.Decision);
        ShowOffscreen(d);
        try
        {
            now = now.AddSeconds(4);
            d.TickBox.Checked = true;
            d.Tick();
            Assert.False(d.ReadToEnd);                           // 120 lines do not fit: not read yet
            Assert.False(d.ApproveButton.Enabled);
            Assert.Equal(PlanDecision.NotTicked, d.Decision);    // closing now is "not ticked"
            d.ScrollToEnd();
            Assert.True(d.ReadToEnd);
            Assert.True(d.ApproveButton.Enabled);
            d.TickBox.Checked = false;
            d.Tick();
            Assert.False(d.ApproveButton.Enabled);
            Assert.Equal(PlanDecision.NotTicked, d.Decision);
        }
        finally { d.Close(); }
    });

    [Fact]
    public void A_script_that_fits_counts_as_read_after_the_delay_and_the_lines_are_all_there() => RunSta(() =>
    {
        var now = DateTime.UtcNow;
        using var d = new ScriptApprovalDialog(PlanView.From(Plan(rp: false, reason: "protection_off", elevated: true))!, () => T0, () => now);
        ShowOffscreen(d);
        try
        {
            d.TickBox.Checked = true;
            d.Tick();
            Assert.True(d.ReadToEnd);
            Assert.False(d.ApproveButton.Enabled);              // the 3 s delay
            now = now.AddSeconds(3.1);
            d.Tick();
            Assert.True(d.ApproveButton.Enabled);
            Assert.False(d.RestorePointBox.Enabled);
            Assert.False(d.RestorePoint);                        // disabled: never asked for
            var labels = d.LabelTexts.ToList();
            Assert.Contains(ApprovalDialogText.ScriptRed, labels);
            Assert.Contains("Asked by Claude", labels);
            Assert.Contains("From Claude: Some sites still open at their old address.", labels);
            Assert.Contains("Line 2: deletes files or folders", labels);
            Assert.Contains(PlanView.ScriptNotice, labels);
            Assert.Contains(PlanView.ScriptPolicyLine, labels);
            Assert.Contains(PlanView.ScriptAdminLine, labels);
            Assert.Contains(PlanView.RestorePointOff, labels);
            Assert.Contains(PlanView.ShareNote, labels);
            Assert.Contains("Clear-DnsClientCache", d.ScriptBox.Text);
            Assert.Contains("Claude’s explanation, not checked by Optimus", d.ScriptBox.Text);
            Assert.True(d.ScriptBox.ReadOnly);
            Assert.Equal("Consolas", d.ScriptBox.Font.Name);

            var shot = Environment.GetEnvironmentVariable("OT_DIALOG_SNAPSHOT_DIR");
            if (!string.IsNullOrEmpty(shot))
            {
                using var bmp = new Bitmap(d.Width, d.Height);
                d.DrawToBitmap(bmp, new Rectangle(Point.Empty, d.Size));
                bmp.Save(Path.Combine(shot, "approve-script.png"));
            }
        }
        finally { d.Close(); }
    });

    [Fact]
    public void Escape_without_the_tick_is_not_ticked_and_expiry_is_expired() => RunSta(() =>
    {
        var utc = T0;
        using var d = new ScriptApprovalDialog(PlanView.From(Plan(expires: T0.AddSeconds(2)))!, () => utc);
        Assert.Equal(PlanDecision.NotTicked, d.Decision);
        utc = T0.AddSeconds(3);
        d.Tick();
        Assert.Equal(PlanDecision.Expired, d.Decision);
    });

    // ---------------------------------------------------------------- the shell flow

    private sealed class Host : IBackend
    {
        public JsonObject Current = Plan();
        public readonly List<(string Method, JsonObject? Params)> Calls = [];
        public JsonObject? ApproveError;

        public Task<JsonObject> CallAsync(string method, JsonObject? p, CancellationToken ct)
        {
            Calls.Add((method, (JsonObject?)p?.DeepClone()));
            JsonNode? result = method switch
            {
                "action.get" => Current.DeepClone(),
                "action.decline" => new JsonObject { ["planId"] = PlanId, ["status"] = "declined" },
                "action.challenge" => new JsonObject { ["nonce"] = "n-1", ["expiresAt"] = "x" },
                "action.approve" when ApproveError is null => new JsonObject { ["operationId"] = "op-0123456789abcdef", ["planId"] = PlanId, ["state"] = "prepared" },
                _ => null,
            };
            return Task.FromResult(result is not null
                ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["result"] = result }
                : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["error"] = ApproveError?.DeepClone() ?? new JsonObject { ["code"] = -32601, ["message"] = "x" } });
        }
    }

    private sealed class Prompt(ScriptDecision decision) : IPlanApprovalPrompt
    {
        public readonly List<PlanView> Shown = [];
        public Task<PlanDecision> ConfirmAsync(PlanView view, CancellationToken ct) => throw new InvalidOperationException("a script uses its own dialog");
        public Task<ScriptDecision> ConfirmScriptAsync(PlanView view, CancellationToken ct)
        {
            Shown.Add(view);
            return Task.FromResult(decision);
        }
    }

    /// <summary>A prompt written before scripts: it knows only ConfirmAsync.</summary>
    private sealed class OldPrompt : IPlanApprovalPrompt
    {
        public Task<PlanDecision> ConfirmAsync(PlanView view, CancellationToken ct) => Task.FromResult(PlanDecision.Approve);
    }

    private static async Task<JsonNode> Run(Host host, IPlanApprovalPrompt prompt, string paramsJson = $$"""{"planId":"{{PlanId}}"}""")
    {
        var dispatcher = new BridgeDispatcher(new BridgeRouter(), host, AppLog.Disabled, new SettingsLauncher(_ => { }),
            approvals: new PlanApproval(prompt, utcNow: () => T0));
        return JsonNode.Parse((await dispatcher.HandleAsync($$"""{"type":"rpc","id":1,"method":"shell.approvePlan","params":{{paramsJson}}}""", Doc, true, default))!)!;
    }

    [Fact]
    public async Task The_dialogs_choices_travel_to_the_challenge_and_the_approve()
    {
        var host = new Host();
        var reply = await Run(host, new Prompt(new ScriptDecision(PlanDecision.Approve, RestorePoint: true, ShareOutput: false)));
        Assert.Equal("""{"status":"approved","operationId":"op-0123456789abcdef"}""", reply["result"]!.ToJsonString());
        var challenge = host.Calls.Single(c => c.Method == "action.challenge").Params!;
        var approve = host.Calls.Single(c => c.Method == "action.approve").Params!;
        Assert.Equal("""{"restorePoint":true,"shareOutput":false}""", challenge["options"]!.ToJsonString());
        Assert.Equal(challenge["options"]!.ToJsonString(), approve["options"]!.ToJsonString());
        Assert.Equal(new string('a', 64), approve["planHash"]!.GetValue<string>());

        // No restore point can be asked for when the plan says none is available, whatever the prompt returns.
        var noRp = new Host { Current = Plan(rp: false) };
        await Run(noRp, new Prompt(new ScriptDecision(PlanDecision.Approve, RestorePoint: true, ShareOutput: true)));
        Assert.Equal("""{"restorePoint":false,"shareOutput":true}""", noRp.Calls.Single(c => c.Method == "action.challenge").Params!["options"]!.ToJsonString());

        // A prompt that predates scripts shares nothing and makes no restore point.
        var old = new Host();
        await Run(old, new OldPrompt());
        Assert.Equal("""{"restorePoint":false,"shareOutput":false}""", old.Calls.Single(c => c.Method == "action.approve").Params!["options"]!.ToJsonString());
    }

    [Fact]
    public async Task Without_the_tick_it_is_declined_not_ticked_and_nothing_is_approved()
    {
        var host = new Host();
        var reply = await Run(host, new Prompt(new ScriptDecision(PlanDecision.NotTicked, true, true)));
        Assert.Equal("""{"status":"declined","error":"not_ticked"}""", reply["result"]!.ToJsonString());
        Assert.Contains(host.Calls, c => c.Method == "action.decline");
        Assert.DoesNotContain(host.Calls, c => c.Method is "action.challenge" or "action.approve");
    }

    [Fact]
    public async Task The_renderer_can_never_supply_options_or_call_challenge_and_approve()
    {
        var host = new Host();
        var reply = await Run(host, new Prompt(new ScriptDecision(PlanDecision.Approve, true, true)),
            $$$"""{"planId":"{{{PlanId}}}","options":{"restorePoint":true,"shareOutput":true}}""");
        Assert.Equal(-32602, reply["error"]!["code"]!.GetValue<int>());
        Assert.Empty(host.Calls);
        Assert.DoesNotContain("action.challenge", (IEnumerable<string>)BridgeRouter.DesktopMethods);
        Assert.DoesNotContain("action.approve", (IEnumerable<string>)BridgeRouter.DesktopMethods);
        Assert.Contains("operation.transcript", (IEnumerable<string>)BridgeRouter.DesktopMethods);
        var router = new BridgeRouter();
        foreach (var m in new[] { "action.challenge", "action.approve" })
            Assert.IsType<BridgeDecision.Reply>(router.Evaluate("{\"type\":\"rpc\",\"id\":1,\"method\":\"" + m + "\",\"params\":{\"planId\":\"" + PlanId + "\",\"options\":{\"restorePoint\":true}}}", Doc, true));
    }

    [Fact]
    public async Task Busy_answers_keep_the_plan_waiting()
    {
        var host = new Host { ApproveError = new JsonObject { ["code"] = -32013, ["message"] = "x", ["data"] = new JsonObject { ["reason"] = "action_active", ["operationId"] = "op-1" } } };
        Assert.Equal("""{"status":"unavailable","error":"action_active"}""", (await Run(host, new Prompt(new ScriptDecision(PlanDecision.Approve, false, true))))["result"]!.ToJsonString());
        var scan = new Host { ApproveError = new JsonObject { ["code"] = -32004, ["message"] = "x", ["data"] = new JsonObject { ["scanId"] = "scan-1" } } };
        Assert.Equal("""{"status":"unavailable","error":"scan_active"}""", (await Run(scan, new Prompt(new ScriptDecision(PlanDecision.Approve, false, true))))["result"]!.ToJsonString());
        Assert.DoesNotContain(scan.Calls, c => c.Method == "action.decline");
    }
}
