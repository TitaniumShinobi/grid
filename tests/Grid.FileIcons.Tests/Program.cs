using Grid.FileIcons;

var repoRoot = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

var mapPath = Path.Combine(
    repoRoot,
    "core", "features", "explorer", "code-file-icon-map.json");

var assetRoot = Path.Combine(
    repoRoot,
    "core", "assets", "file_icons", "material-icon-theme");

if (!File.Exists(mapPath))
    throw new Exception($"Icon map missing: {mapPath}");

if (!Directory.Exists(assetRoot))
    throw new Exception($"Icon assets missing: {assetRoot}");

var resolver = CodeFileIconResolver.FromJson(
    await File.ReadAllTextAsync(mapPath));

var cases = new[]
{
    "README.md",
    "Program.cs",
    "MainWindow.xaml",
    "package.json",
    "package-lock.json",
    "tsconfig.json",
    "index.html",
    "styles.css",
    "app.ts",
    "component.tsx",
    "config.yaml",
    "data.json",
    "script.ps1",
    ".env",
    ".gitignore",
    "image.png",
    "archive.zip",
    "library.dll",
    "program.exe",
    "unknown.xyz",
};

foreach (var path in cases)
{
    var id = resolver.IconIdForFile(path);
    var svg = resolver.SvgFileNameForFile(path);
    var exists = File.Exists(Path.Combine(assetRoot, svg));

    Console.WriteLine(
        $"{path,-22} -> {id,-20} -> {svg,-30} {(exists ? "PASS" : "MISSING")}");

    if (!exists)
        throw new Exception($"{path} resolved to missing SVG: {svg}");
}

/*
 * Exhaustively verify every icon filename referenced by the mapping snapshot.
 */
using var document = System.Text.Json.JsonDocument.Parse(
    await File.ReadAllTextAsync(mapPath));

var iconFiles = document.RootElement.GetProperty("iconFiles");

var missing = new List<string>();

foreach (var property in iconFiles.EnumerateObject())
{
    var svg = property.Value.GetString();

    if (string.IsNullOrWhiteSpace(svg) ||
        !File.Exists(Path.Combine(assetRoot, svg)))
    {
        missing.Add($"{property.Name} -> {svg}");
    }
}

if (missing.Count != 0)
{
    Console.WriteLine();
    Console.WriteLine("MISSING MAPPED ASSETS:");

    foreach (var value in missing)
        Console.WriteLine(value);

    throw new Exception(
        $"{missing.Count} mapped Material Icon Theme assets are missing.");
}

Console.WriteLine();
Console.WriteLine(
    $"PASS Material Icon Theme mapping: {iconFiles.EnumerateObject().Count()} mapped icons available.");

Console.WriteLine(
    $"PASS Installed SVG assets: {Directory.GetFiles(assetRoot, "*.svg").Length}");
