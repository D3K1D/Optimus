// Boots the app on the selected backend (desktop bridge inside the WebView2 shell, fixture in a browser).
(function () {
  var OT = window.OptTool;
  OT.backend = OT.selectBackend();
  if (OT.backend.kind === 'fixture') {
    // Dev hook (website/fixture only): show any §9 connection state offline.
    //   OT_fixture.setProviderState('anthropic_api', 'error', 'auth')   ·   ?fixtureState=claude_code_mcp:client_closed
    var set = function (kind, status, detail, code) { return OT.backend.call('fixture.setProviderState', { kind: kind, status: status, detail: detail || null, code: code || null }); };
    window.OT_fixture = { setProviderState: set };
    var m = /[?&]fixtureState=([^&]+)/.exec(location.search);
    if (m) decodeURIComponent(m[1]).split(',').forEach(function (one) { var a = one.split(':'); set(a[0], a[1], a[2], a[3]).catch(function () {}); });
  }
  ReactDOM.createRoot(document.getElementById('root')).render(React.createElement(OT.ErrorBoundary, null, React.createElement(OT.AppFlow, { backend: OT.backend })));
})();
