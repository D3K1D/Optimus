// node --test ui/tests/   — M9 §6.8/§6.10 (fixture): the broad check-up over the inventory. The PC summary (names,
// publishers and versions only; hostile inventory values never leave), ordering, dedupe and the 100-entry cap, the five
// calls and the caps, the host rules on results, cards through the AI-card path (replacement, the taint mark), the area
// cache and the weekly gate with the clock hook, the permission gate, cancel on a permissions save, the research.get /
// research.state shapes, and the pure helpers the UI uses (chip, card props, state merge).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { createFixtureAdapter } = require('../js/adapter-fixture.js');
const { createFixtureResearch } = require('../js/adapter-fixture-research.js');
const { toolbarChip, newCheckupIds, checkupRow, checkupOf, researchFromState } = require('../js/connection-state.js');
const fixture = JSON.parse(readFileSync(new URL('../fixtures/m1-simulated-audit.json', import.meta.url), 'utf8'));
const extras = JSON.parse(readFileSync(new URL('../fixtures/m2-extras.json', import.meta.url), 'utf8'));

function memStore() { const m = {}; return { getItem: (k) => (k in m ? m[k] : null), setItem: (k, v) => { m[k] = String(v); } }; }
function adapter() { return createFixtureAdapter({ fixture, extras, storage: memStore(), timeScale: 0.01 }); }
function waitFor(be, pred) { return new Promise((r) => { const off = be.onEvent((e) => { if (pred(e)) { off(); r(e); } }); }); }
function collect(be, pred) { const list = []; be.onEvent((e) => { if (pred(e)) list.push(e); }); return list; }
const key = () => crypto.randomUUID();
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const isCk = (e) => e.type === 'research.state' && e.payload.phase === 'checkup';
const ckEnded = (e) => isCk(e) && e.payload.status !== 'running';
const keys = (o) => Object.keys(o).sort();

async function setWeb(be, web) {
  const cur = (await be.call('ai.getPermissions')).permissions.revision;
  return be.call('ai.setPermissions', { idempotencyKey: key(), revision: cur,
    permissions: { see: { specs: true, history: true, names: false }, do: { checks: true, changes: 'ask', scripts: false, web, admin: false } } });
}
async function onboard(be) {
  const o = await be.call('onboarding.get');
  await be.call('onboarding.save', { expectedRevision: o.revision, patch: { uses: ['Gaming'], inventoryConsent: 'skipped', step: 'complete', provider: { value: 'api', label: 'Claude (API)' } } });
  await be.call('shell.requestApiKey', { kind: 'anthropic_api' });
}
async function scan(be) {
  const fin = waitFor(be, (e) => e.type === 'scan.finished');
  await be.call('scan.start', { idempotencyKey: key(), intent: 'audit.full', text: 'Begin scan' });
  return (await fin).payload.reportId;
}
// Web on, a scan, the labels and then the check-up to its end.
async function checked(be, hooks) {
  await onboard(be);
  await setWeb(be, true);
  if (hooks) await be.call('fixture.setCheckup', hooks);
  const end = waitFor(be, ckEnded);
  const reportId = await scan(be);
  return { reportId, end: await end };
}

// The hostile values in the default fixture inventory (CHECKUP_PC) and in HOSTILE below: none may leave.
const LEAKS = ['alex', 'DESKTOP-7Q2M4KD', 'C:\\', 'D:\\', '\\', 'S5GXNX0T123456A', 'NWN7-0042', 'FBK650', 'WTV27', '24A1B7', 'VEN_10DE', '&DEV_', 'SUBSYS', '2B8260C8',
  '3C-7C-3F', '3C:7C:3F', 'DriverStore', 'cgxkmd', 'launcher.exe', 'Program Files', 'AppData', 'tool.exe', 'rebooted', '10.0.0.5', 'inv-'];
const leaked = (text) => LEAKS.filter((bad) => text.includes(bad));

