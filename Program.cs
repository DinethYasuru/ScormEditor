using ScormEditor.Components;
using Microsoft.AspNetCore.DataProtection;
using ScormEditor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddSingleton<AssetVersion>();
builder.Services.Configure<ScormEditorOptions>(builder.Configuration.GetSection("ScormEditor"));
builder.Services.AddSingleton<ScormPackageStore>();
builder.Services.AddSingleton<AzureBlobService>();
builder.Services.AddScoped<AzureConnections>();

// Saved Azure SAS URLs are encrypted with these keys before they are stored in the user's browser.
// The keys must outlive a restart, or saved locations can't be read again: on IIS without a loaded
// user profile, set ScormEditor:DataProtectionKeysPath to a folder only the app can read.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("ScormEditor");
if (builder.Configuration["ScormEditor:DataProtectionKeysPath"] is { Length: > 0 } keysPath)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // Edited pages are sent to the server for review; a large course page can exceed the 32 KB default.
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 64 * 1024 * 1024);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapScormPreview();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
