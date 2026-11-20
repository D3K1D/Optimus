/* Fixture adapter, M9 §6–§7 part: researched reports, simulated offline (contracts/m9-ai-powers.md §6.1–§6.5, §8).
   Plugged into adapter-fixture.js, which owns storage, events and timers; the permissions record, `available`, the
   research provider and web availability come from adapter-fixture-agent.js. The host is the authority
   (src/Engine/Agent/AgentService.Research.cs, Research/ResearchPolicy.cs, UrlHygiene.cs, src/Desktop/Bridge/SourceOpener.cs).

   What it simulates, as the host does:
   - Queue (§6.1): findings in report order whose rule declares Research (on the host only net.wildcard_listener_public,
     kind "port"); one subject per product (kind, name, publisher, product, signature, ports), at most 12; findings of
     one product share it. Subjects are built from the finding's evidence (process, signed, publisher, product, ports).
   - Two stores: a product cache (30 days) and labels per report. A new report is labelled again from the cache with no
     second lookup; the host rules are re-applied to that report's own findings every time.
   - Host rules 1–4 (§6.2): no observed source → no label; considered_safe only with a valid signature and a publisher,
     else worth_a_look with basis name_only; high/critical → worth_a_look; a threat on the same file (evidence
     resourceFiles) → worth_a_look.
   - Fencing (§6.3): a result is stored only while the run is the report's active run, the permissions revision is
     unchanged with web on, the provider kind and model are unchanged and the report is still the newest. Any permissions
     save cancels an active run and starts again for the newest report when web stays on (a run was cancelled) or was
     just turned on. A new scan, a provider key or sign-out change and a reload cancel it too.
   - Outcomes: done; partial (the time budget ran out, or some calls failed); failed (every call failed); unavailable
     (the provider refused the search tool: web "no" for that kind + model); cancelled.

   Wire shapes (the host's):
     research.get → {runId|null, status, done, total, trial, items, usage:{searches, inputTokens, outputTokens}}
       status "off" whenever do.web is off (runId is still the report's last run, or null) or no run exists;
       "unavailable" with no run when available.web is "no".
     research.state {reportId, runId, status, done, total, trial} on stream "user".
     research.start: web off → -32013 {reason:"web_off"}; no AI → -32013 {reason:"no_provider"}; an active run → its view;
       web "no" → a run that is "unavailable" at once.
     shell.openSource {sourceId} → {status:"opened"|"unknown"|"blocked"}; a second open within 1 s → -32013
       {reason:"rate_limited"}; the browser refusing → -32013 {reason:"open_failed"}.
   Dev hook fixture.setResearch {reportId?, status?, done?, total?, trial?, label?:{name|findingId, verdict|null, basis?},
     trialOutcome?:"no", failCalls?:n|"all", budgetCalls?:n, lookupOutcome?}. failCalls/budgetCalls shape the NEXT run: that
     many fresh calls fail (all → "failed", some → "partial"), or the time budget runs out after n calls ("partial").
     lookupOutcome shapes the NEXT fresh web_lookup call: "unsupported" (web "no", status unavailable) | "unavailable" |
     "timeout" | "cancelled" (a transient failure: a tool error, availability unchanged).

   §6.8 broad check-up over the §6.10 inventory (the fixture is its first implementation; the host matches these shapes):
   - Source: the PC's inventory (CHECKUP_PC.inventory, synthetic), records {id, area, name, publisher?, version?} in the
     five areas hardware (CPU, GPU, board and BIOS/UEFI, RAM modules, drives with firmware, monitors, network adapters),
     drivers (every device driver; publisher = the provider), windows (edition, version, build and update level),
     programs (installed programs and Store apps) and background (startup items, services, scheduled tasks outside
     \Microsoft\, running processes; publisher and version of what they launch). Records may carry fields that never
     leave (serial, instanceId, mac, path, account …). The windows area also gets the findings' generic phrases, the
     settings (UAC, Secure Boot, Defender, power mode) and the event-log error sources, as before.
   - After the labels phase of a run ends (not cancelled, not unavailable; also straight away when there was nothing to
     label), a check-up run of the newest report's PC: its own runId ("research-<16 hex>"), one call per area in the order
     hardware, drivers, windows, programs, background (≤ 5 calls), prompt "checkup-v2", each with only that area's
     entries {id:"i12", area, name, publisher?, version?} (buildSummary: names, publishers and versions only, through the
     §6.9 filters; per area third-party first and Microsoft's own in-box items last (not in windows), duplicates merged
     by name + publisher ignoring case, ≤ 100 per area, the rest counted in unsent[area]). An area whose entries hash the
     same and was checked within 7 days is skipped (no call); all skipped → "done" at once, total 0.
   - Results {about, kind, title ≤ 100, what ≤ 400, sources:[url]}: about must be an id sent in that call, kind one of
     bug|security|end_of_life|conflict|update, ≥ 1 source the search observed; ≤ 3 kept per area, ≤ 8 per check-up.
   - Each kept result is a followup card through the AI-card path, appended to the report's deck: origin "ai",
     findingId null, findingIds [], evidenceIds [], options ["Ask about this","Ignore"], no action, guide or preview,
     category "security" for security / end_of_life else "diagnostics", title = title, text = why = what (or the title),
     addedBy = the provider label, sourceTaskId null, and checkup:{kind, sources:[{sourceId, title, domain}]}. Stored
     web-tainted (aiTainted, internal). Card id "card-" + H(reportId, "checkup", area, entry name lower-case, kind); the
     same area + entry name + kind already on the report is replaced in place (wording and sources; its deck place and
     any decision stay).
   - research.get (and research.start / research.cancel / fixture.setResearch results) gain
     checkup:{status:"off"|"idle"|"running"|"done"|"partial"|"failed"|"unavailable", lastAt:ISO|null, cards:[cardId…]}:
     off without web or an AI; unavailable when the AI's web is "no"; running while one runs; else the last check-up's
     outcome for this provider and model (idle when none, or after a cancel). lastAt: the end of the last done or partial
     check-up. cards: this report's check-up cards (queue, then decided, then others). missed: the area ids a partial
     check-up did not check (empty otherwise).
   - research.state gains phase:"labels"|"checkup". A check-up emits running (done/total = areas checked/to check) after
     each area, then done | partial | failed | unavailable, or cancelled; trial is always false.
   - Fenced like a run; cancelled by research.cancel {reportId}, any permissions save, a new scan, an account change of
     its provider and a reload. Calls: failCalls (failed area, not cached), the time budget (budgetCalls), more than
     10 searches where the provider cannot cap them (killed), output over 2 KB (unusable), more than 5 calls. Searches
     count ≤ 6 per call for the API kinds. (The host's 90 s per call and 8 minutes in all are simulated by budgetCalls.)
   - Weekly: fixture.checkupTick {days?} moves the fixture clock on and runs the host's hourly check (a check-up of the
     newest report when the last done/partial one ended ≥ 7 days ago, or there was none). → {started, now, checkup}.
   Dev hooks fixture.setCheckup {status?|null, failCalls?, budgetCalls?, outcome?:"unsupported", searches?,
   pc?:{identity, inventory, settings, events}, phrases?:false, answers?:{<area>: …}} and fixture.getCheckup →
   {calls:[{area, prompt, entries, unsent}], usage, areas:{<area>: {hash, at}}, now}. */
