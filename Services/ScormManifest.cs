using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ScormEditor.Services;

/// <summary>The parts of imsmanifest.xml the editor shows: version, default organization tree and launch page.</summary>
public sealed class ScormManifest
{
    public sealed record Item(string Title, int Depth, string? Path, string? Href);
    public sealed record Resource(string Identifier, string? Href, string ScormType, string? Path);

    public string? Error { get; private init; }
    public string Version { get; private init; } = "SCORM";
    public string Identifier { get; private init; } = "";
    public string CourseTitle { get; private init; } = "";
    public IReadOnlyList<Item> Items { get; private init; } = [];
    public IReadOnlyList<Resource> Resources { get; private init; } = [];
    public string? LaunchPath { get; private init; }

    public bool Is2004 => Version == "SCORM 2004";

    public static ScormManifest Parse(ScormPackage package, string manifestPath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(package.ReadText(manifestPath));
        }
        catch (XmlException ex)
        {
            return new ScormManifest { Error = $"Line {ex.LineNumber}: {ex.Message}" };
        }

        var root = doc.Root!;
        var schemaVersion = Descendants(root, "schemaversion").FirstOrDefault()?.Value.Trim() ?? "";
        var namespaces = string.Join(' ', root.Attributes().Where(a => a.IsNamespaceDeclaration).Select(a => a.Value));
        var version =
            schemaVersion.Contains("2004") || schemaVersion.Contains("CAM 1.3") || namespaces.Contains("adlcp_v1p3") ? "SCORM 2004"
            : schemaVersion.Contains("1.2") || namespaces.Contains("adlcp_rootv1p2") ? "SCORM 1.2"
            : schemaVersion.Length > 0 ? "SCORM " + schemaVersion : "SCORM";

        var manifestDir = ScormPackage.DirOf(manifestPath);
        var resourcesEl = Descendants(root, "resources").FirstOrDefault();
        var base1 = resourcesEl?.Attribute(XNamespace.Xml + "base")?.Value ?? "";

        var resources = new List<Resource>();
        foreach (var r in Descendants(root, "resource"))
        {
            var href = r.Attribute("href")?.Value;
            var type = r.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("scormtype", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            var base2 = r.Attribute(XNamespace.Xml + "base")?.Value ?? "";
            string? path = null;
            if (!string.IsNullOrEmpty(href))
            {
                var clean = Uri.UnescapeDataString(href.Split('?', '#')[0]);
                path = package.Resolve($"{manifestDir}/{base1}/{base2}/{clean}");
            }
            resources.Add(new Resource(r.Attribute("identifier")?.Value ?? "", href, type, path));
        }

        var orgs = Descendants(root, "organizations").FirstOrDefault();
        var defaultId = orgs?.Attribute("default")?.Value;
        var allOrgs = Descendants(root, "organization").ToList();
        var org = allOrgs.FirstOrDefault(o => o.Attribute("identifier")?.Value == defaultId) ?? allOrgs.FirstOrDefault();

        var items = new List<Item>();
        void Walk(XElement parent, int depth)
        {
            foreach (var it in parent.Elements().Where(e => e.Name.LocalName == "item"))
            {
                var title = it.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim();
                var res = resources.FirstOrDefault(r => r.Identifier == it.Attribute("identifierref")?.Value);
                items.Add(new Item(string.IsNullOrEmpty(title) ? it.Attribute("identifier")?.Value ?? "Item" : title, depth, res?.Path, res?.Href));
                Walk(it, depth + 1);
            }
        }
        if (org is not null) Walk(org, 0);

        var launch = items.FirstOrDefault(i => i.Path is not null)?.Path
            ?? resources.FirstOrDefault(r => r.Path is not null && r.ScormType.Equals("sco", StringComparison.OrdinalIgnoreCase))?.Path
            ?? resources.FirstOrDefault(r => r.Path is not null)?.Path;

        return new ScormManifest
        {
            Version = version,
            Identifier = root.Attribute("identifier")?.Value ?? "",
            CourseTitle = org?.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim() ?? "",
            Items = items,
            Resources = resources,
            LaunchPath = launch,
        };
    }

    /// <summary>Returns the manifest text with the launch resource's &lt;file&gt; list rebuilt from the package contents.</summary>
    internal static (string Xml, int Count)? SyncFileList(ScormPackage package, ScormManifest manifest)
    {
        var doc = XDocument.Parse(package.ReadText(package.ManifestPath), LoadOptions.PreserveWhitespace);
        var resourceEls = Descendants(doc.Root!, "resource").ToList();
        var launchRes = manifest.Resources.FirstOrDefault(r => r.Path == manifest.LaunchPath) ?? manifest.Resources.FirstOrDefault();
        var el = resourceEls.FirstOrDefault(r => r.Attribute("identifier")?.Value == launchRes?.Identifier);
        if (el is null) return null;

        var ns = el.Name.Namespace;
        el.Elements().Where(e => e.Name.LocalName == "file").Remove();
        var dir = ScormPackage.DirOf(package.ManifestPath);
        var paths = package.Paths
            .Where(p => p != package.ManifestPath && !p.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".dtd", StringComparison.OrdinalIgnoreCase))
            .Where(p => dir.Length == 0 || p.StartsWith(dir + "/"))
            .ToList();

        // Insert before any <dependency> elements, as the schema requires files first.
        var anchor = el.Elements().FirstOrDefault(e => e.Name.LocalName == "dependency");
        var indent = "\n      ";
        var nodes = new List<XNode>();
        foreach (var p in paths)
        {
            nodes.Add(new XText(indent));
            nodes.Add(new XElement(ns + "file", new XAttribute("href", ScormPackage.Relative(dir, p))));
        }
        nodes.Add(new XText("\n    "));
        foreach (var t in el.Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).ToList()) t.Remove();
        if (anchor is not null) anchor.AddBeforeSelf(nodes); else el.Add(nodes);

        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true }))
            doc.Save(w);
        var decl = doc.Declaration?.ToString() ?? "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";
        return (decl + "\n" + sb.ToString().TrimStart() + "\n", paths.Count);
    }

    private static IEnumerable<XElement> Descendants(XElement root, string localName) =>
        root.DescendantsAndSelf().Where(e => e.Name.LocalName == localName);
}
