using Grid.Files;

var fixture = Path.Combine(
    Path.GetTempPath(),
    "grid-authorized-files-" + Guid.NewGuid().ToString("N"));

try
{
    var root = Path.Combine(fixture, "root");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(Path.Combine(root, "docs"));

    await File.WriteAllTextAsync(
        Path.Combine(root, "docs", "readme.md"),
        "# GRID");

    var service = new AuthorizedFileService();
    service.AuthorizeRoot("fixture", root);

    var valid = await service.ReadTextAsync("fixture", "docs/readme.md");
    Assert(valid.Success && valid.Value == "# GRID",
        "Authorized file can be read.");

    var traversal = service.Resolve(
        "fixture",
        "../outside.txt");

    Assert(
        !traversal.Success &&
        traversal.Failure == AuthorizedFileFailure.OutsideRoot,
        "Parent traversal is rejected.");

    var absolute = service.Resolve(
        "fixture",
        Path.Combine(fixture, "outside.txt"));

    Assert(
        !absolute.Success &&
        absolute.Failure == AuthorizedFileFailure.InvalidPath,
        "Absolute path injection is rejected.");

    var listing = service.ListDirectory("fixture", "");

    Assert(
        listing.Success &&
        listing.Value!.Any(value =>
            value.Name == "docs" &&
            value.IsDirectory),
        "Authorized directory can be listed.");

    Console.WriteLine("PASS AuthorizedFileService");
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