(function (root) {
  'use strict';

  // §6.1: the rules that declare Research (host IRule.Research). Only this one on the host.
  var RESEARCH = { 'net.wildcard_listener_public': 'port' };
  var WHAT = { port: 'listens on the network', startup: 'starts with Windows', service: 'runs as a Windows service', task: 'runs as a scheduled task', program: 'runs on this PC' };
  // What the simulated model answers per program name (synthetic). A name not listed: nothing observed online.
  var ANSWERS = {
    'steam.exe': { label: 'Steam · open port', verdict: 'considered_safe',
      summary: 'Steam uses UDP port 27036 to find other PCs on your home network for Remote Play and in-home streaming. Valve documents this port, and it is normal while Steam runs. It only needs to be reachable on networks you trust.',
      sources: [
        { title: 'Required ports for Steam: which ports the Steam client, Remote Play and in-home streaming use on home and public networks', url: 'https://help.steampowered.com/en/faqs/view/2EA8-4D75-DA21-31EB' },
        { title: 'Steam Remote Play', url: 'https://store.steampowered.com/remoteplay' },
        { title: 'Steam (service)', url: 'https://en.wikipedia.org/wiki/Steam_(service)#Remote_Play' }
      ] },
    // The model calls it safe; it is unsigned, so rule 2 caps it at worth_a_look (name_only).
    'remotehelper.exe': { label: 'RemoteHelper · open port', verdict: 'considered_safe',
      summary: 'RemoteHelper is a name used by several remote-desktop tools. TCP port 5900 is the usual VNC port: whoever connects can see and control this PC. Keep it only if you set it up yourself.',
      sources: [
        { title: 'Virtual Network Computing', url: 'https://en.wikipedia.org/wiki/Virtual_Network_Computing' },
        { title: 'Port 5900 (tcp/udp)', url: 'https://www.speedguide.net/port.php?port=5900' }
      ] }
  };
  /* §6.10 the inventory the check-up reads (synthetic; fictional makers). Records {id, area, name, publisher?, version?};
     some carry fields that must never be sent (serial, instanceId, mac, path, account), and some names or publishers are
     hostile on purpose (a serial, a hardware instance id, a MAC address, a path, the user and the PC name): the
     projection (buildSummary) drops a hostile name whole and a hostile publisher or version field. The windows area
     also reads the settings and event-log errors (never the message) as before. */
  // Microsoft's own in-box drivers as Windows lists them (many, often repeated): enough to pass the 100-entry cap.
  var INBOX_DRIVERS = ['ACPI x64-based PC', 'High precision event timer', 'PCI Express Root Port', 'PCI Express Root Complex', 'USB Root Hub (USB 3.0)',
    'Generic USB Hub', 'USB Composite Device', 'HID-compliant mouse', 'HID-compliant consumer control device', 'HID Keyboard Device',
    'HID-compliant system controller', 'Generic volume', 'Volume Manager', 'Disk drive', 'Standard NVM Express Controller', 'WAN Miniport (IP)',
    'WAN Miniport (IPv6)', 'Microsoft Wi-Fi Direct Virtual Adapter', 'Generic software device', 'System timer', 'Programmable interrupt controller',
    'Microsoft Basic Display Adapter', 'Microsoft Kernel Debug Network Adapter', 'Remote Desktop Device Redirector Bus', 'Composite Bus Enumerator',
    'UMBus Root Bus Enumerator', 'Microsoft System Management BIOS Driver', 'Plug and Play Software Device Enumerator',
    'Microsoft Storage Spaces Controller', 'Generic PnP Monitor'];
  function inboxDrivers() {
    var out = [];
    for (var i = 0; i < 120; i++) {
      var base = INBOX_DRIVERS[i % INBOX_DRIVERS.length], round = Math.floor(i / INBOX_DRIVERS.length);
      out.push({ id: 'inv-drv-ms-' + i, area: 'drivers', name: round ? base + ' #' + (round + 1) : base, publisher: 'Microsoft', version: '10.0.26100.' + (1 + (i % 7)) });
    }
    // Repeats of the same device kind with the same provider: merged into one entry.
    for (var j = 0; j < 3; j++) out.push({ id: 'inv-drv-ms-dup' + j, area: 'drivers', name: 'Generic software device', publisher: 'microsoft', version: '10.0.26100.1' });
    return out;
  }
  var CHECKUP_PC = {
    identity: { user: 'alex', pc: 'DESKTOP-7Q2M4KD' },
    inventory: [
      // hardware: models with maker and version (firmware where Windows reports one)
      { id: 'inv-hw-1', area: 'hardware', name: 'Northwind N7 8-core processor', publisher: 'Northwind', serial: 'NWN7-0042-99812-XQ' },
      { id: 'inv-hw-2', area: 'hardware', name: 'Contoso GX 4070 graphics card', publisher: 'Contoso', instanceId: 'PCI\\VEN_10DE&DEV_2786&SUBSYS_40BF1458&REV_A1\\4&2B8260C8&0&0008' },
      { id: 'inv-hw-3', area: 'hardware', name: 'Fabrikam B650 board', publisher: 'Fabrikam', serial: 'FBK650-221145-0042' },
      { id: 'inv-hw-4', area: 'hardware', name: 'Fabrikam B650 board BIOS', publisher: 'Fabrikam', version: '1813' },
      { id: 'inv-hw-5', area: 'hardware', name: 'Litware DDR5-6000 16 GB module', publisher: 'Litware', serial: '24A1B7C9' },
      { id: 'inv-hw-6', area: 'hardware', name: 'Litware DDR5-6000 16 GB module', publisher: 'LITWARE', serial: '24A1B7D0' },   // the second stick: merged
      { id: 'inv-hw-7', area: 'hardware', name: 'Adatum Swift 1 TB SSD', publisher: 'Adatum', version: '5B2QGXA7', serial: 'S5GXNX0T123456A' },
      { id: 'inv-hw-8', area: 'hardware', name: 'S5GXNX0T123456A', publisher: 'Adatum', version: '5B2QGXA7' },                  // a serial where a model should be
      { id: 'inv-hw-9', area: 'hardware', name: 'Wingtip View 27 monitor', publisher: 'Wingtip Toys', serial: 'WTV27-8812345' },
      { id: 'inv-hw-10', area: 'hardware', name: 'Wingtip Wi-Fi 7 adapter', publisher: 'Wingtip Toys', mac: '3C-7C-3F-1A-2B-4D' },
      { id: 'inv-hw-11', area: 'hardware', name: 'Wingtip Ethernet 3C-7C-3F-1A-2B-4E', publisher: 'Wingtip Toys' },             // a MAC address in the name
      { id: 'inv-hw-12', area: 'hardware', name: 'Microsoft Wi-Fi Direct Virtual Adapter', publisher: 'Microsoft' },
      // drivers: every device driver; the provider as publisher
      { id: 'inv-drv-1', area: 'drivers', name: 'Contoso GX 4070 graphics card driver', publisher: 'Contoso', version: '551.23',
        instanceId: 'PCI\\VEN_10DE&DEV_2786&SUBSYS_40BF1458&REV_A1\\4&2B8260C8&0&0008', path: 'C:\\Windows\\System32\\DriverStore\\FileRepository\\cgx.inf_amd64\\cgxkmd.sys' },
      { id: 'inv-drv-2', area: 'drivers', name: 'Wingtip Wi-Fi 7 adapter driver', publisher: 'Wingtip Toys', version: '22.200.0.6' },
      { id: 'inv-drv-3', area: 'drivers', name: 'Tailspin audio device driver', publisher: 'Tailspin Toys', version: '6.0.9549.1' },
      { id: 'inv-drv-4', area: 'drivers', name: 'Northwind chipset driver', publisher: 'Northwind', version: '5.10.0.38' },
      { id: 'inv-drv-5', area: 'drivers', name: 'Adatum NVMe storage driver', publisher: 'Adatum', version: '2.4.1' },
      { id: 'inv-drv-6', area: 'drivers', name: 'PCI\\VEN_10DE&DEV_2786&SUBSYS_40BF1458&REV_A1\\4&2B8260C8&0&0008', publisher: 'Contoso', version: '551.23' },   // an instance id as the name
      { id: 'inv-drv-7', area: 'drivers', name: 'Fabrikam USB 3.2 controller driver', publisher: 'DESKTOP-7Q2M4KD', version: '1.2.0' }   // the PC name as provider: field dropped
    ].concat(inboxDrivers(), [
      // windows: edition, version, build and update level
      { id: 'inv-win-1', area: 'windows', name: 'Windows 11 Pro', publisher: 'Microsoft', version: '24H2 build 26100.2605' },
      // programs: installed programs, Store apps included
      { id: 'inv-app-1', area: 'programs', name: 'Litware VPN', publisher: 'Litware', version: '3.2', path: 'C:\\Program Files\\Litware\\VPN' },
      { id: 'inv-app-2', area: 'programs', name: 'Wingtip Game Launcher', publisher: 'Wingtip Toys', version: '8.1.4', path: 'D:\\Games\\Wingtip' },
      { id: 'inv-app-3', area: 'programs', name: 'Adatum PDF Reader', publisher: 'Adatum', version: '11.0.2' },
      { id: 'inv-app-4', area: 'programs', name: 'Microsoft Edge', publisher: 'Microsoft Corporation', version: '131.0.2903.70' },
      { id: 'inv-app-5', area: 'programs', name: 'Tailspin Backup', publisher: 'Tailspin Toys', version: '2.0' },
      { id: 'inv-app-6', area: 'programs', name: 'Contoso Overlay', publisher: 'Contoso', version: '4.6' },
      { id: 'inv-app-7', area: 'programs', name: 'Wingtip Photo Studio', publisher: 'Wingtip Toys', version: '2.4.118.0' },        // a Store app
      { id: 'inv-app-8', area: 'programs', name: 'Windows Calculator', publisher: 'Microsoft Corporation', version: '11.2409.0.0' },   // a Store app
      { id: 'inv-app-9', area: 'programs', name: 'litware vpn', publisher: 'LITWARE', version: '3.2' },                         // listed twice: merged
      { id: 'inv-app-10', area: 'programs', name: 'C:\\Users\\alex\\AppData\\Local\\Programs\\tool.exe', publisher: 'Adatum', version: '1.0' },
      { id: 'inv-app-11', area: 'programs', name: 'Notes for alex', publisher: 'Adatum', version: '1.0' },
      // background: startup items, services, tasks outside \Microsoft\, running processes (what they launch)
      { id: 'inv-bg-1', area: 'background', name: 'Wingtip Game Launcher', publisher: 'Wingtip Toys', version: '8.1.4', path: '"D:\\Games\\Wingtip\\launcher.exe" --minimized' },
      { id: 'inv-bg-2', area: 'background', name: 'Windows Update', publisher: 'Microsoft Corporation', version: '10.0.26100.2605' },
      { id: 'inv-bg-3', area: 'background', name: 'Tailspin Backup Service', publisher: 'Tailspin Toys', version: '2.0', account: '.\\alex' },
      { id: 'inv-bg-4', area: 'background', name: 'Adatum PDF Reader update check', publisher: 'Adatum', version: '11.0.2', path: 'C:\\Program Files\\Adatum\\update.exe' },
      { id: 'inv-bg-5', area: 'background', name: 'Contoso Overlay', publisher: 'Contoso', version: '4.6' },
      { id: 'inv-bg-6', area: 'background', name: 'DESKTOP-7Q2M4KD remote agent', publisher: 'Litware', version: '1.0' },
      { id: 'inv-bg-7', area: 'background', name: 'Microsoft Edge', publisher: 'Microsoft Corporation', version: '131.0.2903.70' }
    ]),
    settings: { uac: true, secureBoot: true, defenderRealTime: true, defenderDefinitions: '1.421.1260.0', powerMode: 'Balanced', proxy: 'http://10.0.0.5:8080' },
    // Critical and error events, by the provider's stored name; never the message.
    events: [{ provider: 'Microsoft-Windows-Kernel-Power', level: 'critical', id: 41, count: 3, message: 'The system has rebooted without cleanly shutting down first. DESKTOP-7Q2M4KD\\alex' }]
  };
  // What the simulated model answers per area (synthetic). `about` names a sent entry by its name ({name}) or is a raw
  // id; `sources` are URLs the model wrote, `observed` what the search tool returned (only those count, §6.3).
  // Kept with the default inventory: hardware 2, drivers 2, windows 3 of 4 (the per-area cap), programs 1 of 2 and
  // background 0 of 1 (8 in all).
  var CHECKUP_ANSWERS = {
    hardware: { searches: 4,
      observed: [
        { title: 'B650 board firmware 1903', url: 'https://www.fabrikam.com/support/b650/firmware' },
        { title: 'N7 processor microcode update', url: 'https://www.northwindtraders.com/support/n7-microcode' }
      ],
      items: [
        { about: { name: 'Fabrikam B650 board BIOS' }, kind: 'update', title: 'A newer board firmware fixes slow starts with four memory sticks',
          what: 'Firmware 1903 for this board shortens start-up when all four memory slots are used. This PC runs 1813.', sources: ['https://www.fabrikam.com/support/b650/firmware'] },
        { about: 'i999', kind: 'security', title: 'A problem with something that was never sent', what: 'Dropped: not an entry of this call.', sources: ['https://www.fabrikam.com/support/b650/firmware'] },
        { about: { name: 'Adatum Swift 1 TB SSD' }, kind: 'conflict', title: 'A source the search never returned', what: 'Dropped: no observed source.', sources: ['https://made-up.adatum.com/claim'] },
        { about: { name: 'Northwind N7 8-core processor' }, kind: 'update', title: 'A processor update improves stability at idle',
          what: 'Northwind released a microcode update for N7 processors that fixes rare restarts at idle. It comes with board firmware 1903.', sources: ['https://www.northwindtraders.com/support/n7-microcode'] }
      ] },
    drivers: { searches: 3,
      observed: [
        { title: 'Contoso graphics driver 551.23 known issues: short freezes in games and video playback after resuming from sleep on GX 40 series cards', url: 'https://support.contoso.com/drivers/551-23/known-issues' },
        { title: 'Driver 552.12 release notes', url: 'https://www.contoso.com/drivers/552-12' },
        { title: 'Stutter after sleep with 551.23', url: 'https://forums.contoso.com/t/stutter-after-sleep/88121' },
        { title: 'Wingtip Wi-Fi 7 driver 22.210', url: 'https://support.wingtiptoys.com/wifi7/driver' }
      ],
      items: [
        { about: { name: 'Contoso GX 4070 graphics card driver' }, kind: 'bug',
          title: 'This graphics driver version can make games and videos freeze for a moment after the PC wakes from sleep',
          what: 'Contoso lists driver 551.23 as causing short freezes in games and video after the PC wakes from sleep. It happens on GX 40 series cards, and this PC has one. The freezes stop after a restart and come back after the next sleep. Version 552.12 fixes it. You can update from the Contoso app or from Windows Update. Until then, restarting instead of using sleep avoids it. Nothing on this PC was tested for it.',
          sources: ['https://support.contoso.com/drivers/551-23/known-issues', 'https://www.contoso.com/drivers/552-12', 'https://forums.contoso.com/t/stutter-after-sleep/88121'] },
        { about: { name: 'Wingtip Wi-Fi 7 adapter driver' }, kind: 'update', title: 'A newer Wi-Fi driver fixes dropouts after sleep',
          what: 'Wingtip driver 22.210 fixes Wi-Fi dropping after the PC wakes from sleep. This PC has 22.200.0.6.', sources: ['https://support.wingtiptoys.com/wifi7/driver'] }
      ] },
    windows: { searches: 5,
      observed: [
        { title: 'November security update for Windows 11 24H2', url: 'https://support.microsoft.com/help/5099999' },
        { title: 'Kernel-Power event 41: what it means', url: 'https://learn.microsoft.com/troubleshoot/windows-client/performance/event-id-41-restart' },
        { title: 'Fast Startup and dual boot', url: 'https://answers.microsoft.com/windows/forum/fast-startup-dual-boot' }
      ],
      items: [
        { about: { name: 'Windows 11 Pro' }, kind: 'security', title: 'This Windows build is missing last month’s security fixes',
          what: 'Build 26100.2605 is older than the November security update. Windows Update installs it.', sources: ['https://support.microsoft.com/help/5099999'] },
        { about: { name: 'Microsoft-Windows-Kernel-Power event 41, 3 times' }, kind: 'bug', title: 'Restarts without a clean shutdown are often power or driver problems',
          what: 'Event 41 means Windows restarted without shutting down first. Common causes are the power supply, sleep settings and graphics drivers.', sources: ['https://learn.microsoft.com/troubleshoot/windows-client/performance/event-id-41-restart'] },
        { about: { name: 'Power mode Balanced' }, kind: 'conflict', title: 'Balanced power mode can keep drivers from updating fully',
          what: 'With Fast Startup on in Balanced mode, a shutdown does not fully restart Windows, so some driver updates wait for a real restart.', sources: ['https://answers.microsoft.com/windows/forum/fast-startup-dual-boot'] },
        { about: { name: 'Secure Boot on' }, kind: 'security', title: 'A fourth result for Windows', what: 'A fourth result for this area: over the cap of 3.', sources: ['https://support.microsoft.com/help/5099999'] }
      ] },
    programs: { searches: 6,
      observed: [
        { title: 'Litware VPN product lifecycle', url: 'https://www.litwareinc.com/support/lifecycle' },
        { title: 'Adatum PDF Reader security bulletin', url: 'https://security.adatum.com/bulletins/2026-11' }
      ],
      items: [
        { about: { name: 'Litware VPN' }, kind: 'end_of_life', title: 'Your VPN app no longer gets security fixes',
          what: 'Litware stopped updating version 3.2 in 2025. Newer versions are free.', sources: ['https://www.litwareinc.com/support/lifecycle'] },
        { about: { name: 'Adatum PDF Reader' }, kind: 'security', title: 'This PDF reader version has a reported security flaw',
          what: 'Adatum fixed a flaw in version 11.0.3 that a booby-trapped PDF could use. This PC has 11.0.2.', sources: ['https://security.adatum.com/bulletins/2026-11'] }
      ] },
    background: { searches: 2,
      observed: [{ title: 'Tailspin Backup 2.1 notes', url: 'https://www.tailspintoys.com/backup/notes' }],
      items: [
        { about: { name: 'Tailspin Backup Service' }, kind: 'bug', title: 'Backups can stop silently after a Windows update',
          what: 'Tailspin Backup 2.0 can stop its service after a Windows update. 2.1 fixes it.', sources: ['https://www.tailspintoys.com/backup/notes'] }
      ] }
  };
  var AREAS = ['hardware', 'drivers', 'windows', 'programs', 'background'];
  var CHECKUP_KINDS = ['bug', 'security', 'end_of_life', 'conflict', 'update'];
  var CHECKUP_STATUSES = ['off', 'idle', 'running', 'done', 'partial', 'failed', 'unavailable'];
  var CK = { STEP_MS: 1500, MAX_ENTRIES: 100, PER_AREA: 3, KEPT: 8, REPORT_CARDS: 8, CALLS: 5, SEARCH_CAP: 6, SEARCH_KILL: 10, OUT_MAX: 2048,
    CALL_MS: 90e3, TOTAL_MS: 8 * 60e3, WEEK_MS: 7 * 864e5 };
  var CAN_CAP = { anthropic_api: 1, openai_api: 1 };     // §6.6: the API kinds cap searches; ChatGPT sign-in and Claude Code cannot
  var MICROSOFT = /^microsoft(\s|$)/i;                     // Microsoft's own in-box items (publisher), sent last

  // §6.8 the names-and-versions projection. A name or version that could be a path, an address, an account or PC name,
  // a serial or an ID is dropped, never cut down: only what is plainly a product, driver or setting name leaves.
  function plain(s) { return String(s == null ? '' : s).replace(/[\u0000-\u001f\u007f-\u009f​-‏‪-‮⁠-⁤﻿]/g, '').replace(/\s+/g, ' ').trim(); }
  function unsafe(s, ident, version) {
    if (/[\\\/@<>"|?*`$%{}\[\]]/.test(s) || /\b[a-z]:/i.test(s) || /:\/\/|\bwww\./i.test(s)) return true;          // paths, URLs, mail, markup
    // IP addresses and MACs (a four-part version number looks like an IPv4 address, so versions skip that one)
    if ((!version && /\b\d{1,3}(?:\.\d{1,3}){3}\b/.test(s)) || /\b[0-9a-f]{1,4}(?:[:-][0-9a-f]{1,4}){4,}\b/i.test(s)) return true;
    if (/\bS-1-\d/i.test(s) || /\b[0-9a-f]{8}-[0-9a-f]{4}-/i.test(s) || /\b[0-9a-f]{12,}\b/i.test(s)) return true; // SIDs, GUIDs, hashes
    if (s.split(/[\s,()]+/).some(function (w) { return w.length >= 10 && /\d/.test(w) && /[A-Za-z]/.test(w) && !/[a-z]{3}/.test(w); })) return true;   // serial-like tokens
    return ident.some(function (x) { return x && new RegExp('(^|[^a-z0-9])' + x.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '($|[^a-z0-9])', 'i').test(s); });
  }
  function safeName(raw, ident) { var s = plain(raw); return s && s.length <= 64 && !unsafe(s, ident) ? s : null; }
  function safeVersion(raw, ident) {
    var s = plain(raw);
    return s && s.length <= 32 && /^[0-9A-Za-z][0-9A-Za-z.+_ ()-]*$/.test(s) && !unsafe(s, ident, true) ? s : null;
  }
  // The PC (inventory + settings + events) → {entries:{<area>: [{id, area, name, publisher?, version?}]}, unsent:{<area>: n}},
  // as the host's CheckupPolicy.BuildSummary (contract §6.10). Only name, publisher and version of a record are read;
  // any other field (serial, instanceId, mac, path, account …) is never looked at. A name that fails the filters drops
  // the record whole; a publisher or version that fails drops only that field.
  //   hardware, drivers, programs, background: third-party records first, then Microsoft's own (publisher Microsoft…),
  //             each group in inventory order.
  //   windows   0 the inventory's windows records (edition, version, build and update level) · 1 the findings' generic
  //             phrases · 2 User Account Control, Secure Boot, Defender real-time protection (on/off), Defender
  //             definitions (version), power mode · 3 critical and error events as "<provider> event <id>, <n> times"
  //             (the most frequent first).
  // Per area, duplicates (name + publisher, ignoring case) are merged into the first, keeping the highest version (the
  // host's CheckupPolicy.Higher) and a count of how many merged (counts[id]: stays in the fixture, never sent, as the
  // host's CheckupEntry.Count); ≤ 100 are sent and the rest counted; ids i1… run across the areas in order.
  function buildSummary(pc, phrases) {
    pc = pc || {};
    var ident = [pc.identity && pc.identity.user, pc.identity && pc.identity.pc].map(plain).filter(function (x) { return x.length >= 2; });
    var cands = [];
    function add(area, rank, name, publisher, version, weight) { cands.push({ area: area, rank: rank, name: name, publisher: publisher, version: version, weight: weight || 0, i: cands.length }); }
    var list = function (x) { return Array.isArray(x) ? x : []; };
    var onOff = function (b) { return b ? 'on' : 'off'; };
    list(pc.inventory).forEach(function (x) {
      if (!x || typeof x !== 'object' || AREAS.indexOf(x.area) < 0) return;
      add(x.area, x.area === 'windows' ? 0 : MICROSOFT.test(plain(x.publisher)) ? 1 : 0, x.name, x.publisher, x.version);
    });
    list(phrases).forEach(function (t) { add('windows', 1, t); });
    var s = pc.settings || {};
    if (typeof s.uac === 'boolean') add('windows', 2, 'User Account Control ' + onOff(s.uac));
    if (typeof s.secureBoot === 'boolean') add('windows', 2, 'Secure Boot ' + onOff(s.secureBoot));
    if (typeof s.defenderRealTime === 'boolean') add('windows', 2, 'Microsoft Defender real-time protection ' + onOff(s.defenderRealTime));
    if (s.defenderDefinitions) add('windows', 2, 'Microsoft Defender definitions', null, s.defenderDefinitions);
    if (s.powerMode) add('windows', 2, 'Power mode ' + plain(s.powerMode));
    // Event-log errors: the provider, the event ID and the count; never the message text.
    list(pc.events).forEach(function (x) {
      if (!x || (x.level !== 'critical' && x.level !== 'error') || !/^\d{1,6}$/.test(String(x.id))) return;
      var c = Math.max(1, Math.min(9999, parseInt(x.count, 10) || 1)), src = safeName(x.provider, ident);
      if (src) add('windows', 3, src + ' event ' + x.id + ', ' + c + (c === 1 ? ' time' : ' times'), null, null, c);
    });
    var out = {}, unsent = {}, counts = {}, n = 0;
    AREAS.forEach(function (area) {
      var kept = [], byKey = {};
      cands.filter(function (c) { return c.area === area; }).sort(function (a, b) { return (a.rank - b.rank) || (b.weight - a.weight) || (a.i - b.i); }).forEach(function (c) {
        var nm = safeName(c.name, ident); if (!nm) return;
        var pub = c.publisher == null || c.publisher === '' ? null : safeName(c.publisher, ident);
        var v = c.version == null || c.version === '' ? null : safeVersion(c.version, ident);
        var k = nm.toLowerCase() + '\u0000' + (pub || '').toLowerCase();
        if (byKey[k]) { byKey[k].version = higherVersion(byKey[k].version, v); byKey[k].count++; return; }
        kept.push(byKey[k] = { name: nm, publisher: pub, version: v, count: 1 });
      });
      out[area] = kept.slice(0, CK.MAX_ENTRIES).map(function (c) {
        var e = { id: 'i' + (++n), area: area, name: c.name };
        counts[e.id] = c.count;
        if (c.publisher) e.publisher = c.publisher;
        if (c.version) e.version = c.version;
        return e;
      });
      unsent[area] = kept.length - out[area].length;
    });
    return { entries: out, unsent: unsent, counts: counts };
  }
  // The host's CheckupPolicy.Higher: the higher of two versions, split on anything not a letter or digit; parts compared
  // in order, numerically when both are digits, else as text ignoring case; a missing version loses; a tie keeps the first.
  function higherVersion(a, b) {
    if (a == null) return b;
    if (b == null) return a;
    var pa = String(a).split(/[^0-9A-Za-z]+/), pb = String(b).split(/[^0-9A-Za-z]+/);
    for (var i = 0; i < Math.max(pa.length, pb.length); i++) {
      var x = i < pa.length ? pa[i] : '', y = i < pb.length ? pb[i] : '', c;
      if (/^\d{1,18}$/.test(x) && /^\d{1,18}$/.test(y)) { var nx = x.replace(/^0+(?=.)/, ''), ny = y.replace(/^0+(?=.)/, ''); c = nx.length !== ny.length ? (nx.length > ny.length ? 1 : -1) : nx > ny ? 1 : nx < ny ? -1 : 0; }
      else { var ux = x.toUpperCase(), uy = y.toUpperCase(); c = ux > uy ? 1 : ux < uy ? -1 : 0; }
      if (c !== 0) return c > 0 ? a : b;
    }
    return a;
  }
  // The area cache key: the entries sent, without their ids (FNV-1a, hex).
  function areaHash(entries) {
    var s = entries.map(function (e) { return e.name + '\u0000' + (e.publisher || '') + '\u0000' + (e.version || ''); }).join('\n'), h = 0x811c9dc5;
    for (var i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; }
    return ('0000000' + h.toString(16)).slice(-8) + ':' + entries.length;
  }

  var STEP_MS = 1200, QUEUE_MS = 300, LOOKUP_MS = 900, MAX_SUBJECTS = 12, CACHE_MS = 30 * 864e5, OPEN_GAP_MS = 1000;
  var STATUSES = ['off', 'queued', 'running', 'done', 'partial', 'cancelled', 'failed', 'unavailable'];
  var ACTIVE = { queued: 1, running: 1 };
  var IN_APP = { anthropic_api: 1, openai_api: 1, chatgpt_plan: 1 };
  var PRIVATE = ['.local', '.localhost', '.internal', '.lan', '.home', '.corp', '.intranet', '.private', '.home.arpa', '.invalid', '.test', '.example', '.onion', '.arpa'];

  // §6.3 URL hygiene (host UrlHygiene.Clean, shell SourceUrl.Clean): https only, a public host name (no IP literal,
  // credentials or port; IDN in punycode), ≤ 300 characters, fragment removed. The cleaned URL, or null.
  function cleanUrl(raw) {
    raw = typeof raw === 'string' ? raw : '';
    if (!raw || raw.length > 2048 || /[\s\u0000-\u001f\u007f-\u009f\\­​-‏‪-‮⁠-⁤﻿]/.test(raw)) return null;
    if (raw.slice(0, 8).toLowerCase() !== 'https://') return null;
    var rest = raw.slice(8), end = rest.search(/[\/?#]/), authority = end < 0 ? rest : rest.slice(0, end);
    if (!authority || /[@:\[%]/.test(authority)) return null;
    var x;
    try { x = new URL(raw); } catch (e) { return null; }
    if (x.protocol !== 'https:' || x.username || x.password || x.port) return null;
    var host = x.hostname.toLowerCase(), labels = host.split('.');
    if (!host || host.length > 253 || host.charAt(0) === '.' || host.charAt(host.length - 1) === '.' || labels.length < 2) return null;
    if (labels.some(function (l) { return !l || l.length > 63 || !/^[a-z0-9-]+$/.test(l) || l.charAt(0) === '-' || l.charAt(l.length - 1) === '-'; })) return null;
    var tld = labels[labels.length - 1];
    if (tld.length < 2 || /^\d+$/.test(tld) || (tld.indexOf('xn--') !== 0 && !/^[a-z]+$/.test(tld))) return null;
    if (host === 'localhost' || PRIVATE.some(function (s) { return ('.' + host).slice(-s.length) === s; })) return null;
    var s = 'https://' + host + x.pathname + x.search;
    return s.length <= 300 ? s : null;
  }
  // Host ResearchPolicy.CleanText: plain text, tags and links removed, capped on a word with "…".
  function cleanText(raw, max) {
    var fold = function (t) { return String(t || '').replace(/[\u0000-\u001f\u007f-\u009f​-‏‪-‮⁠-⁤﻿]/g, '').replace(/\s+/g, ' ').trim(); };
    var s = fold(raw);
    if (!s) return null;
    s = fold(s.replace(/<[^>]{0,200}>/g, ' ').replace(/\b(?:[a-z][a-z0-9+.-]{1,15}:\/\/|www\.)\S+/gi, ' ').replace(/[<>]/g, ' ').replace(/`/g, '\''));
    if (s.length > max) { var cut = s.lastIndexOf(' ', max - 1); s = (cut > max / 2 ? s.slice(0, cut) : s.slice(0, max - 1)).replace(/\s+$/, '') + '…'; }
    return s || null;
  }
  function fileName(p) { var s = String(p || ''); var i = Math.max(s.lastIndexOf('\\'), s.lastIndexOf('/')); return i >= 0 ? s.slice(i + 1) : s; }
  // Host ResearchPolicy.Apply (rules 1–4): what may be applied to one finding, or null.
  function applyRules(severity, subject, verdict, sourceCount, threatFiles) {
    if (sourceCount < 1) return null;                                                          // 1. no observed source
    if (verdict !== 'considered_safe' && verdict !== 'worth_a_look') return null;
    var basis = subject.signed === 'valid' && subject.publisher ? 'name_and_publisher' : 'name_only';
    if (basis !== 'name_and_publisher') verdict = 'worth_a_look';                              // 2. identity
    if (severity === 'high' || severity === 'critical') verdict = 'worth_a_look';               // 3. severity cap
    if (subject.fileKey && threatFiles[subject.fileKey]) verdict = 'worth_a_look';             // 4. threat veto
    return { verdict: verdict, basis: basis };
  }

  function createFixtureResearch(ctx) {
    var E = ctx.rpcError, clone = ctx.clone;
    var lastOpen = null;                                   // the shell's 1 s gap between opens (process memory, like the shell)
    function R() {
      var S = ctx.state();
      if (!S.research) S.research = {};
      var r = S.research;
      ['runs', 'labels', 'sources', 'forced', 'products'].forEach(function (k) { if (!r[k]) r[k] = {}; });
      return r;
    }
    function job(reportId) { return 'research:' + reportId; }
    function web() { var p = ctx.perms(); return !!(p && p['do'] && p['do'].web); }
    function currentReportId() { return (ctx.state().reportOrder || [])[0] || null; }
    function reportIdParam(p) {
      if (typeof p.reportId !== 'string' || !p.reportId || p.reportId.length > 64) throw E(-32602, 'invalid params', { field: 'reportId' });
      return p.reportId;
    }

    /* ---------- subjects (§6.1) ---------- */
    function subjectOf(rep, f) {
      var kind = RESEARCH[f.ruleId];
      if (!kind) return null;
      var cited = (f.evidenceIds || []).map(function (id) { return (rep.evidence.filter(function (e) { return e.id === id; })[0] || {}).value; }).filter(Boolean);
      var image = cited.map(function (v) { return v.image || v.process; }).filter(Boolean)[0];
      var name = image ? fileName(image).slice(0, 64) : null;
      if (!name) return null;
      var stated = cited.filter(function (v) { return v.signed || v.signature; })[0] || {}, sig = stated.signed || stated.signature;
      var signed = sig === 'valid' ? 'valid' : sig === 'none' || sig === 'unsigned' || sig === 'invalid' ? 'none' : 'unknown';
      var ports = [];
      if (kind === 'port') cited.forEach(function (v) {
        (v.ports || []).concat(v.port != null && v.protocol ? [v.port + '/' + v.protocol] : []).forEach(function (x) { if (/^\d{1,5}\/(tcp|udp)$/.test(x) && ports.indexOf(x) < 0) ports.push(x); });
      });
      ports.sort(function (a, b) { return parseInt(a, 10) - parseInt(b, 10) || (a < b ? -1 : a > b ? 1 : 0); });
      var s = { kind: kind, name: name, publisher: signed === 'valid' ? stated.publisher || null : null, product: signed === 'valid' ? stated.product || null : null,
        signed: signed, ports: ports.slice(0, 8), what: WHAT[kind], fileKey: name.toLowerCase(), findingIds: [] };
      s.productKey = ['rp', kind, name.toLowerCase(), s.publisher || '', s.product || '', signed, s.ports.join(',')].join('|');
      return s;
    }
    function subjectsOf(rep) {
      var byKey = {}, ordered = [];
      rep.findings.forEach(function (f) {
        var s = subjectOf(rep, f);
        if (!s) return;
        if (byKey[s.productKey]) { byKey[s.productKey].findingIds.push(f.id); return; }
        if (ordered.length >= MAX_SUBJECTS) return;
        s.findingIds.push(f.id); byKey[s.productKey] = s; ordered.push(s);
      });
      return ordered;
    }
    function threatFilesOf(rep) {
      var out = {};
      rep.evidence.forEach(function (e) { ((e.value || {}).resourceFiles || []).forEach(function (n) { var k = fileName(n).trim().toLowerCase(); if (k) out[k] = 1; }); });
      return out;
    }

    /* ---------- the stores ---------- */
    function itemsOf(reportId) {
      var labels = R().labels[reportId] || {}, rep = ctx.state().reports[reportId];
      var order = rep ? rep.findings.map(function (f) { return f.id; }) : Object.keys(labels);
      return order.filter(function (id) { return labels[id]; }).map(function (id) { return clone(labels[id]); });
    }
    function view(reportId) {
      ctx.getReport(reportId);
      var run = R().runs[reportId] || null, active = !!(run && ACTIVE[run.status]);
      var status = !web() ? 'off' : run ? run.status : ctx.available().web === 'no' ? 'unavailable' : 'off';
      return { runId: run ? run.runId : null, status: status, done: run ? run.done : 0, total: run ? run.total : 0,
        trial: active && !!run.trial, items: itemsOf(reportId),
        usage: run ? clone(run.usage) : { searches: 0, inputTokens: 0, outputTokens: 0 }, checkup: checkupView(reportId) };
    }
    function emitState(run) {
      ctx.emit('user', 'research.state', { reportId: run.reportId, runId: run.runId, status: run.status, done: run.done, total: run.total,
        trial: !!(ACTIVE[run.status] && run.trial), phase: 'labels' });
    }
    function cachedProduct(key) {
      var c = R().products[key];
      return c && Date.parse(c.expiresAt) > Date.now() ? c.body : null;
    }
    // A fresh answer → observed sources stored by id, the general result cached for the product.
    function storeAnswer(subject, answer, by) {
      var forced = R().forced[subject.name.toLowerCase()];
      if (forced && forced.verdict === null) return null;
      if (!answer) return null;
      var sources = (answer.sources || []).map(function (s) {
        var url = cleanUrl(s.url);
        if (!url) return null;                                    // a URL that fails hygiene is dropped at store time
        var host = new URL(url).hostname, id = 'src-' + ctx.randomHex(16), title = cleanText(s.title, 120) || host;
        R().sources[id] = { url: url, title: title, domain: host };
        return { sourceId: id, title: title, domain: host };
      }).filter(Boolean).slice(0, 3);
      if (!sources.length) return null;
      var at = new Date().toISOString();
      var body = { verdict: forced ? forced.verdict : answer.verdict, summary: cleanText(answer.summary, 400), sources: sources, by: clone(by), researchedAt: at };
      R().products[subject.productKey] = { body: body, expiresAt: new Date(Date.now() + CACHE_MS).toISOString() };
      return body;
    }
    // The general result applied to each finding of the subject in this report, after the host rules ran on it.
    function applyLabels(run, subject, body) {
      var rep = ctx.state().reports[run.reportId], threats = threatFilesOf(rep), any = false;
      var forced = R().forced[subject.name.toLowerCase()];
      subject.findingIds.forEach(function (fid) {
        var f = rep.findings.filter(function (x) { return x.id === fid; })[0];
        var applied = applyRules(f ? f.severity : 'info', subject, body.verdict, body.sources.length, threats);
        if (!applied) return;
        if (forced && forced.basis) applied.basis = forced.basis;
        var labels = R().labels[run.reportId] = R().labels[run.reportId] || {};
        labels[fid] = { findingId: fid, verdict: applied.verdict, summary: body.summary, sources: clone(body.sources), basis: applied.basis,
          by: clone(body.by), researchedAt: body.researchedAt };
        any = true;
      });
      return any;
    }

    /* ---------- runs ---------- */
    function cancelRun(run) {
      if (!run || !ACTIVE[run.status]) return false;
      run.status = 'cancelled'; run.trial = false;                // marked first: a late result stores nothing
      ctx.stop(job(run.reportId));
      emitState(run); ctx.save();
      return true;
    }
    function cancelAll(kind) {
      var any = false;
      Object.keys(R().runs).forEach(function (id) { var r = R().runs[id]; if (!kind || r.by.kind === kind) any = cancelRun(r) || any; });
      return any;
    }
    // §6.3: true when a result may be stored. Otherwise the run is cancelled.
    function fence(run) {
      if (R().runs[run.reportId] !== run || !ACTIVE[run.status]) return false;
      var p = ctx.perms(), prov = ctx.provider();
      var ok = !!(p && p['do'].web && p.revision === run.permissionsRevision && prov && prov.kind === run.by.kind && prov.model === run.by.model &&
        currentReportId() === run.reportId);
      if (!ok) cancelRun(run);
      return ok;
    }
    function finish(run, status) {
      if (!ACTIVE[run.status]) return;
      run.status = status; run.trial = false;
      emitState(run); ctx.save();
      afterLabels(run);
    }
    // §6.8: the check-up follows the per-finding research of the report (not after a cancelled run, nor when the
    // provider refused the search tool).
    function afterLabels(run) {
      if (run.status === 'cancelled' || run.status === 'unavailable') return;
      ctx.later(CK_JOB, function () { startCheckup(run.reportId); }, QUEUE_MS);
    }
    function start(reportId, auto) {
      var rep = ctx.getReport(reportId);
      if (!web()) { if (auto) return null; throw E(-32013, 'invalid_state', { reason: 'web_off' }); }
      var prov = ctx.provider();
      if (!prov) { if (auto) return null; throw E(-32013, 'invalid_state', { reason: 'no_provider' }); }
      var cur = R().runs[reportId];
      if (cur && ACTIVE[cur.status]) return auto ? null : view(reportId);
      if (auto && currentReportId() !== reportId) return null;
      // One run at a time: a run for another report ends.
      Object.keys(R().runs).forEach(function (id) { if (id !== reportId) cancelRun(R().runs[id]); });
      var webState = ctx.webFor(prov.kind, prov.model);
      if (auto && webState === 'no') return null;
      var subjects = subjectsOf(rep);
      var status = webState === 'no' ? 'unavailable' : subjects.length ? 'queued' : 'done';
      var sim = R().nextRun || {}; delete R().nextRun;
      var run = { runId: 'research-' + ctx.randomHex(16), reportId: reportId, status: status, done: 0, total: subjects.length, subjects: subjects,
        trial: status === 'queued' && webState === 'unknown', permissionsRevision: ctx.perms().revision, by: { kind: prov.kind, label: prov.label, model: prov.model },
        usage: { searches: 0, inputTokens: 0, outputTokens: 0 }, startedAt: new Date().toISOString(), failed: 0, reached: 0, calls: 0,
        failCalls: sim.failCalls != null ? sim.failCalls : 0, budgetCalls: sim.budgetCalls != null ? sim.budgetCalls : null };
      R().runs[reportId] = run;
      emitState(run); ctx.save();
      if (status === 'done') afterLabels(run);             // nothing to label: straight to the check-up
      if (status !== 'queued') return auto ? null : view(reportId);
      ctx.later(job(reportId), function () { execute(run); }, QUEUE_MS);
      return auto ? null : view(reportId);
    }
    function execute(run) {
      if (!fence(run)) return;
      run.status = 'running'; emitState(run); ctx.save();
      if (IN_APP[run.by.kind] && !ctx.hasCredential(run.by.kind)) { finish(run, 'failed'); return; }   // no key or sign-in
      // Cached products are not researched again; the host rules are re-applied to this report every time.
      var pending = [];
      for (var i = 0; i < run.subjects.length; i++) {
        var s = run.subjects[i], body = cachedProduct(s.productKey);
        if (!body) { pending.push(s); continue; }
        if (!fence(run)) return;
        run.done++; run.trial = false; run.reached++;
        applyLabels(run, s, body);
        emitState(run); ctx.save();
      }
      if (!pending.length) { finish(run, 'done'); return; }
      var step = function (k) {
        ctx.later(job(run.reportId), function () {
          if (!ACTIVE[run.status]) return;
          // The run's time budget ran out: what was reached stays; the run ends partial.
          if (run.budgetCalls != null && run.calls >= run.budgetCalls) { finish(run, 'partial'); return; }
          var subject = pending[k];
          run.calls++;
          if (run.trial && R().trialOutcome === 'no') {
            // Refused as unsupported: web "no" for this kind and model; the run ends unavailable; nothing is stored.
            delete R().trialOutcome;
            if (!fence(run)) return;
            ctx.setWeb(run.by.kind, run.by.model, 'no');
            finish(run, 'unavailable'); return;
          }
          if (run.failCalls === 'all' || run.failCalls > 0) {
            // A sign-in, network, rate-limit or timeout failure: availability unchanged; the subject stays unlabelled.
            if (run.failCalls !== 'all') run.failCalls--;
            if (!fence(run)) return;
            run.done++; run.trial = false; run.failed++; run.reached++;
            emitState(run); ctx.save();
          } else {
            if (!fence(run)) return;
            var answer = ANSWERS[subject.name.toLowerCase()] || { verdict: 'considered_safe', summary: subject.name + ' is a program.', sources: [] };
            run.usage.searches += 2; run.usage.inputTokens += 900; run.usage.outputTokens += 140;
            run.done++; run.trial = false; run.reached++;
            if ((answer.sources || []).length) ctx.setWeb(run.by.kind, run.by.model, 'yes');   // observed sources → yes
            var body = storeAnswer(subject, answer, run.by);
            if (body) applyLabels(run, subject, body);
            emitState(run); ctx.save();
          }
          if (k + 1 < pending.length) { step(k + 1); return; }
          finish(run, run.reached < run.total ? 'partial' : run.failed === 0 ? 'done' : run.failed === run.total ? 'failed' : 'partial');
        }, STEP_MS);
      };
      step(0);
    }

    /* ---------- §6.8 the broad check-up ---------- */
    var CK_JOB = 'checkup';
    function C() {
      var r = R();
      if (!r.checkup) r.checkup = { status: 'idle', by: null, lastAt: null, areas: {}, run: null, clock: 0, calls: [] };
      return r.checkup;
    }
    // The fixture clock (fixture.checkupTick moves it on); the check-up's times and its 7-day gates read it.
    function now() { return Date.now() + (C().clock || 0); }
    function iso(ms) { return new Date(ms).toISOString(); }
    function sameProvider(by, prov) { return !!(by && prov && by.kind === prov.kind && by.model === prov.model); }
    function checkupCards(reportId) {
      var fu = ctx.state().followups[reportId]; if (!fu) return [];
      var order = fu.queue.concat(fu.decisions.map(function (d) { return d.cardId; }), Object.keys(fu.cards));
      return order.filter(function (cid, i) { return order.indexOf(cid) === i && fu.cards[cid] && fu.cards[cid].checkup; });
    }
    // research.get .checkup = {status, lastAt, cards, missed}: off without web or an AI; unavailable when the provider can't search;
    // else the last check-up's outcome for this provider (idle when there is none, or it was cancelled).
    function checkupView(reportId) {
      var c = C(), prov = ctx.provider(), status;
      if (c.forced) status = c.forced;
      else if (!web() || !prov) status = 'off';
      else if (c.run && c.run.status === 'running') status = 'running';
      else if (ctx.webFor(prov.kind, prov.model) === 'no') status = 'unavailable';
      else status = sameProvider(c.by, prov) ? c.status : 'idle';
      // missed: the areas a partial check-up did not check (research.schema.json checkup.missed); empty otherwise.
      return { status: status, lastAt: c.lastAt, cards: checkupCards(reportId), missed: status === 'partial' ? (c.missed || []).slice() : [] };
    }
    function emitCheckup(run) {
      ctx.emit('user', 'research.state', { reportId: run.reportId, runId: run.runId, status: run.status, done: run.done, total: run.total, trial: false, phase: 'checkup' });
    }
    function genericTitles() {
      var agent = (root && root.OptTool && root.OptTool.createFixtureAgent) || (typeof require === 'function' ? require('./adapter-fixture-agent.js').createFixtureAgent : null);
      return (agent && agent.GENERIC_TITLES) || {};
    }
    function summaryFor(reportId) {
      var rep = ctx.state().reports[reportId], pc = clone(C().pc || CHECKUP_PC);
      // The report's own event-log count (fixture kernel-power evidence) replaces the synthetic one.
      var kp = rep ? rep.evidence.filter(function (e) { return e.collectorId === 'fixture.kernelpower' && e.value && e.value.count != null; })[0] : null;
      if (kp && !C().pc) pc.events = [{ provider: 'Microsoft-Windows-Kernel-Power', level: 'critical', id: 41, count: kp.value.count }];
      // The findings as the host sends them: FindingTitles.Generic by rule (a rule without a phrase is not sent).
      var generic = genericTitles();
      var phrases = rep && C().phrases !== false ? rep.findings.map(function (f) { return generic[f.ruleId]; }).filter(function (t, i, a) { return t && a.indexOf(t) === i; }) : [];
      return buildSummary(pc, phrases);
    }
    function cancelCheckup(kind) {
      var c = C(), run = c.run;
      if (!run || run.status !== 'running' || (kind && run.by.kind !== kind)) return false;
      run.status = 'cancelled'; c.status = 'idle';               // marked first: a late result stores nothing
      ctx.stop(CK_JOB);
      emitCheckup(run); ctx.save();
      return true;
    }
    // The same fence as a research run (§6.3), for the check-up run.
    function fenceCheckup(run) {
      if (C().run !== run || run.status !== 'running') return false;
      var p = ctx.perms(), prov = ctx.provider();
      var ok = !!(p && p['do'].web && p.revision === run.permissionsRevision && sameProvider(run.by, prov) && currentReportId() === run.reportId);
      if (!ok) cancelCheckup(null);
      return ok;
    }
    function endCheckup(run, status) {
      if (run.status !== 'running') return;
      var c = C();
      run.status = status; c.status = status; c.by = clone(run.by);
      c.missed = status === 'partial' ? run.areas.filter(function (a) { return (run.ok || []).indexOf(a) < 0; }) : [];
      if (status === 'done' || status === 'partial') c.lastAt = iso(now());
      emitCheckup(run); ctx.save();
    }
    // Starts a check-up of the newest report's PC when allowed. Areas whose entries are unchanged and were checked
    // within 7 days are skipped (no call). true when a check-up ran or started.
    function startCheckup(reportId) {
      var c = C(), prov = ctx.provider();
      if (!web() || !prov || currentReportId() !== reportId) return false;
      if (c.run && c.run.status === 'running') return false;
      var lr = R().runs[reportId]; if (lr && ACTIVE[lr.status]) return false;   // the labels go first
      if (ctx.webFor(prov.kind, prov.model) === 'no') return false;
      var summary = summaryFor(reportId), t = now();
      if (!AREAS.some(function (a) { return summary.entries[a].length; })) return false;   // an empty summary creates no run
      var areas = AREAS.filter(function (a) {
        var e = summary.entries[a], hit = c.areas[a];
        return e.length && !(hit && hit.hash === areaHash(e) && t - Date.parse(hit.at) < CK.WEEK_MS);
      });
      var sim = c.next || {}; delete c.next;
      var run = c.run = { runId: 'research-' + ctx.randomHex(16), reportId: reportId, status: 'running', done: 0, total: areas.length, areas: areas, summary: summary,
        permissionsRevision: ctx.perms().revision, by: { kind: prov.kind, label: prov.label, model: prov.model },
        usage: { searches: 0, inputTokens: 0, outputTokens: 0 }, kept: 0, failed: 0, reached: 0, calls: 0, startedAt: iso(t),
        failCalls: sim.failCalls || 0, budgetCalls: sim.budgetCalls != null ? sim.budgetCalls : null, outcome: sim.outcome || null, searches: sim.searches != null ? sim.searches : null };
      c.calls = [];
      emitCheckup(run); ctx.save();
      if (!areas.length) { endCheckup(run, 'done'); return true; }                // all cached: costs nothing
      if (IN_APP[run.by.kind] && !ctx.hasCredential(run.by.kind)) { endCheckup(run, 'failed'); return true; }   // no key or sign-in
      stepCheckup(run, 0);
      return true;
    }
    // §6.8 host rules on one result: `about` is an entry sent in this call, a known kind, plain text, ≥ 1 observed source.
    function checkItem(it, sent, observed) {
      if (!it || typeof it !== 'object' || typeof it.about !== 'string' || !sent[it.about] || CHECKUP_KINDS.indexOf(it.kind) < 0) return null;
      var title = cleanText(it.title, 100); if (!title) return null;
      var urls = (Array.isArray(it.sources) ? it.sources : []).map(cleanUrl).filter(function (u, i, a) { return u && observed[u] && a.indexOf(u) === i; }).slice(0, 3);
      if (!urls.length) return null;
      return { entry: sent[it.about], kind: it.kind, title: title, what: cleanText(it.what, 400) || '', urls: urls };
    }
    // What the simulated model answers for one area: `about` by entry name is mapped to the id it was sent under.
    function answerFor(area, entries) {
      var a = clone(((C().answers || CHECKUP_ANSWERS)[area]) || { searches: 1, observed: [], items: [] });
      a.items = (a.items || []).map(function (it) {
        if (it && it.about && typeof it.about === 'object') {
          var e = entries.filter(function (x) { return x.name.toLowerCase() === String(it.about.name).toLowerCase(); })[0];
          it.about = e ? e.id : 'i0';
        }
        return it;
      });
      return a;
    }
    var KIND_CATEGORY = { security: 'security', end_of_life: 'security' };
    // Results → cards through the AI-card path (origin ai, web-tainted, no action). The same entry (area + name) and
    // kind already on the report replaces that card in place (its place in the deck and any decision stay).
    function storeResults(run, area, kept) {
      var fu = ctx.state().followups[run.reportId];
      if (!fu || !kept.length) return Promise.resolve();
      return Promise.all(kept.map(function (k) { return ctx.H(run.reportId, 'checkup', area, k.entry.name.toLowerCase(), k.kind); })).then(function (hs) {
        if (!fenceCheckup(run)) return;
        kept.forEach(function (k, i) {
          var key = area + '|' + k.entry.name.toLowerCase() + '|' + k.kind;
          var sources = k.urls.map(function (u) {
            var host = new URL(u).hostname, id = 'src-' + ctx.randomHex(16), title = cleanText(k.observed[u], 120) || host;
            R().sources[id] = { url: u, title: title, domain: host };
            return { sourceId: id, title: title, domain: host };
          });
          var cid = Object.keys(fu.cards).filter(function (x) { return fu.cards[x].checkupKey === key; })[0] || null;
          if (!cid && Object.keys(fu.cards).filter(function (x) { return fu.cards[x].checkup; }).length >= CK.REPORT_CARDS) return;   // ≤ 8 per report
          var fields = { category: KIND_CATEGORY[k.kind] || 'diagnostics', title: k.title, text: k.what || k.title, why: k.what || k.title,
            checkup: { kind: k.kind, sources: sources, checkedAt: iso(now()) }, addedBy: run.by.label, aiTainted: true };
          if (cid) { Object.assign(fu.cards[cid], fields); fu.revision += 1; return; }
          cid = 'card-' + hs[i];
          fu.cards[cid] = Object.assign({ id: cid, findingId: null, evidenceIds: [], options: ['Ask about this', 'Ignore'], guide: null, preview: null, actionId: null,
            settingsUri: null, origin: 'ai', findingIds: [], aiTaskId: null, checkupKey: key }, fields);
          fu.queue.push(cid); fu.total += 1; fu.revision += 1;
        });
      });
    }
    function stepCheckup(run, k) {
      ctx.later(CK_JOB, function () {
        if (run.status !== 'running') return;
        // The time budget (≤ 8 minutes in all) ran out, or the call budget (≤ 5): what was reached stays.
        if ((run.budgetCalls != null && run.calls >= run.budgetCalls) || run.calls >= CK.CALLS) { endCheckup(run, 'partial'); return; }
        var area = run.areas[k], entries = run.summary.entries[area];
        run.calls++;
        C().calls.push({ area: area, prompt: 'checkup-v2', entries: clone(entries), unsent: run.summary.unsent[area] });
        var next = function () {
          if (run.status !== 'running') return;
          if (k + 1 < run.areas.length) { stepCheckup(run, k + 1); return; }
          endCheckup(run, run.reached < run.total ? 'partial' : run.failed === 0 ? 'done' : run.failed === run.total ? 'failed' : 'partial');
        };
        var failed = function () { if (!fenceCheckup(run)) return false; run.done++; run.reached++; run.failed++; emitCheckup(run); ctx.save(); return true; };
        if (run.outcome === 'unsupported') {
          // Refused as unsupported: web "no" for this kind and model; nothing is stored.
          if (!fenceCheckup(run)) return;
          ctx.setWeb(run.by.kind, run.by.model, 'no'); endCheckup(run, 'unavailable'); return;
        }
        if (run.failCalls === 'all' || run.failCalls > 0) {
          // A sign-in, network, rate-limit or timeout failure: availability unchanged; the area stays unchecked (not cached).
          if (run.failCalls !== 'all') run.failCalls--;
          if (failed()) next(); return;
        }
        var answer = answerFor(area, entries), searches = run.searches != null ? run.searches : answer.searches || 0;
        if (!CAN_CAP[run.by.kind] && searches > CK.SEARCH_KILL) {
          // A provider that cannot cap its searches went over 10: the call is killed and counted; nothing stored.
          run.usage.searches += CK.SEARCH_KILL + 1;
          if (failed()) next(); return;
        }
        run.usage.searches += CAN_CAP[run.by.kind] ? Math.min(searches, CK.SEARCH_CAP) : searches;
        run.usage.inputTokens += 1200 + entries.length * 12; run.usage.outputTokens += 260;
        if (JSON.stringify(answer.items || []).length > CK.OUT_MAX) { if (failed()) next(); return; }   // output over 2 KB: cut, unusable
        if (!fenceCheckup(run)) return;
        var observed = {};
        (answer.observed || []).forEach(function (o) { var u = cleanUrl(o && o.url); if (u && !observed[u]) observed[u] = o.title || ''; });
        if (Object.keys(observed).length) ctx.setWeb(run.by.kind, run.by.model, 'yes');   // observed sources → yes (§6.4)
        var sent = {}; entries.forEach(function (e) { sent[e.id] = e; });
        var kept = (answer.items || []).map(function (it) { return checkItem(it, sent, observed); }).filter(Boolean)
          .slice(0, Math.max(0, Math.min(CK.PER_AREA, CK.KEPT - run.kept)));
        kept.forEach(function (x) { x.observed = observed; });
        run.kept += kept.length;
        storeResults(run, area, kept).then(function () {
          if (run.status !== 'running') return;
          C().areas[area] = { hash: areaHash(entries), at: iso(now()) };
          (run.ok = run.ok || []).push(area);
          run.done++; run.reached++;
          emitCheckup(run); ctx.save();
          next();
        });
      }, CK.STEP_MS);
    }
    // "Between scans at most once every 7 days, only while the app is open": the host's hourly check.
    function weeklyCheck() {
      var c = C(), id = currentReportId();
      if (!id || (c.lastAt && now() - Date.parse(c.lastAt) < CK.WEEK_MS)) return false;
      return startCheckup(id);
    }

    // §7 web_lookup, as the host's LookupAsync (§6.7): the stored label first; else, unless the task's provider has web
    // "no", a cached product with the host rules re-applied; else ONE fresh call on the task's own provider. Either way
    // the label is stored on the report like a research result, through the lookup fence (fence() → a tool error or null).
    // Resolves {status:"done", item} | {status:"none"} | {status:"unavailable"} (web "no") | {error:{code, message}}
    // (a transient failure or the fence: unavailable, timeout, cancelled, not_allowed; availability unchanged).
    function lookup(reportId, findingId, prov, fence) {
      var rep = ctx.getReport(reportId), f = rep.findings.filter(function (x) { return x.id === findingId; })[0];
      var subject = f ? subjectOf(rep, f) : null;
      if (!subject) return Promise.resolve({ status: 'not_eligible' });
      function stored() { var it = (R().labels[reportId] || {})[findingId]; return it ? { status: 'done', item: clone(it) } : null; }
      if (stored()) return Promise.resolve(stored());
      if (!prov || !prov.kind || ctx.webFor(prov.kind, prov.model) === 'no') return Promise.resolve({ status: 'unavailable' });
      var one = Object.assign({}, subject, { findingIds: [findingId] }), into = { reportId: reportId };
      var by = { kind: prov.kind, label: prov.label, model: prov.model };
      var cached = cachedProduct(subject.productKey);
      if (cached) {
        var blocked = fence ? fence() : null;
        if (blocked) return Promise.resolve(blocked);
        applyLabels(into, one, cached); ctx.save();
        return Promise.resolve(stored() || { status: 'none' });
      }
      if (IN_APP[prov.kind] && !ctx.hasCredential(prov.kind)) return Promise.resolve({ error: { code: 'unavailable', message: 'The AI is not signed in, so the lookup could not run.' } });
      var outcome = R().lookupOutcome || null; delete R().lookupOutcome;
      return new Promise(function (resolve) { ctx.later('lookup', resolve, LOOKUP_MS); }).then(function () {
        // Unsupported (the tool or flag refused): web "no"; nothing stored.
        if (outcome === 'unsupported') { ctx.setWeb(prov.kind, prov.model, 'no'); ctx.save(); return { status: 'unavailable' }; }
        if (outcome === 'timeout') return { error: { code: 'timeout', message: 'The lookup did not answer in time.' } };
        if (outcome === 'cancelled') return { error: { code: 'cancelled', message: 'The call was cancelled.' } };
        if (outcome === 'unavailable') return { error: { code: 'unavailable', message: 'The lookup could not run. Try again later.' } };
        var blocked2 = fence ? fence() : null;
        if (blocked2) return blocked2;
        var answer = ANSWERS[subject.name.toLowerCase()];
        if (answer && (answer.sources || []).length) ctx.setWeb(prov.kind, prov.model, 'yes');   // observed sources (§6.4)
        var body = storeAnswer(one, answer, by);
        if (body) applyLabels(into, one, body);
        ctx.save();
        return stored() || { status: 'none' };
      });
    }

    var methods = {
      'research.get': function (p) { return view(reportIdParam(p)); },
      'research.start': function (p) {
        return ctx.withIdem('research.start', p, function () { return start(reportIdParam(p), false); });
      },
      // Both phases: the report's research run and a check-up running for it.
      'research.cancel': function (p) {
        var id = reportIdParam(p); ctx.getReport(id); cancelRun(R().runs[id]);
        if (C().run && C().run.reportId === id) cancelCheckup(null);
        return view(id);
      },
      // Shell-handled on the desktop (SourceOpener): the shell fetches the stored URL (research.source, shell-only),
      // re-validates it and opens the browser. The page passes only the id. Here: the same checks, and the browser demo
      // opens a new tab.
      'shell.openSource': function (p) {
        if (!p || Object.keys(p).length !== 1 || typeof p.sourceId !== 'string' || !/^src-[0-9a-f]{8,32}$/.test(p.sourceId))
          throw E(-32602, 'invalid params', { field: 'sourceId' });
        var s = R().sources[p.sourceId];
        if (!s) return { status: 'unknown' };
        var url = cleanUrl(s.url);
        if (!url || url !== s.url) return { status: 'blocked' };
        var now = Date.now();
        if (lastOpen !== null && now - lastOpen < OPEN_GAP_MS) throw E(-32013, 'Too many requests.', { reason: 'rate_limited' });
        lastOpen = now;
        try { if (ctx.open) ctx.open(url); } catch (e) { throw E(-32013, 'Could not open.', { reason: 'open_failed' }); }
        return { status: 'opened' };
      },
      // Dev hook: force a run state for the newest (or the given) report, a verdict for a program (null = no label), or
      // how the next run's calls go.
      'fixture.setResearch': function (p) {
        var reportId = p.reportId || currentReportId();
        if (p.label) {
          var v = p.label.verdict;
          if (v !== null && v !== 'considered_safe' && v !== 'worth_a_look') throw E(-32602, 'invalid params', { field: 'label.verdict' });
          var rep = reportId ? ctx.getReport(reportId) : null;
          var f = rep && rep.findings.filter(function (x) {
            var s = subjectOf(rep, x);
            return s && (p.label.findingId ? x.id === p.label.findingId : s.name.toLowerCase() === String(p.label.name || '').toLowerCase());
          })[0];
          var subject = f ? subjectOf(rep, f) : null;
          var name = subject ? subject.name.toLowerCase() : String(p.label.name || '').toLowerCase();
          if (!name) throw E(-32602, 'invalid params', { field: 'label.name' });
          R().forced[name] = { verdict: v, basis: p.label.basis || null };
          if (subject) {
            delete R().products[subject.productKey];
            var labels = R().labels[reportId] = R().labels[reportId] || {};
            subjectsOf(rep).filter(function (s) { return s.productKey === subject.productKey; }).forEach(function (s) {
              s.findingIds.forEach(function (id) { delete labels[id]; });
              var prov = ctx.provider() || { kind: 'anthropic_api', label: 'Claude (API)', model: 'claude-fixture' };
              var answer = ANSWERS[name] || { verdict: v || 'considered_safe', summary: subject.name + ' is a program.',
                sources: [{ title: subject.name, url: 'https://www.example.org/' + encodeURIComponent(name) }] };
              var body = storeAnswer(s, answer, { kind: prov.kind, label: prov.label, model: prov.model });
              if (body) applyLabels({ reportId: reportId }, s, body);
            });
          }
        }
        if (p.trialOutcome) R().trialOutcome = p.trialOutcome;
        if (p.lookupOutcome != null) {
          if (['unsupported', 'unavailable', 'timeout', 'cancelled'].indexOf(p.lookupOutcome) < 0) throw E(-32602, 'invalid params', { field: 'lookupOutcome' });
          R().lookupOutcome = p.lookupOutcome;
        }
        if (p.failCalls != null || p.budgetCalls != null) {
          if (p.failCalls != null && p.failCalls !== 'all' && !(p.failCalls >= 0)) throw E(-32602, 'invalid params', { field: 'failCalls' });
          if (p.budgetCalls != null && !(p.budgetCalls >= 0)) throw E(-32602, 'invalid params', { field: 'budgetCalls' });
          R().nextRun = { failCalls: p.failCalls != null ? p.failCalls : 0, budgetCalls: p.budgetCalls != null ? p.budgetCalls : null };
        }
        if (p.status) {
          if (STATUSES.indexOf(p.status) < 0) throw E(-32602, 'invalid params', { field: 'status' });
          if (!reportId) throw E(-32001, 'not_found', { kind: 'report', id: null });
          var run = R().runs[reportId];
          if (run) ctx.stop(job(reportId));
          run = R().runs[reportId] = Object.assign(run || { runId: 'research-' + ctx.randomHex(16), reportId: reportId, subjects: [], by: { kind: null, label: null, model: null },
            usage: { searches: 0, inputTokens: 0, outputTokens: 0 }, permissionsRevision: (ctx.perms() || {}).revision },
          { status: p.status, done: p.done != null ? p.done : 0, total: p.total != null ? p.total : 0, trial: !!p.trial && !!ACTIVE[p.status] });
          emitState(run);
        }
        ctx.save();
        return reportId ? view(reportId) : null;
      },
      // Dev hook (§6.8): status forces what research.get says (null clears it; "running" also shows the chip);
      // failCalls / budgetCalls / outcome:"unsupported" / searches shape the NEXT check-up; pc ({identity, inventory,
      // settings, events}) and answers ({hardware|drivers|windows|programs|background: {searches, observed:[{title,url}],
      // items}}) replace the synthetic ones (null restores).
      'fixture.setCheckup': function (p) {
        var c = C(), reportId = currentReportId();
        if (p.failCalls != null || p.budgetCalls != null || p.outcome != null || p.searches != null) {
          if (p.failCalls != null && p.failCalls !== 'all' && !(p.failCalls >= 0)) throw E(-32602, 'invalid params', { field: 'failCalls' });
          if (p.budgetCalls != null && !(p.budgetCalls >= 0)) throw E(-32602, 'invalid params', { field: 'budgetCalls' });
          if (p.outcome != null && p.outcome !== 'unsupported') throw E(-32602, 'invalid params', { field: 'outcome' });
          if (p.searches != null && !(p.searches >= 0)) throw E(-32602, 'invalid params', { field: 'searches' });
          c.next = { failCalls: p.failCalls || 0, budgetCalls: p.budgetCalls != null ? p.budgetCalls : null, outcome: p.outcome || null, searches: p.searches != null ? p.searches : null };
        }
        if (p.pc !== undefined) c.pc = p.pc;
        if (p.phrases !== undefined) c.phrases = p.phrases === false ? false : null;   // false: send no finding phrases (an empty summary)
        if (p.answers !== undefined) c.answers = p.answers;
        if (p.status !== undefined) {
          if (p.status !== null && CHECKUP_STATUSES.indexOf(p.status) < 0) throw E(-32602, 'invalid params', { field: 'status' });
          if (c.run && c.run.status === 'running') { ctx.stop(CK_JOB); c.run.status = 'cancelled'; }
          c.forced = p.status;
          if (reportId) {
            // A state event so the page re-reads: the forced status when it is a run status, else "cancelled".
            var st = p.status === 'running' || p.status === 'done' || p.status === 'partial' || p.status === 'failed' || p.status === 'unavailable' ? p.status : 'cancelled';
            emitCheckup({ reportId: reportId, runId: 'research-' + ctx.randomHex(16), status: st, done: 0, total: st === 'running' ? AREAS.length : 0 });
          }
        }
        ctx.save();
        return reportId ? view(reportId) : null;
      },
      // Dev hook (§6.8): moves the fixture clock on by `days` (default 0) and runs the hourly check: a check-up starts
      // when the last one ended 7 days ago or more (or there was none), web is on and the AI can search.
      'fixture.checkupTick': function (p) {
        if (p.days != null && !(typeof p.days === 'number' && p.days >= 0 && p.days <= 366)) throw E(-32602, 'invalid params', { field: 'days' });
        C().clock += (p.days || 0) * 864e5;
        var started = weeklyCheck(), id = currentReportId();
        ctx.save();
        return { started: started, now: iso(now()), checkup: id ? checkupView(id) : null };
      },
      // Dev hook (§6.8): what the last check-up's calls were sent, and its usage.
      'fixture.getCheckup': function () {
        var c = C();
        return { calls: clone(c.calls || []), usage: c.run ? clone(c.run.usage) : null, areas: clone(c.areas), now: iso(now()) };
      }
    };

    return {
      methods: methods,
      // adapter-fixture.js: a scan ended with a report (only finished scans research).
      onScanFinished: function (rep) { try { return start(rep.reportId, true); } catch (e) { return null; } },
      // adapter-fixture.js: a new scan cancels any running research (its report is about to stop being current).
      onScanStarted: function () { cancelAll(null); cancelCheckup(null); },
      // adapter-fixture-agent.js, inside onboarding.save: a change of the chosen AI cancels a running run and check-up.
      onChoiceChanged: function () { cancelAll(null); cancelCheckup(null); },
      // adapter-fixture-agent.js, inside ai.setPermissions: ANY save cancels an active run and a running check-up (the
      // revision changed); research starts again for the newest report when web stays on and something was cancelled,
      // or web was just turned on (the check-up follows it; areas checked before the cancel stay cached).
      onPermissionsSaved: function (before, after) {
        var cancelled = cancelAll(null);
        cancelled = cancelCheckup(null) || cancelled;
        if (after['do'].web && (cancelled || !before['do'].web)) {
          var id = currentReportId();
          if (id) { try { start(id, true); } catch (e) { /* not started */ } }
        }
      },
      // adapter-fixture-agent.js: a key, sign-in or disconnect changed this kind's account; its runs are cancelled.
      onAccountChanged: function (kind) { cancelAll(kind); cancelCheckup(kind); },
      // report.get / finding.get overlay (§6.2 read side): finding.research = {verdict, researchedAt}.
      overlay: function (reportId, finding) {
        var it = (R().labels[reportId] || {})[finding.id];
        if (it) finding.research = { verdict: it.verdict, researchedAt: it.researchedAt };
        return finding;
      },
      eligible: function (ruleId) { return !!RESEARCH[ruleId]; },
      labelled: function (reportId, findingId) { return (R().labels[reportId] || {})[findingId] ? 1 : 0; },
      // get_findings (§6.2 rule 7): the applied label of a finding, or null.
      itemFor: function (reportId, findingId) { var it = (R().labels[reportId] || {})[findingId]; return it ? clone(it) : null; },
      // The Activity row's label for a web_lookup ("Steam · open port").
      subjectLabel: function (finding) {
        var rep = finding && ctx.state().reports[(Object.keys(ctx.state().reports).filter(function (id) {
          return ctx.state().reports[id].findings.some(function (x) { return x.id === finding.id; });
        })[0])];
        var s = rep ? subjectOf(rep, finding) : null;
        if (!s) return null;
        var a = ANSWERS[s.name.toLowerCase()];
        return a ? a.label : s.name.replace(/\.exe$/i, '') + (s.kind === 'port' ? ' · open port' : '');
      },
      lookup: lookup,
      // A reload is a restart: an interrupted run is cancelled, never resumed.
      __restart: function () {
        var S = ctx.state(); if (!S || !S.research) return;
        Object.keys(S.research.runs || {}).forEach(function (id) { var r = S.research.runs[id]; if (ACTIVE[r.status]) { r.status = 'cancelled'; r.trial = false; } });
        var ck = S.research.checkup;
        if (ck && ck.run && ck.run.status === 'running') { ck.run.status = 'cancelled'; ck.status = 'idle'; }
      }
    };
  }

  createFixtureResearch.cleanUrl = cleanUrl;
  createFixtureResearch.cleanText = cleanText;
  createFixtureResearch.applyRules = applyRules;
  createFixtureResearch.RESEARCH = RESEARCH;
  createFixtureResearch.buildSummary = buildSummary;
  createFixtureResearch.higherVersion = higherVersion;
  createFixtureResearch.CHECKUP_PC = CHECKUP_PC;
  createFixtureResearch.CHECKUP_LIMITS = CK;
  createFixtureResearch.CHECKUP_AREAS = AREAS;
  if (root) { root.OptTool = root.OptTool || {}; root.OptTool.createFixtureResearch = createFixtureResearch; }
  if (typeof module !== 'undefined' && module.exports) module.exports = { createFixtureResearch: createFixtureResearch };
})(typeof window !== 'undefined' ? window : globalThis);
