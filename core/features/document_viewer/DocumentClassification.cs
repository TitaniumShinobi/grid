// Adapted from CODE's fileViewerKindForPath/languageForPath in CodeWorkspaceScreen.tsx.
// This file is intentionally independent of CODE's React shell.
namespace Grid.DocumentViewer;

public enum DocumentKind { Markdown, Html, Image, Source, Unsupported }

public static class DocumentClassification
{
    public static DocumentKind Classify(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".markdown" => DocumentKind.Markdown,
            ".html" or ".htm" => DocumentKind.Html,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" => DocumentKind.Image,
            ".tsx" or ".ts" or ".jsx" or ".js" or ".json" or ".css" or ".txt" or ".yml" or ".yaml" or ".toml" or ".env" => DocumentKind.Source,
            _ => DocumentKind.Unsupported
        };
    }

    public static string Language(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".tsx" or ".ts" => "typescript",
        ".jsx" or ".js" => "javascript",
        ".json" => "json",
        ".css" => "css",
        ".md" => "markdown",
        ".html" => "html",
        _ => "plaintext"
    };
}
