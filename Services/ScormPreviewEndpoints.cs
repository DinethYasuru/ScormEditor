using Microsoft.AspNetCore.StaticFiles;

namespace ScormEditor.Services;

/// <summary>
/// Serves the files of an open package so the preview iframe loads it like an LMS would, from real
/// relative URLs on the same origin as the editor (which is what lets the course find window.API).
/// </summary>
public static class ScormPreviewEndpoints
{
    public const string PreviewPrefix = "/scorm-preview";

    private static readonly FileExtensionContentTypeProvider ContentTypes = CreateProvider();

    public static IEndpointRouteBuilder MapScormPreview(this IEndpointRouteBuilder app)
    {
        app.MapGet(PreviewPrefix + "/{id:guid}/{**path}", (Guid id, string? path, ScormPackageStore store, HttpContext http) =>
        {
            var package = store.Get(id);
            if (package is null) return Results.NotFound("This package is no longer open. Reload the editor.");
            var resolved = package.Resolve(path ?? "");
            if (resolved is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(package.Read(resolved) ?? [], ContentTypeOf(resolved));
        });

        // Code edits and image swaps from the browser are written here rather than over the
        // Blazor circuit, so large files are not limited by the SignalR message size.
        app.MapPut(PreviewPrefix + "/{id:guid}/{**path}", async (Guid id, string? path, ScormPackageStore store, HttpRequest request) =>
        {
            var package = store.Get(id);
            if (package is null) return Results.NotFound();
            var target = ScormPackage.Normalize(path ?? "");
            if (target.Length == 0) return Results.BadRequest();
            target = package.Resolve(target) ?? target;
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            package.Write(target, ms.ToArray());
            return Results.NoContent();
        }).DisableAntiforgery();

        app.MapGet("/scorm-export/{id:guid}", (Guid id, ScormPackageStore store) =>
        {
            var package = store.Get(id);
            if (package is null) return Results.NotFound();
            var name = Path.GetFileNameWithoutExtension(package.FileName) + "-edited.zip";
            return Results.File(package.ToZip(), "application/zip", name);
        });

        return app;
    }

    public static string ContentTypeOf(string path) =>
        ContentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream";

    private static FileExtensionContentTypeProvider CreateProvider()
    {
        var p = new FileExtensionContentTypeProvider();
        p.Mappings[".js"] = "text/javascript";
        p.Mappings[".mjs"] = "text/javascript";
        p.Mappings[".json"] = "application/json";
        p.Mappings[".vtt"] = "text/vtt";
        p.Mappings[".woff2"] = "font/woff2";
        p.Mappings[".xsd"] = "application/xml";
        return p;
    }
}
