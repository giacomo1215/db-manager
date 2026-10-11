using DbManager.Abstractions;
using DbManager.Components;
using DbManager.Providers;
using DbManager.Services;

var builder = WebApplication.CreateBuilder(args);

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
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();