const HOSTILE = {
  identity: { user: 'alex', pc: 'DESKTOP-7Q2M4KD' },
  inventory: [
    { id: 'h1', area: 'hardware', name: 'Northwind N7 8-core processor', publisher: 'Northwind', serial: 'NWN7-0042-99812-XQ' },
    { id: 'h2', area: 'hardware', name: 'S5GXNX0T123456A', publisher: 'Adatum', version: '1.0' },                      // a serial where a model should be
    { id: 'h3', area: 'hardware', name: 'Adatum Swift 1 TB SSD', publisher: 'Adatum', version: '5B2QGXA7', serial: 'S5GXNX0T123456A' },
    { id: 'h4', area: 'hardware', name: 'Wingtip Ethernet 3C:7C:3F:1A:2B:4D', publisher: 'Wingtip Toys' },            // a MAC address in a name
    { id: 'h5', area: 'hardware', name: 'Wingtip Wi-Fi 7 adapter', publisher: 'Wingtip Toys', mac: '3C-7C-3F-1A-2B-4D', instanceId: 'PCI\\VEN_8086&DEV_272B\\4&2B8260C8&0&00E0' },
    { id: 'h6', area: 'hardware', name: 'Microsoft Wi-Fi Direct Virtual Adapter', publisher: 'Microsoft' },
    { id: 'h7', area: 'hardware', name: 'Contoso GX 4070 graphics card', publisher: 'Contoso' },
    { id: 'd1', area: 'drivers', name: 'PCI\\VEN_10DE&DEV_2786&SUBSYS_40BF1458&REV_A1\\4&2B8260C8&0&0008', publisher: 'Contoso', version: '551.23' },
    { id: 'd2', area: 'drivers', name: 'Generic volume', publisher: 'Microsoft', version: '10.0.26100.1' },
    { id: 'd3', area: 'drivers', name: 'Contoso GX 4070 graphics card driver', publisher: 'Contoso', version: 'C:\\temp', path: 'C:\\Windows\\System32\\drivers\\cgx.sys' },
    { id: 'd4', area: 'drivers', name: 'Generic Volume', publisher: 'MICROSOFT', version: '10.0.26100.2' },             // a duplicate: merged
    { id: 'd5', area: 'drivers', name: 'Device of alex', publisher: 'Contoso', version: '2.0' },
    { id: 'd6', area: 'drivers', name: 'Tailspin audio device driver', publisher: 'DESKTOP-7Q2M4KD', version: '6.0.9549.1' },
    { id: 'w1', area: 'windows', name: 'Windows 11 Pro', publisher: 'Microsoft', version: '24H2 build 26100.2605' },
    { id: 'p1', area: 'programs', name: 'Microsoft Edge', publisher: 'Microsoft Corporation', version: '131.0.2903.70' },
    { id: 'p2', area: 'programs', name: 'Litware VPN', publisher: 'Litware', version: '3.2', path: 'C:\\Program Files\\Litware\\VPN' },
    { id: 'p3', area: 'programs', name: 'https://evil.example.com/payload', version: '1' },
    { id: 'p4', area: 'programs', name: 'alex@example.com', version: '1' },
    { id: 'p5', area: 'programs', name: 'Tool {6F9619FF-8B86-D011-B42D-00C04FC964FF}', version: '1' },
    { id: 'p6', area: 'programs', name: 'Share on 192.168.1.20', version: '1' },
    { id: 'p7', area: 'programs', name: 'User S-1-5-21-3623811015-3361044348-30300820-1013', version: '1' },
    { id: 'p8', area: 'programs', name: 'x'.repeat(65), version: '1' },
    { id: 'p9', area: 'programs', name: 'C:\\Users\\alex\\AppData\\Local\\evil', version: '1' },
    { id: 'p10', area: 'programs', name: 'litware vpn', publisher: 'LITWARE' },                                       // a duplicate: merged
    { id: 'b1', area: 'background', name: 'Windows Update', publisher: 'Microsoft Corporation', version: '10.0.26100.2605' },
    { id: 'b2', area: 'background', name: 'Wingtip Game Launcher', publisher: 'Wingtip Toys', version: '8.1.4', path: '"D:\\Games\\Wingtip\\launcher.exe" --user alex' },
    { id: 'b3', area: 'background', name: 'DESKTOP-7Q2M4KD remote agent', publisher: 'Litware', version: '1.0' },
    { id: 'b4', area: 'background', name: 'Tailspin Backup Service', publisher: 'Tailspin Toys', version: '2.0', account: '.\\alex' },
    { id: 'x1', area: 'nowhere', name: 'Not an area' },
    null
  ],
  settings: { uac: true, secureBoot: false, defenderRealTime: true, defenderDefinitions: '1.421.1260.0', powerMode: 'Balanced', proxy: 'http://10.0.0.5:8080' },
  events: [
    { provider: 'Microsoft-Windows-Kernel-Power', level: 'critical', id: 41, count: 3, message: 'The system has rebooted without cleanly shutting down first. DESKTOP-7Q2M4KD\\alex' },
    { provider: 'Disk', level: 'error', id: 7, count: 9, message: 'more event text' },
    { provider: 'DESKTOP-7Q2M4KD', level: 'error', id: 7, count: 1, message: 'secret event text' },
    { provider: 'Disk', level: 'warning', id: 51, count: 2 },
    { provider: 'Disk', level: 'error', id: 'not an id', count: 2 }
  ]
};
const AREAS = ['hardware', 'drivers', 'windows', 'programs', 'background'];
const SENT_KEYS = ['id', 'area', 'name', 'publisher', 'version'];
const allOf = (s) => [].concat(...AREAS.map((a) => s.entries[a]));
const show = (e) => e.name + (e.publisher ? ' / ' + e.publisher : '') + (e.version ? ' ' + e.version : '');

test('check-up summary: names, publishers and versions only (§6.10); serials, instance ids, MACs, paths, the user and PC name, addresses and event text never leave', () => {
  const s = createFixtureResearch.buildSummary(HOSTILE, ['The public network firewall is off', 'An app accepts connections on all networks']);
  assert.deepEqual(Object.keys(s.entries), AREAS);
  const all = allOf(s);
  for (const e of all) {
    assert.ok(Object.keys(e).every((k) => SENT_KEYS.includes(k)), JSON.stringify(e));
    assert.deepEqual(Object.keys(e), SENT_KEYS.filter((k) => k in e), 'key order ' + JSON.stringify(e));
    assert.match(e.id, /^i\d+$/);
    assert.ok(e.name.length <= 64);
  }
  const text = JSON.stringify(s);
  assert.deepEqual(leaked(text), []);
  for (const bad of ['evil', '@', '192.168', 'S-1-5-21', '6F9619FF', 'secret', 'event text', 'xxxxxxxxxx', 'warning', 'Not an area', 'serial', 'instanceId', 'mac', 'path', 'account'])
    assert.ok(!text.includes(bad), 'leaked: ' + bad);
  // Third-party first, Microsoft's own last; hostile names dropped whole; a hostile publisher or version drops that field.
  assert.deepEqual(s.entries.hardware.map(show), ['Northwind N7 8-core processor / Northwind', 'Adatum Swift 1 TB SSD / Adatum 5B2QGXA7',
    'Wingtip Wi-Fi 7 adapter / Wingtip Toys', 'Contoso GX 4070 graphics card / Contoso', 'Microsoft Wi-Fi Direct Virtual Adapter / Microsoft']);
  assert.deepEqual(s.entries.drivers.map(show), ['Contoso GX 4070 graphics card driver / Contoso', 'Tailspin audio device driver 6.0.9549.1', 'Generic volume / Microsoft 10.0.26100.2']);
  assert.deepEqual(s.entries.windows.map(show), ['Windows 11 Pro / Microsoft 24H2 build 26100.2605', 'The public network firewall is off', 'An app accepts connections on all networks',
    'User Account Control on', 'Secure Boot off', 'Microsoft Defender real-time protection on', 'Microsoft Defender definitions 1.421.1260.0', 'Power mode Balanced',
    'Disk event 7, 9 times', 'Microsoft-Windows-Kernel-Power event 41, 3 times']);
  // Duplicates merged by name + publisher ignoring case into the first, with the highest version (the host's Summary).
  assert.deepEqual(s.entries.programs.map(show), ['Litware VPN / Litware 3.2', 'Microsoft Edge / Microsoft Corporation 131.0.2903.70']);
  assert.deepEqual(s.entries.background.map(show), ['Wingtip Game Launcher / Wingtip Toys 8.1.4', 'Tailspin Backup Service / Tailspin Toys 2.0',
    'Windows Update / Microsoft Corporation 10.0.26100.2605']);
  // Ids run across the five areas in order.
  assert.deepEqual(all.map((e) => e.id), all.map((e, i) => 'i' + (i + 1)));
  assert.deepEqual(s.unsent, { hardware: 0, drivers: 0, windows: 0, programs: 0, background: 0 });
});

