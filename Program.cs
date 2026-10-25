using ScormEditor.Components;
using ScormEditor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddSingleton<AssetVersion>();
builder.Services.Configure<ScormEditorOptions>(builder.Configuration.GetSection("ScormEditor"));
builder.Services.AddSingleton<ScormPackageStore>();

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
