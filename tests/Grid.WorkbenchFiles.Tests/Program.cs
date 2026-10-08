using Grid.DocumentViewer;
using Grid.DocumentViewer.Tests;
using Grid.Explorer;
using Grid.Explorer.Tests;
using Grid.Files;

MarkdownBlocksTests.Run();
ExplorerProjectionChecks.Run();

var fixture = Path.Combine(
    Path.GetTempPath(),
    "grid-workbench-files-" + Guid.NewGuid().ToString("N"));

try
{
    var root = Path.Combine(fixture, "root");
    Directory.CreateDirectory(Path.Combine(root, "docs"));

    await File.WriteAllTextAsync(
        Path.Combine(root, "docs", "GRID_TERMS_OF_SERVICE.md"),
        "# GRID Terms of Service\n\nWelcome to GRID.");

    var files = new AuthorizedFileService();
    files.AuthorizeRoot("fixture", root);

    var listing = files.ListDirectory("fixture", "docs");
    Assert(listing.Success, "Authorized docs directory loads.");

    var projected = ExplorerProjection.Sort(
        listing.Value!.Select(value =>
            new ExplorerEntry(
                value.Name,
                value.RelativePath,
                value.IsDirectory)));

    Assert(
        projected.Single().Name == "GRID_TERMS_OF_SERVICE.md",
        "Explorer projects authorized file.");

    var document = await files.ReadTextAsync(
        "fixture",
        "docs/GRID_TERMS_OF_SERVICE.md");

    Assert(document.Success, "Viewer reads through authorized boundary.");

    var blocks = MarkdownBlocks.Parse(document.Value!);

    Assert(
        blocks.Count == 2 &&
        blocks[0].Kind == MarkdownBlockKind.Heading &&
        blocks[0].Text == "GRID Terms of Service",
        "Authorized Markdown reaches document projection.");

    Console.WriteLine("PASS GRID workbench file pipeline");
}
finally
{
    if (Directory.Exists(fixture))
        Directory.Delete(fixture, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}