test('check-up summary: the default fixture inventory holds the hostile values, and none of them is projected', () => {
  const pc = createFixtureResearch.CHECKUP_PC;
  const raw = JSON.stringify(pc);
  // The fixture really carries them (as names and as fields), so the projection is tested on them.
  for (const bad of ['S5GXNX0T123456A', 'PCI\\\\VEN_10DE', '3C-7C-3F-1A-2B-4D', 'C:\\\\Program Files', 'alex', 'DESKTOP-7Q2M4KD']) assert.ok(raw.includes(bad), 'fixture lacks ' + bad);
  assert.ok(pc.inventory.some((x) => x.name === 'S5GXNX0T123456A') && pc.inventory.some((x) => x.serial) && pc.inventory.some((x) => x.instanceId) &&
    pc.inventory.some((x) => x.mac) && pc.inventory.some((x) => /^PCI\\/.test(x.name)) && pc.inventory.some((x) => /3C-7C-3F/.test(x.name)) && pc.inventory.some((x) => x.path));
  for (const x of pc.inventory) assert.ok(AREAS.includes(x.area) && typeof x.id === 'string' && typeof x.name === 'string', JSON.stringify(x));
  const s = createFixtureResearch.buildSummary(pc, []);
  assert.deepEqual(leaked(JSON.stringify(s)), []);
  for (const e of allOf(s)) assert.ok(Object.keys(e).every((k) => SENT_KEYS.includes(k)), JSON.stringify(e));
  // > 100 driver candidates: third-party first, then Microsoft's in-box ones up to the cap; the rest counted.
  const drv = s.entries.drivers, firstMs = drv.findIndex((e) => /^Microsoft/.test(e.publisher || ''));
  assert.equal(drv.length, 100);
  assert.ok(s.unsent.drivers > 0);
  assert.ok(firstMs > 0 && drv.slice(firstMs).every((e) => e.publisher === 'Microsoft') && drv.slice(0, firstMs).every((e) => e.publisher !== 'Microsoft'));
  assert.equal(drv.filter((e) => e.name.toLowerCase() === 'generic software device').length, 1, 'repeats merged');
  // The two identical RAM sticks are one entry; the programs listed twice are one.
  assert.equal(s.entries.hardware.filter((e) => /DDR5/.test(e.name)).length, 1);
  assert.equal(s.entries.programs.filter((e) => e.name.toLowerCase() === 'litware vpn').length, 1);
  assert.equal(s.entries.windows[0].name, 'Windows 11 Pro');
});

test('check-up: an empty summary creates no run', async () => {
  const be = adapter();
  const ck = collect(be, isCk);
  await onboard(be);
  await setWeb(be, true);
  await be.call('fixture.setCheckup', { pc: {}, phrases: false });
  const labels = waitFor(be, (e) => e.type === 'research.state' && e.payload.phase === 'labels' && e.payload.status !== 'queued' && e.payload.status !== 'running');
  const reportId = await scan(be);
  await labels;
  await sleep(300);
  assert.equal(ck.length, 0, 'no check-up run, so no check-up events');
  const g = await be.call('fixture.getCheckup');
  assert.equal(g.calls.length, 0);
  const r = await be.call('research.get', { reportId });
  assert.notEqual(r.checkup.status, 'running');
  assert.deepEqual(r.checkup.cards, []);
});

test('check-up summary: at most 100 entries per area, Microsoft’s own last; duplicates merged first; the rest are counted, not sent', () => {
  const ms = Array.from({ length: 60 }, (_, i) => ({ id: 'm' + i, area: 'programs', name: 'Windows component ' + i, publisher: 'Microsoft Corporation', version: '10.0.' + i }));
  const third = Array.from({ length: 70 }, (_, i) => ({ id: 't' + i, area: 'programs', name: 'App number ' + i, publisher: 'Adatum', version: '1.' + i }));
  const dups = [{ id: 'd1', area: 'programs', name: 'APP NUMBER 3', publisher: 'adatum', version: '9.9' }, { id: 'd2', area: 'programs', name: 'App number 4', publisher: 'Litware' }];
  const s = createFixtureResearch.buildSummary({ inventory: ms.concat(third, dups) }, []);
  const p = s.entries.programs;
  assert.equal(p.length, 100);
  assert.equal(s.unsent.programs, 60 + 70 + 1 - 100);       // "App number 4" by Litware is a different item; "APP NUMBER 3" by adatum is not
  assert.ok(p.slice(0, 71).every((e) => e.publisher !== 'Microsoft Corporation'), 'third-party first');
  assert.ok(p.slice(71).every((e) => e.publisher === 'Microsoft Corporation'));
  const app3 = p.find((e) => e.name === 'App number 3');
  assert.equal(app3.version, '9.9', 'the first of a duplicate is kept, with the highest version');
  assert.equal(s.counts[app3.id], 2, 'and a count (kept in the fixture, never sent)');
  assert.equal(s.counts[p.find((e) => e.name === 'App number 4').id], 1);
  assert.ok(!('count' in app3));
  assert.deepEqual([s.entries.hardware.length, s.unsent.hardware, s.entries.drivers.length], [0, 0, 0]);
  // A merged duplicate fills a missing version from a later record.
  const m = createFixtureResearch.buildSummary({ inventory: [{ id: 'a', area: 'drivers', name: 'Tailspin audio device driver', publisher: 'Tailspin Toys' },
    { id: 'b', area: 'drivers', name: 'Tailspin Audio Device Driver', publisher: 'TAILSPIN TOYS', version: '6.0.1' }] }, []);
  assert.deepEqual(m.entries.drivers, [{ id: 'i1', area: 'drivers', name: 'Tailspin audio device driver', publisher: 'Tailspin Toys', version: '6.0.1' }]);
});

