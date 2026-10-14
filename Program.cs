using DbManager.Abstractions;
using DbManager.Components;
using DbManager.Providers;
using DbManager.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
#if DESKTOP
using Photino.NET;
#endif

var builder = WebApplication.CreateBuilder(args);

#if DESKTOP
// Desktop release: loopback-only with an OS-assigned port. Invisible to the
// LAN, no TLS cert needed.
builder.WebHost.UseUrls("http://127.0.0.1:0");
#else
var configuredUrls = builder.Configuration["urls"];
// Server release: LAN-ready default binding when nothing else was configured
// (e.g. bare double-click of the exe). WebApplication only populates
// app.Urls at Start, so checking it there would misfire even when
// --urls/ASPNETCORE_URLS/launchSettings have set values.
if (string.IsNullOrEmpty(configuredUrls))
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
#endif

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// --- Engine connectors: one singleton per engine. Adding a new database is
// one class implementing IDbConnector + one registration here. ---
builder.Services.AddSingleton<IDbConnector, SqlServerConnector>();
builder.Services.AddSingleton<IDbConnector, MySqlDbConnector>();
builder.Services.AddSingleton<IDbConnector, PostgreSqlConnector>();
builder.Services.AddSingleton<IDbConnector, SqliteConnector>();
builder.Services.AddSingleton<IDbConnectorRegistry, ConnectorRegistry>();

// --- Database backup: one dumper per engine (same one-class-per-engine shape
// as the connectors) + the orchestrating service (singleton, stateless). ---
builder.Services.AddSingleton<IDbDumper, SqlServerDumper>();
builder.Services.AddSingleton<IDbDumper, MySqlDumper>();
builder.Services.AddSingleton<IDbDumper, PostgreSqlDumper>();
builder.Services.AddSingleton<IDbDumper, SqliteDumper>();
builder.Services.AddSingleton<IDbDumpService, DbDumpService>();

// --- Connection vault: stateless crypto/file facade (singleton) + per-circuit
// unlocked state (scoped; disposing it zeroes the derived key). ---
builder.Services.AddSingleton<IVaultService, VaultService>();
builder.Services.AddScoped<ISessionVault, SessionVault>();

// --- Backup & restore: stateless format facade (singleton). Crypto stays in VaultService. ---
builder.Services.AddSingleton<IBackupService, BackupService>();

// --- User preferences: plain JSON file beside the vault (non-secret, readable
// while locked). Singleton so the loaded values are shared by every circuit. ---
builder.Services.AddSingleton<ISettingsService, SettingsService>();

// --- Saved-query library: scoped SQLite store (encrypts under the session vault
// key, so it is per-circuit like ISessionVault) + stateless backup format facade. ---
builder.Services.AddSingleton<IQueryBackupService, QueryBackupService>();
builder.Services.AddScoped<IQueryStore, QueryStore>();

// --- SQL editor: scoped per circuit (schema cache lives in the ViewModel). ---
builder.Services.AddScoped<IToastService, ToastService>();
builder.Services.AddScoped<ISqlEditorService, SqlEditorService>();
builder.Services.AddScoped<ISqlEditorViewModel, SqlEditorViewModel>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/not-found", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

#if !DESKTOP
// Https redirect only when an https endpoint actually exists; without a
// cert it would 500 every redirect.
var hasHttpsEndpoint =
    (configuredUrls ?? string.Empty).Contains("https://", StringComparison.OrdinalIgnoreCase)
    || builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren()
        .Any(e => (e["Url"] ?? string.Empty).StartsWith("https://", StringComparison.OrdinalIgnoreCase));
if (hasHttpsEndpoint)
    app.UseHttpsRedirection();
#endif

app.UseAntiforgery();

#if DESKTOP
// Single-file exe: assets are embedded (see EmbeddedSiteFileProvider).
// blazor.web.js and the circuit endpoints come from MapRazorComponents.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new DbManager.Desktop.EmbeddedSiteFileProvider(typeof(App).Assembly),
});
#else
app.MapStaticAssets();
#endif
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

#if DESKTOP
await app.StartAsync();
var url = app.Services.GetRequiredService<IServer>()
    .Features.Get<IServerAddressesFeature>()!.Addresses.First();

var window = new PhotinoWindow()
    .SetTitle("DB Manager")
    .SetUseOsDefaultSize(false)
    .SetSize(1440, 900)
    .Center()
    .SetGrantBrowserPermissions(true)
    .Load(url);

window.WaitForClose();
await app.StopAsync();
#else
Console.WriteLine($"DB Manager listening on: {string.Join(", ", app.Urls)}");
app.Run();
#endif