# Roadmap

## Standalone desktop build

Goal: ship db-manager as a self-contained executable — no .NET install, no browser URL typing.

### Phase 1 — Single-file publish

One exe per OS (`win-x64`, `win-arm64`, `linux-x64`, `osx-arm64`) via:

```bash
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

- [ ] Adjust `Program.cs` for standalone use:
  - [ ] Skip `app.UseHttpsRedirection()` when running standalone (no dev cert on user machines); bind to `localhost` only.
  - [ ] Launch browser automatically: `app.Start()` → `Process.Start` the `http://localhost:...` URL → `await app.WaitForShutdownAsync()`.
  - [ ] Optionally gate the above behind a `--standalone` arg or a config flag so `dotnet run` dev behavior stays unchanged.
- [ ] Bundle `wwwroot/` assets: add `-p:IncludeAllContentForSelfExtract=true` to the publish command, or copy `wwwroot/` next to the exe.
- [ ] Accept the size: no Native AOT is possible (Razor components / Blazor Server are not AOT-compatible), so expect ~100–150 MB per exe with all four DB drivers included.
- [ ] Add a publish script or `PublishProfile` so the build is one command.

### Phase 2 — Native window (optional)

A local Kestrel server + browser tab may be enough for a single-user tool. If a real desktop window is wanted:

- [ ] Evaluate [Photino.NET](https://github.com/tryphotino/photino.NET) — lightweight, cross-OS native webview hosting Kestrel, no DevExpress, fits the project constraints.
- [ ] Alternative considered and rejected: Blazor Hybrid / .NET MAUI (much bigger footprint for little gain here).

## Ideas parking lot

- Nothing here yet — add candidate features as they come up, promote to a phase when they become real.