test('check-up summary: duplicates keep the highest version, as the host’s CheckupPolicy.Higher; the count stays in the fixture', () => {
  const H = createFixtureResearch.higherVersion;
  // Numeric parts compare as numbers, in order; text parts ignoring case; a missing version loses; a tie keeps the first.
  assert.equal(H('10.0.26100.9', '10.0.26100.10'), '10.0.26100.10');
  assert.equal(H('2.10', '2.9'), '2.10');
  assert.equal(H('1.0', '1.0.1'), '1.0.1');
  assert.equal(H('1.0b', '1.0A'), '1.0b');
  assert.equal(H('24H2', '23H2'), '24H2');
  assert.equal(H(null, '3.1'), '3.1');
  assert.equal(H('3.1', null), '3.1');
  assert.equal(H('1.2', '1-2'), '1.2');
  assert.equal(H('007', '7'), '007');
  const inv = ['6.0.2', '6.0.10', '6.0.9'].map((v, i) => ({ id: 'x' + i, area: 'drivers', name: 'Tailspin audio device driver', publisher: i ? 'TAILSPIN TOYS' : 'Tailspin Toys', version: v }));
  const s = createFixtureResearch.buildSummary({ inventory: inv.concat({ id: 'y', area: 'drivers', name: 'Tailspin audio device driver', publisher: 'Contoso', version: '1.0' }) }, []);
  assert.deepEqual(s.entries.drivers, [{ id: 'i1', area: 'drivers', name: 'Tailspin audio device driver', publisher: 'Tailspin Toys', version: '6.0.10' },
    { id: 'i2', area: 'drivers', name: 'Tailspin audio device driver', publisher: 'Contoso', version: '1.0' }]);
  assert.deepEqual(s.counts, { i1: 3, i2: 1 });
});

