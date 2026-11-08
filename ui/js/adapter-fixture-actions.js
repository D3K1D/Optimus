/* Fixture adapter, M4 part: an offline simulation of plans, approval, execution, receipts and undo
   (contracts/m4-actions.md §4) so the website demo shows the whole change path. Plugged into adapter-fixture.js.

   Beyond the contract (fixture only):
     shell.approvePlan {planId} → {status, operationId?, error?}   stands in for the shell's native approval dialog (same shape as
                                                            the shell, §8): approves, unless fixture.setApproval set another answer.
     fixture.setApproval {status, error?}                   the next approval answers declined / unavailable / error (once).
     fixture.drift {}                                       changes the power plan behind the app's back (undo then refuses).
   Writes nothing anywhere: the "PC" is a few values in the fixture's state. */
(function (root) {
  'use strict';

  var CATALOG = [
    { actionId: 'power.select_scheme', label: 'Change power plan', privilege: 'none', undo: true, enabled: true },
    { actionId: 'firewall.enable_public', label: 'Turn on Public firewall', privilege: 'admin', undo: true, enabled: false, reason: 'handler_off' },
    { actionId: 'defender.update_signatures', label: 'Update definitions', privilege: 'none', undo: false, enabled: true },
    { actionId: 'defender.quick_scan', label: 'Quick scan', privilege: 'none', undo: false, enabled: true },
    { actionId: 'defender.full_scan', label: 'Full scan', privilege: 'none', undo: false, enabled: true },
    { actionId: 'manual.open', label: 'Open settings', privilege: 'none', undo: false, enabled: true }
  ];
  var SCHEMES = { balanced: 'Balanced', high_performance: 'High performance', power_saver: 'Power saver' };

  /** ctx: { state(), emit, later, H, getReport, rpcError, clone, randomHex, save, ruleOf(findingId) } */
  function createFixtureActions(ctx) {
    var E = ctx.rpcError, clone = ctx.clone;
    function X() {
      var S = ctx.state();
      if (!S.actions) S.actions = { pc: { power: 'High performance', signaturesDays: 9, lastQuickScanDays: 6 }, plans: {}, ops: {}, rechecks: {} };
      return S.actions;
    }
    function catalogEntry(id) { return CATALOG.filter(function (c) { return c.actionId === id; })[0] || null; }
    function emitPlan(p) { ctx.emit('user', 'action.plan', { planId: p.planId, status: p.status, requestedBy: clone(p.requestedBy) }); }
    function emitOp(op, progress) {
      var ev = { operationId: op.operationId, planId: op.planId, state: op.state };
      if (op.receipt) ev.receipt = clone(op.receipt);
      if (progress) ev.progress = progress;
      ctx.emit('user', 'operation.state', ev);
    }

    // Builds the plan from fixture "live state" — never from what the caller says about it.
    function resolve(actionId, parameters, findingId) {
      var pc = X().pc;
      if (actionId === 'power.select_scheme') {
        var want = SCHEMES[(parameters && parameters.personality) || 'balanced'] || 'Balanced';
        if (pc.power === want) return { ineligible: { code: 'already_active', message: want + ' is already active' } };
        return { target: { type: 'power_scheme', id: want.toLowerCase().replace(/ /g, '_'), label: want },
          before: { activeScheme: { label: pc.power } }, after: { activeScheme: { label: want } },
          rows: [{ label: 'Active power plan', before: pc.power, after: want }], context: [['Power', 'On battery · 64%']],
          tradeoffs: [want === 'Balanced' ? 'Slightly lower peak speed' : 'Changes speed and battery use'], privilege: 'none',
          undo: { supported: true, conditions: 'Only while ' + want + ' is still active' } };
      }
      if (actionId === 'firewall.enable_public') return { ineligible: { code: 'handler_off', message: 'Change it in Windows Security', manualUri: 'windowsdefender://network' } };
      if (actionId === 'defender.update_signatures') {
        return { target: { type: 'defender', id: 'signatures', label: 'Defender definitions' },
          rows: [{ label: 'Definitions', before: pc.signaturesDays + ' days old', after: 'Latest' }], context: [['Provider', 'Microsoft Defender']],
          tradeoffs: ['Downloads from Microsoft'], privilege: 'none', undo: { supported: false, conditions: null } };
      }
      if (actionId === 'defender.quick_scan' || actionId === 'defender.full_scan') {
        var quick = actionId === 'defender.quick_scan';
        return { target: { type: 'defender', id: quick ? 'quick' : 'full', label: quick ? 'Quick scan' : 'Full scan' },
          rows: [{ label: quick ? 'Quick scan' : 'Full scan', before: 'Last ' + pc.lastQuickScanDays + ' days ago', after: 'Now' }],
          context: [['Provider', 'Microsoft Defender']], tradeoffs: [quick ? 'A few minutes' : 'An hour or more'], privilege: 'none',
          undo: { supported: false, conditions: null } };
      }
      // Not a write handler in M4: guidance through Settings.
      var r = findingId ? ctx.ruleOf(findingId) : null;
      return { ineligible: { code: 'manual_only', message: 'Change it in Settings', manualUri: (r && r.settingsUri) || 'ms-settings:' } };
    }
    function makePlan(p, requestedBy, undoOf) {
      var res = undoOf || resolve(p.actionId, p.parameters, p.findingId);
      var c = catalogEntry(p.actionId);
      if (!c) throw E(-32602, 'invalid params', { field: 'actionId' });
      var plan = { planId: 'plan-' + ctx.randomHex(16), actionId: p.actionId, label: c.label, handlerVersion: '1.0.0', findingId: p.findingId || null, reportId: p.reportId || null,
        target: res.target || null, before: res.before || null, after: res.after || null, rows: res.rows || [], context: res.context || [], tradeoffs: res.tradeoffs || [],
        privilege: res.privilege || c.privilege, undo: res.undo || { supported: false }, requestedBy: requestedBy || { kind: 'user', taskId: null, invocationId: null },
        createdAt: new Date().toISOString(), expiresAt: new Date(Date.now() + 300000).toISOString(),
        status: res.ineligible ? 'ineligible' : 'awaiting_user', ineligible: res.ineligible || null, undoOf: p.undoOf || null };
      return ctx.H(JSON.stringify([plan.actionId, plan.target, plan.before, plan.after, plan.rows])).then(function (h) {
        plan.planHash = h;
        X().plans[plan.planId] = plan;
        emitPlan(plan);
        ctx.save();
        return clone(plan);
      });
    }
    function getPlan(id) { var p = X().plans[id]; if (!p) throw E(-32001, 'not_found', { kind: 'plan', id: id }); return p; }
    function opOf(planId) { var ops = X().ops; for (var k in ops) if (ops[k].planId === planId) return ops[k]; return null; }

    // The native dialog's Approve, simulated: challenge, approve, then the executor journal.
    function approvePlan(p) {
      var plan = getPlan(p.planId);
      // §8: the shell answers with a status, never an exception, for anything the user or the host decided.
      if (plan.status !== 'awaiting_user') return { status: 'stale' };
      if (Date.parse(plan.expiresAt) < Date.now()) { plan.status = 'expired'; emitPlan(plan); return { status: 'expired' }; }
      var forced = X().nextApproval; X().nextApproval = null;
      if (forced && forced.status === 'declined') { plan.status = 'declined'; emitPlan(plan); return { status: 'declined', error: forced.error || null }; }
      if (forced) return { status: forced.status, error: forced.error || null };
      plan.status = 'approved'; emitPlan(plan);
      var op = { operationId: 'op-' + ctx.randomHex(16), planId: plan.planId, actionId: plan.actionId, state: 'prepared', receipt: null, undo: { available: false, reason: 'not_verified' }, error: null };
      X().ops[op.operationId] = op;
      var job = 'op:' + op.operationId, pc = X().pc;
      var scan = /scan$/.test(plan.actionId), update = plan.actionId === 'defender.update_signatures';
      var progress = scan ? 'Scanning · ' + (plan.actionId === 'defender.quick_scan' ? 'quick' : 'full') : update ? 'Updating definitions' : null;
      emitOp(op, progress);
      ctx.later(job, function () { op.state = 'attempted'; plan.status = 'running'; emitPlan(plan); emitOp(op, progress); ctx.save(); }, 300);
      if (scan) ctx.later(job, function () { emitOp(op, progress + ' · 60%'); }, 1600);
      ctx.later(job, function () {
        var text;
        if (plan.actionId === 'power.select_scheme') { pc.power = plan.after.activeScheme.label; text = pc.power + ' is active, read back from Windows.'; }
        else if (update) { pc.signaturesDays = 0; text = 'Definitions are current, read back from Defender.'; }
        else { pc.lastQuickScanDays = 0; text = 'Defender finished the scan: no threats found.'; }
        op.state = 'verified';
        op.receipt = { receiptId: 'rcpt-' + ctx.randomHex(8), verifiedAt: new Date().toISOString(), observed: clone(plan.after), text: text, benefit: 'unmeasured' };
        op.undo = plan.undo && plan.undo.supported ? { available: true, reason: null } : { available: false, reason: 'not_supported' };
        plan.status = 'verified';
        emitPlan(plan); emitOp(op, null); ctx.save();
      }, scan ? 2800 : update ? 1800 : 900);
      return { status: 'approved', operationId: op.operationId };
    }
    function undo(p) {
      var op = X().ops[p.operationId];
      if (!op) throw E(-32001, 'not_found', { kind: 'operation', id: p.operationId });
      var plan = getPlan(op.planId), pc = X().pc;
      if (op.state !== 'verified' || !plan.undo || !plan.undo.supported) throw E(-32013, 'invalid_state', { reason: 'not_undoable' });
      var params = { actionId: plan.actionId, findingId: plan.findingId, undoOf: op.operationId };
      // Undo is a new plan, and only while the target still holds what we wrote.
      if (pc.power !== plan.after.activeScheme.label) {
        return makePlan(params, null, { ineligible: { code: 'changed_since', message: 'Changed since', manualUri: 'ms-settings:powersleep' } });
      }
      return makePlan(params, null, { target: { type: 'power_scheme', id: plan.before.activeScheme.label.toLowerCase().replace(/ /g, '_'), label: plan.before.activeScheme.label },
        before: clone(plan.after), after: clone(plan.before), rows: [{ label: 'Active power plan', before: plan.after.activeScheme.label, after: plan.before.activeScheme.label }],
        context: clone(plan.context), tradeoffs: [], privilege: 'none', undo: { supported: true, conditions: 'Only while ' + plan.before.activeScheme.label + ' is still active' } });
    }
    function recheck(p) {
      var n = (X().rechecks[p.findingId] || 0) + 1;
      X().rechecks[p.findingId] = n;
      var reps = ctx.state().reports, ev = [];
      for (var id in reps) { var f = reps[id].findings.filter(function (x) { return x.id === p.findingId; })[0]; if (f) { ev = f.evidenceIds.slice(); break; } }
      // The first check finds nothing changed; the next one finds it fixed (the user changed it in Settings meanwhile).
      return { findingId: p.findingId, status: n >= 2 ? 'verified_by_rescan' : 'awaiting_rescan', evidenceIds: ev };
    }
    // For the agent: request_action builds a plan the user approves in the app (agents never approve).
    function requestFromAgent(t, actionId, parameters, findingId, invocationId) {
      var pending = Object.keys(X().plans).map(function (k) { return X().plans[k]; })
        .filter(function (pl) { return pl.requestedBy.taskId === t.taskId && pl.status === 'awaiting_user'; });
      if (pending.length) return Promise.resolve(clone(pending[0]));
      var kind = t.pairing ? t.pairing.client : 'in_app';
      return makePlan({ actionId: actionId, parameters: parameters, findingId: findingId, reportId: t.scope.reportId }, { kind: kind, taskId: t.taskId, invocationId: invocationId });
    }

    return {
      requestFromAgent: requestFromAgent,
      methods: {
        'action.catalog': function () { return { actions: clone(CATALOG) }; },
        'action.preview': function (p) { return ctx.withIdem('action.preview', p, function (q) { return makePlan(q); }); },
        'action.get': function (p) { var pl = getPlan(p.planId), op = opOf(p.planId); var v = clone(pl); if (op) v.operation = clone(op); return v; },
        'action.decline': function (p) {
          var pl = getPlan(p.planId);
          if (pl.status === 'awaiting_user') { pl.status = 'declined'; emitPlan(pl); }
          return clone(pl);
        },
        'shell.approvePlan': approvePlan,
        'action.undo': function (p) { return ctx.withIdem('action.undo', p, undo); },
        'operation.get': function (p) { var op = X().ops[p.operationId]; if (!op) throw E(-32001, 'not_found', { kind: 'operation', id: p.operationId }); return clone(op); },
        'operation.list': function (p) {
          var all = Object.keys(X().ops).map(function (k) { return X().ops[k]; });
          return { operations: clone(all.slice(-Math.min((p && p.limit) || 50, 50))), next: null };
        },
        'manual.recheck': recheck,
        'fixture.setApproval': function (p) { X().nextApproval = { status: p.status, error: p.error || null }; return { ok: true }; },
        'fixture.drift': function () { X().pc.power = 'Power saver'; ctx.save(); return { power: X().pc.power }; }
      }
    };
  }

  createFixtureActions.CATALOG = CATALOG;
  if (root) { root.OptTool = root.OptTool || {}; root.OptTool.createFixtureActions = createFixtureActions; }
  if (typeof module !== 'undefined' && module.exports) module.exports = { createFixtureActions: createFixtureActions };
})(typeof window !== 'undefined' ? window : null);
