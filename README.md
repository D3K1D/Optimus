# Optimus

A Windows PC audit and optimization app. It checks security, performance, storage, privacy and startup locally, explains what it finds, and can make approved, reversible changes. You can use it on its own, or connect an AI (Claude Code, Codex, ChatGPT or an API key) to ask questions about your PC inside the app.

## Install

Download the `.msi` from Releases and run it. Windows 10 or 11, 64-bit. The installer is not signed yet, so Windows may show "Windows protected your PC": choose **More info → Run anyway**.

## Build

Requires the .NET 10 SDK (see `global.json`) and Node.js for the UI tests.

```
dotnet build OptimizationTool.slnx
dotnet test OptimizationTool.slnx --filter "Category!=Live"
node --test ui/tests/
```

Installer: `powershell -ExecutionPolicy Bypass -File packaging\build-installer.ps1` (uses WiX; accepting its license is up to you).

The UI also runs in a browser with a simulated backend: `node ui/tools/serve.mjs`, then open the printed address.

## License

MIT. Third-party notices for the bundled React and Geist fonts are in `ui/vendor/`.