test('check-up: after the labels, five calls (one per area); caps per area and in all; host rules drop bad results; cards after the report’s', async () => {
  const be = adapter();
  const states = collect(be, (e) => e.type === 'research.state');
  const { reportId, end } = await checked(be);
  assert.equal(end.payload.status, 'done');
  assert.equal(end.stream, 'user');
  // The labels phase ends before the check-up starts; every event names its phase.
  const phases = states.map((e) => e.payload.phase + ':' + e.payload.status);
  const firstCk = phases.findIndex((p) => p.startsWith('checkup'));
  assert.equal(phases[firstCk - 1], 'labels:done');
  assert.deepEqual(phases.slice(firstCk), ['checkup:running', 'checkup:running', 'checkup:running', 'checkup:running', 'checkup:running', 'checkup:running', 'checkup:done']);
  const ck = states.filter(isCk);
  assert.deepEqual(ck.map((e) => e.payload.done + '/' + e.payload.total), ['0/5', '1/5', '2/5', '3/5', '4/5', '5/5', '5/5']);
  assert.ok(ck.every((e) => /^research-[0-9a-f]{16}$/.test(e.payload.runId) && e.payload.runId === end.payload.runId && e.payload.trial === false));
  assert.deepEqual(keys(end.payload), ['done', 'phase', 'reportId', 'runId', 'status', 'total', 'trial']);

  const g = await be.call('fixture.getCheckup');
  assert.deepEqual(g.calls.map((c) => c.area), AREAS);
  for (const c of g.calls) {
    assert.equal(c.prompt, 'checkup-v2');   // the host's CheckupPolicy.PromptVersion
    assert.ok(c.entries.length > 0 && c.entries.length <= 100);
    for (const e of c.entries) { assert.equal(e.area, c.area); assert.ok(Object.keys(e).every((k) => SENT_KEYS.includes(k)), JSON.stringify(e)); }
  }
  // The drivers area has more than 100 candidates: 100 sent, the rest counted.
  const drv = g.calls.find((c) => c.area === 'drivers');
  assert.equal(drv.entries.length, 100);
  assert.ok(drv.unsent > 0);
  assert.equal(g.calls.find((c) => c.area === 'hardware').unsent, 0);
  // None of the inventory's hostile values (serials, instance ids, MACs, paths, the user and PC name) was sent.
  const sent = JSON.stringify(g.calls);
  assert.deepEqual(leaked(sent), []);
  for (const bad of ['.\\', 'serial', 'instanceId', '"mac"', '"path"', 'account']) assert.ok(!sent.includes(bad), 'leaked: ' + bad);
  // The windows call carries the edition and build, the report's finding titles and the event-log count from the report's own evidence.
  const win = g.calls.find((c) => c.area === 'windows');
  assert.deepEqual(win.entries[0], { id: win.entries[0].id, area: 'windows', name: 'Windows 11 Pro', publisher: 'Microsoft', version: '24H2 build 26100.2605' });
  assert.ok(win.entries.some((e) => e.name === 'The public network firewall is off'));   // the rule's generic phrase, not the finding's title
  assert.ok(win.entries.some((e) => e.name === 'Microsoft-Windows-Kernel-Power event 41, 3 times'));
  assert.ok(!sent.includes('Public firewall profile is off'), 'never a finding title');
  // Ids run i1… across the calls in order.
  const ids = [].concat(...g.calls.map((c) => c.entries.map((e) => e.id)));
  assert.deepEqual(ids, ids.map((_, i) => 'i' + (i + 1)));
  // Usage on the run: searches capped at 6 per call for an API kind.
  assert.equal(g.usage.searches, 4 + 3 + 5 + 6 + 2);
  assert.ok(g.usage.inputTokens > 0 && g.usage.outputTokens > 0);

  const r = await be.call('research.get', { reportId });
  assert.deepEqual(keys(r.checkup), ['cards', 'lastAt', 'missed', 'status']);
  assert.equal(r.checkup.status, 'done');
  assert.ok(Date.parse(r.checkup.lastAt));
  // ≤ 3 per area (windows 3 of 4) and ≤ 8 in all (hardware 2, drivers 2, windows 3, programs 1 of 2, background 0 of 1).
  assert.equal(r.checkup.cards.length, 8);
  assert.deepEqual(leaked(JSON.stringify(r)), []);
  const fu = await be.call('followup.get', { reportId });
  const rep = await be.call('report.get', { reportId });
  assert.equal(fu.total, rep.findings.length + 8 - fu.decisions.length);
  // The check-up cards come after the report's own cards: walk the deck with Later until they come up.
  const seen = [];
  let cur = fu;
  for (let i = 0; i < 40 && cur.card; i++) {
    seen.push(cur.card);
    cur = await be.call('followup.respond', { idempotencyKey: key(), reportId, expectedRevision: cur.revision, scope: 'card', cardId: cur.card.id, option: 'later' });
  }
  const firstCheckup = seen.findIndex((c) => c.checkup);
  assert.ok(firstCheckup > 0 && seen.slice(firstCheckup).every((c) => c.checkup) && seen.slice(0, firstCheckup).every((c) => !c.checkup));
  const cards = seen.filter((c) => c.checkup);
  assert.deepEqual(cards.map((c) => c.id), r.checkup.cards);
  const titles = cards.map((c) => c.title);
  assert.ok(!titles.includes('A problem with something that was never sent'), '`about` outside the sent entries is dropped');
  assert.ok(!titles.includes('A source the search never returned'), 'no observed source, no card');
  assert.ok(!titles.includes('A fourth result for Windows'), 'the 4th result of an area is over the cap');
  assert.ok(!titles.includes('This PDF reader version has a reported security flaw'), 'over 8 in all');
  assert.ok(!titles.includes('Backups can stop silently after a Windows update'), 'over 8 in all');
  assert.deepEqual(cards.map((c) => c.checkup.kind), ['update', 'update', 'bug', 'update', 'security', 'bug', 'conflict', 'end_of_life']);
  assert.deepEqual(leaked(JSON.stringify(cards)), []);
  for (const c of cards) {
    // The AI-card path: origin ai, no finding, no action or preview; Ask about this / Ignore (the app shows Hide).
    assert.equal(c.origin, 'ai');
    assert.deepEqual([c.findingId, c.actionId, c.preview, c.guide, c.sourceTaskId], [null, null, null, null, null]);
    assert.deepEqual(c.options, ['Ask about this', 'Ignore']);
    assert.deepEqual(keys(c.checkup), ['checkedAt', 'kind', 'sources']);
    assert.ok(c.title.length <= 100 && c.text.length <= 400 && c.text.length > 0);
    assert.ok(c.checkup.sources.length >= 1 && c.checkup.sources.length <= 3);
    for (const s of c.checkup.sources) { assert.deepEqual(keys(s), ['domain', 'sourceId', 'title']); assert.match(s.sourceId, /^src-[0-9a-f]{16}$/); }
    // The internal marks never leave the card view.
    for (const k of ['aiTainted', 'aiTaskId', 'checkupKey', 'noteTainted']) assert.ok(!(k in c), k);
    assert.ok(['security', 'diagnostics'].includes(c.category));
  }
  const gpu = cards[2];
  assert.ok(gpu.title.endsWith('…') && gpu.title.length <= 100);          // capped on a word
  assert.equal(gpu.checkup.sources.length, 3);
  assert.equal(gpu.checkup.sources[0].domain, 'support.contoso.com');
  assert.equal(cards.find((c) => c.checkup.kind === 'end_of_life').category, 'security');
  // A source opens by id through the shell.
  assert.equal((await be.call('shell.openSource', { sourceId: gpu.checkup.sources[0].sourceId })).status, 'opened');
  // Observed sources made the provider's web "yes".
  assert.equal((await be.call('ai.getPermissions')).available.web, 'yes');
});

test('check-up: its cards are web-tainted, so a task that reads them is marked (and cannot propose a script)', async () => {
  const be = adapter();
  const { reportId } = await checked(be);
  const t = await be.call('agent.start', { idempotencyKey: key(), goal: 'Read', recipientKind: 'anthropic_api', scope: { reportId } });
  assert.equal(t.webTainted, false);
  const done = waitFor(be, (e) => e.stream === 'task:' + t.taskId && (e.type === 'agent.message' || (e.type === 'agent.turn' && e.payload.status !== 'started')));
  await be.call('agent.ask', { idempotencyKey: key(), taskId: t.taskId, text: 'Look over my latest report and improve the suggestion cards where you can.' });
  await done;
  await sleep(30);
  assert.equal((await be.call('agent.get', { taskId: t.taskId })).webTainted, true);
});

