// Portable .NET adaptation of CODE's materialIconIdForFile/materialIconUrlForIconId.
// Load source/code-file-icon-map.json once; supply the returned SVG filename to
// GRID's own asset loader. This does not grant filesystem access to documents.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Grid.FileIcons;

public sealed class CodeFileIconResolver
{
    private readonly Dictionary<string, string> _overrides;
    private readonly Dictionary<string, string> _fileNames;
    private readonly Dictionary<string, string> _extensions;
    private readonly Dictionary<string, string> _languageIds;
    private readonly Dictionary<string, string> _iconFiles;
    private readonly string[] _extensionOrder;
    private readonly string _fallback;

    private CodeFileIconResolver(JsonElement root)
    {
        _overrides = ReadMap(root.GetProperty("codeFileNameOverrides"));
        _fileNames = ReadMap(root.GetProperty("fileNames"));
        _extensions = ReadMap(root.GetProperty("fileExtensions"));
        _languageIds = ReadMap(root.GetProperty("languageIds"));
        _iconFiles = ReadMap(root.GetProperty("iconFiles"));
        var order = new List<string>();
        foreach (var item in root.GetProperty("fileExtensionOrder").EnumerateArray())
            order.Add(item.GetString() ?? "");
        _extensionOrder = order.ToArray();
        _fallback = root.GetProperty("defaultFileIcon").GetString() ?? "file";
    }

    public static CodeFileIconResolver FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new CodeFileIconResolver(document.RootElement);
    }

    public string IconIdForFile(string relativePath)
    {
        var lowerPath = relativePath.Replace('\\', '/').ToLowerInvariant();
        var slash = lowerPath.LastIndexOf('/');
        var basename = slash < 0 ? lowerPath : lowerPath[(slash + 1)..];
        if (_overrides.TryGetValue(basename, out var value)) return value;
        if (_fileNames.TryGetValue(basename, out value)) return value;
        if (_fileNames.TryGetValue(lowerPath, out value)) return value;
        foreach (var extension in _extensionOrder)
        {
            if (basename == extension || basename.EndsWith('.' + extension, StringComparison.Ordinal))
                return _extensions.TryGetValue(extension, out value) ? value : _fallback;
        }
        var language = LanguageForPath(lowerPath);
        return _languageIds.TryGetValue(language, out value) ? value : _fallback;
    }

    public string SvgFileNameForFile(string relativePath)
    {
        var id = IconIdForFile(relativePath);
        return _iconFiles.TryGetValue(id, out var filename)
            ? filename
            : _iconFiles.TryGetValue(_fallback, out filename) ? filename : "file.svg";
    }

    private static string LanguageForPath(string path)
    {
        if (path.EndsWith(".tsx", StringComparison.Ordinal) || path.EndsWith(".ts", StringComparison.Ordinal)) return "typescript";
        if (path.EndsWith(".jsx", StringComparison.Ordinal) || path.EndsWith(".js", StringComparison.Ordinal)) return "javascript";
        if (path.EndsWith(".json", StringComparison.Ordinal)) return "json";
        if (path.EndsWith(".css", StringComparison.Ordinal)) return "css";
        if (path.EndsWith(".md", StringComparison.Ordinal)) return "markdown";
        if (path.EndsWith(".html", StringComparison.Ordinal)) return "html";
        return "plaintext";
    }

    private static Dictionary<string, string> ReadMap(JsonElement element)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) map[property.Name] = property.Value.GetString() ?? "";
        return map;
    }
}