test('check-up: the same result again replaces its card (about + kind); nothing is added', async () => {
  const be = adapter();
  const { reportId } = await checked(be);
  const before = await be.call('research.get', { reportId });
  const fu1 = await be.call('followup.get', { reportId });
  // A week later the hourly check runs again and finds the same problems.
  const end = waitFor(be, ckEnded);
  const tick = await be.call('fixture.checkupTick', { days: 8 });
  assert.equal(tick.started, true);
  assert.equal((await end).payload.status, 'done');
  const after = await be.call('research.get', { reportId });
  assert.deepEqual(after.checkup.cards, before.checkup.cards);
  const fu2 = await be.call('followup.get', { reportId });
  assert.equal(fu2.total, fu1.total);
  assert.ok(fu2.revision > fu1.revision);
  assert.notEqual(after.checkup.lastAt, before.checkup.lastAt);
  assert.ok(Date.parse(after.checkup.lastAt) - Date.parse(before.checkup.lastAt) >= 8 * 864e5 - 60e3);
});

test('check-up: a replaced card takes the new wording and sources', async () => {
  const be = adapter();
  const { reportId } = await checked(be, { answers: { programs: { searches: 1, observed: [{ title: 'Lifecycle', url: 'https://www.litwareinc.com/support/lifecycle' }],
    items: [{ about: { name: 'Litware VPN' }, kind: 'end_of_life', title: 'Old wording', what: 'Old text.', sources: ['https://www.litwareinc.com/support/lifecycle'] }] } } });
  const ids = (await be.call('research.get', { reportId })).checkup.cards;
  assert.equal(ids.length, 1);
  await be.call('fixture.setCheckup', { answers: { programs: { searches: 1, observed: [{ title: 'Litware VPN 3.x end of support', url: 'https://www.litwareinc.com/support/vpn-3' }],
    items: [{ about: { name: 'Litware VPN' }, kind: 'end_of_life', title: 'New wording', what: 'New text.', sources: ['https://www.litwareinc.com/support/vpn-3'] }] } } });
  const end = waitFor(be, ckEnded);
  await be.call('fixture.checkupTick', { days: 7 });
  await end;
  const r = await be.call('research.get', { reportId });
  assert.deepEqual(r.checkup.cards, ids);
  let cur = await be.call('followup.get', { reportId }), card = null;
  for (let i = 0; i < 40 && cur.card && !card; i++) {
    if (cur.card.checkup) card = cur.card;
    else cur = await be.call('followup.respond', { idempotencyKey: key(), reportId, expectedRevision: cur.revision, scope: 'card', cardId: cur.card.id, option: 'later' });
  }
  assert.deepEqual([card.id, card.title, card.text, card.checkup.sources[0].domain, card.checkup.sources[0].title], [ids[0], 'New wording', 'New text.', 'www.litwareinc.com', 'Litware VPN 3.x end of support']);
});

test('check-up: the area cache skips unchanged areas within 7 days; a change on the PC checks that area only', async () => {
  const be = adapter();
  await checked(be);
  // A second scan the same week: the labels run again, the check-up costs nothing.
  const end = waitFor(be, ckEnded);
  const second = await scan(be);
  const e2 = await end;
  assert.equal(e2.payload.status, 'done');
  assert.deepEqual([e2.payload.done, e2.payload.total], [0, 0]);
  assert.deepEqual((await be.call('fixture.getCheckup')).calls, []);
  // The second report has its own deck: no check-up cards were made for it (nothing was checked).
  assert.deepEqual((await be.call('research.get', { reportId: second })).checkup.cards, []);
  // A program's version changed in the inventory: only that area is checked again.
  const pc = JSON.parse(JSON.stringify(createFixtureResearch.CHECKUP_PC));
  pc.inventory.find((x) => x.area === 'programs' && x.name === 'Litware VPN').version = '3.3';
  await be.call('fixture.setCheckup', { pc });
  const end3 = waitFor(be, ckEnded);
  await scan(be);
  assert.equal((await end3).payload.total, 1);
  assert.deepEqual((await be.call('fixture.getCheckup')).calls.map((c) => c.area), ['programs']);
});

test('check-up: the weekly gate between scans (clock hook): not before 7 days, then once', async () => {
  const be = adapter();
  await checked(be);
  const states = collect(be, isCk);
  let t = await be.call('fixture.checkupTick', { days: 1 });
  assert.equal(t.started, false);
  t = await be.call('fixture.checkupTick', { days: 5 });
  assert.equal(t.started, false);
  assert.equal(states.length, 0);
  const end = waitFor(be, ckEnded);
  t = await be.call('fixture.checkupTick', { days: 1 });     // 7 days since the last one
  assert.equal(t.started, true);
  assert.equal(t.checkup.status, 'running');
  await end;
  assert.equal((await be.call('fixture.getCheckup')).calls.length, 5);   // the area cache expired with it
  // Right after, the next hourly check does nothing.
  assert.equal((await be.call('fixture.checkupTick', {})).started, false);
  await assert.rejects(be.call('fixture.checkupTick', { days: -1 }), (e) => e.code === -32602 && e.data.field === 'days');
});

test('check-up: web off → status off, no calls, no cards; the weekly check does nothing', async () => {
  const be = adapter();
  await onboard(be);
  const states = collect(be, (e) => e.type === 'research.state');
  const reportId = await scan(be);
  await sleep(150);
  assert.equal(states.length, 0);
  const r = await be.call('research.get', { reportId });
  assert.deepEqual(r.checkup, { status: 'off', lastAt: null, cards: [], missed: [] });
  assert.equal((await be.call('fixture.checkupTick', { days: 30 })).started, false);
  assert.deepEqual((await be.call('fixture.getCheckup')).calls, []);
  await assert.rejects(be.call('research.start', { idempotencyKey: key(), reportId }), (e) => e.code === -32013 && e.data.reason === 'web_off');
});

test('check-up: a permissions save cancels a running check-up; with web still on it runs again (cached areas skipped)', async () => {
  const be = adapter();
  await onboard(be);
  await setWeb(be, true);
  const reportId = await scan(be);
  // Wait for the second area to be in flight.
  await waitFor(be, (e) => isCk(e) && e.payload.status === 'running' && e.payload.done === 1);
  const cancelled = waitFor(be, (e) => isCk(e) && e.payload.status === 'cancelled');
  const restarted = waitFor(be, (e) => isCk(e) && e.payload.status === 'running' && e.payload.done === 0);
  const cur = (await be.call('ai.getPermissions')).permissions.revision;
  await be.call('ai.setPermissions', { idempotencyKey: key(), revision: cur, permissions: { see: { specs: true, history: true, names: true }, do: { checks: true, changes: 'ask', scripts: false, web: true, admin: false } } });
  const c = await cancelled;
  assert.equal(c.payload.reportId, reportId);
  const mid = await be.call('research.get', { reportId });
  assert.notEqual(mid.checkup.status, 'running');
  const again = await restarted;
  assert.notEqual(again.payload.runId, c.payload.runId);
  assert.equal(again.payload.total, 4);      // hardware was checked before the cancel and stays cached
  await waitFor(be, ckEnded);
  // Web off now: a save cancels, nothing restarts, the status is off.
  await be.call('fixture.checkupTick', { days: 8 });
  await waitFor(be, (e) => isCk(e) && e.payload.status === 'running');
  const gone = waitFor(be, (e) => isCk(e) && e.payload.status === 'cancelled');
  await setWeb(be, false);
  await gone;
  await sleep(100);
  assert.equal((await be.call('research.get', { reportId })).checkup.status, 'off');
});

test('check-up: research.cancel covers both phases; research.start runs both', async () => {
  const be = adapter();
  const { reportId } = await checked(be);
  await be.call('fixture.checkupTick', { days: 8 });
  const cancelled = waitFor(be, (e) => isCk(e) && e.payload.status === 'cancelled');
  const v = await be.call('research.cancel', { reportId });
  await cancelled;
  assert.equal(v.checkup.status, 'idle');
  // research.start: the labels, then the check-up (the areas are due again after the clock moved on).
  const end = waitFor(be, ckEnded);
  const s = await be.call('research.start', { idempotencyKey: key(), reportId });
  assert.deepEqual(keys(s.checkup), ['cards', 'lastAt', 'missed', 'status']);
  assert.equal((await end).payload.status, 'done');
});

test('check-up outcomes: partial (a failed call, the time budget), failed (every call), unavailable (search refused: web "no")', async () => {
  for (const [hooks, want, done, missed] of [[{ failCalls: 1 }, 'partial', 5, ['hardware']], [{ budgetCalls: 1 }, 'partial', 1, ['drivers', 'windows', 'programs', 'background']],
    [{ failCalls: 'all' }, 'failed', 5, []], [{ outcome: 'unsupported' }, 'unavailable', 0, []]]) {
    const be = adapter();
    const { reportId, end } = await checked(be, hooks);
    assert.equal(end.payload.status, want, JSON.stringify(hooks));
    assert.equal(end.payload.done, done, JSON.stringify(hooks));
    assert.equal(end.payload.total, 5, JSON.stringify(hooks));     // ≤ 5 calls: one per area
    const r = await be.call('research.get', { reportId });
    assert.equal(r.checkup.status, want);
    assert.deepEqual(r.checkup.missed, missed, JSON.stringify(hooks));   // the new area ids
    if (want === 'unavailable') assert.equal((await be.call('ai.getPermissions')).available.web, 'no');
    if (want === 'failed' || want === 'unavailable') { assert.equal(r.checkup.lastAt, null); assert.deepEqual(r.checkup.cards, []); }
    if (want === 'partial') assert.ok(r.checkup.lastAt);
  }
});

test('check-up: searches are capped at 6 per call where the provider can cap them', async () => {
  const be = adapter();
  await checked(be, { searches: 20 });
  assert.equal((await be.call('fixture.getCheckup')).usage.searches, 5 * 6);
});

test('check-up dev hook: each status can be forced; "running" shows in the view and the event', async () => {
  const be = adapter();
  const { reportId } = await checked(be);
  for (const status of ['off', 'idle', 'running', 'done', 'partial', 'failed', 'unavailable']) {
    const ev = waitFor(be, isCk);
    const v = await be.call('fixture.setCheckup', { status });
    assert.equal(v.checkup.status, status);
    assert.equal((await ev).payload.status, ['off', 'idle'].includes(status) ? 'cancelled' : status);
    assert.equal((await be.call('research.get', { reportId })).checkup.status, status);
  }
  const v = await be.call('fixture.setCheckup', { status: null });
  assert.equal(v.checkup.status, 'done');
  await assert.rejects(be.call('fixture.setCheckup', { status: 'nope' }), (e) => e.code === -32602);
});

test('UI helpers: the toolbar chip (one at a time, the designer\'s priority); the card props; a state event merges into the view', () => {
  const ck = (status, more) => Object.assign({ status, lastAt: null, cards: [], missed: [] }, more);
  // Running first: the check-up, then research (n = the subject being worked on), then the first-ever lookups.
  assert.deepEqual(toolbarChip({ status: 'running', done: 2, total: 7, checkup: ck('idle') }), { researching: [3, 7] });
  assert.deepEqual(toolbarChip({ status: 'done', done: 3, total: 3, checkup: ck('running') }, { newCheckups: 2 }), { checking: true });
  assert.deepEqual(toolbarChip({ status: 'running', done: 0, total: 3, checkup: ck('running') }), { checking: true });
  assert.deepEqual(toolbarChip({ status: 'queued', done: 0, total: 3, trial: true, checkup: ck('failed') }, { newCheckups: 1 }), { checking: 'setup' });
  // Then new cards, over any result.
  assert.deepEqual(toolbarChip({ status: 'failed', checkup: ck('failed') }, { newCheckups: 3 }), { newCheckups: 3 });
  // Then the check-up's result: done with no cards is "nothing new found"; done with cards shows none.
  assert.deepEqual(toolbarChip({ status: 'done', checkup: ck('done') }), { checkupStatus: 'none' });
  assert.equal(toolbarChip({ status: 'done', checkup: ck('done', { cards: ['card-1'] }) }), null);
  assert.deepEqual(toolbarChip({ status: 'done', checkup: ck('failed') }), { checkupStatus: 'failed' });
  assert.deepEqual(toolbarChip({ status: 'done', checkup: ck('unavailable') }, { ai: 'ChatGPT' }), { checkupStatus: 'unavailable', ai: 'ChatGPT' });
  // missed: the area keys (design Toolbar v55 words them), in the design's area order; unknown ids are dropped; all five
  // missed reads as the failed check-up.
  assert.deepEqual(toolbarChip({ status: 'done', checkup: ck('partial', { missed: ['windows', 'hardware'] }) }), { checkupStatus: 'partial', missed: ['hardware', 'windows'] });
  assert.deepEqual(toolbarChip({ status: 'done', checkup: ck('partial', { missed: ['drivers', 'programs', 'background'] }) }), { checkupStatus: 'partial', missed: ['drivers', 'programs', 'background'] });
  assert.deepEqual(toolbarChip({ status: 'done', checkup: ck('partial', { missed: ['software', 'C:\Users\alex'] }) }), { checkupStatus: 'partial' });
  assert.deepEqual(toolbarChip({ status: 'done', checkup: ck('partial', { missed: ['hardware', 'drivers', 'windows', 'programs', 'background'] }) }), { checkupStatus: 'failed' });
  assert.deepEqual(toolbarChip({ status: 'done', checkup: ck('partial') }), { checkupStatus: 'partial' });
  // Then research's own end state; quiet otherwise.
  assert.deepEqual(toolbarChip({ status: 'failed', checkup: ck('idle') }), { researchStatus: 'failed' });
  assert.deepEqual(toolbarChip({ status: 'unavailable', checkup: ck('unavailable') }, { ai: 'ChatGPT' }), { checkupStatus: 'unavailable', ai: 'ChatGPT' });
  assert.deepEqual(toolbarChip({ status: 'unavailable', checkup: ck('off') }, { ai: 'ChatGPT' }), { researchStatus: 'unavailable', ai: 'ChatGPT' });
  for (const s of ['off', 'idle']) assert.equal(toolbarChip({ status: 'done', checkup: ck(s) }), null, s);
  assert.equal(toolbarChip(null), null);
  // Before the count is known: "Researching…" (design Toolbar researching: true).
  assert.deepEqual(toolbarChip({ status: 'running', done: 0, total: 0, checkup: ck('idle') }), { researching: true });
  assert.deepEqual(toolbarChip({ status: 'queued', checkup: ck('idle') }), { researching: true });
  // New since last seen.
  assert.deepEqual(newCheckupIds(['card-1', 'card-2', 'card-3'], { 'card-1': true }), ['card-2', 'card-3']);
  assert.deepEqual(newCheckupIds([], null), []);
  // The report's check-up rows.
  const card = { id: 'card-1', origin: 'ai', title: 'T', text: 'What', checkup: { kind: 'bug', checkedAt: '2026-11-12T14:10:00.000Z', sources: [{ sourceId: 'src-1', title: 'A', domain: 'a.com' }, { sourceId: 'src-2', title: 'B', domain: 'b.com' }] } };
  assert.deepEqual(checkupRow(card, 0, () => '14:10'), { id: 'ck-1', kind: 'Bug', title: 'T', text: 'What', sources: [{ sourceId: 'src-1', site: 'a.com' }, { sourceId: 'src-2', site: 'b.com' }], time: '14:10' });
  assert.equal(checkupRow({ id: 'card-2', title: 'x' }, 1, () => ''), null);

  const src = [{ sourceId: 'src-1', title: 'A', domain: 'a.com' }, { sourceId: 'src-2', title: 'B', domain: 'b.com' }, { sourceId: 'src-3', title: 'C', domain: 'c.com' }, { sourceId: 'src-4', title: 'D', domain: 'd.com' }];
  assert.deepEqual(checkupOf({ origin: 'ai', title: 'T', text: 'What', checkup: { kind: 'end_of_life', sources: src } }),
    { kind: 'End of life', title: 'T', text: 'What', sources: src.slice(0, 3) });
  const words = ['bug', 'security', 'end_of_life', 'conflict', 'update'].map((k) => checkupOf({ title: 'T', text: 'T', checkup: { kind: k, sources: [] } }));
  assert.deepEqual(words.map((w) => w.kind), ['Bug', 'Security', 'End of life', 'Conflict', 'Update']);
  assert.equal(words[0].text, null);                         // text equal to the title is not repeated
  assert.equal(checkupOf({ title: 'T', checkup: { kind: 'other', sources: [] } }).kind, undefined);
  assert.equal(checkupOf({ origin: 'ai', title: 'T' }), null);
  assert.equal(checkupOf(null), null);

  const base = { reportId: 'report-1', runId: 'research-a', status: 'done', done: 3, total: 3, trial: false, items: [{ findingId: 'fnd-1' }], checkup: { status: 'idle', lastAt: null, cards: [] } };
  const running = researchFromState(base, { reportId: 'report-1', runId: 'research-b', status: 'running', done: 1, total: 3, phase: 'checkup' });
  assert.deepEqual([running.status, running.runId, running.done, running.checkup.status, running.items.length], ['done', 'research-a', 3, 'running', 1]);
  const ended = researchFromState(running, { reportId: 'report-1', runId: 'research-b', status: 'cancelled', done: 1, total: 3, phase: 'checkup' });
  assert.equal(ended.checkup.status, 'idle');
  const labels = researchFromState(base, { reportId: 'report-1', runId: 'research-c', status: 'running', done: 0, total: 3, trial: true, phase: 'labels' });
  assert.deepEqual([labels.status, labels.runId, labels.trial, labels.checkup.status], ['running', 'research-c', true, 'idle']);
});
