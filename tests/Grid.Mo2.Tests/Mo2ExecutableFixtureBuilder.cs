using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed class Mo2ExecutableFixtureBuilder : IDisposable
{
    public static readonly GameId SkyrimGame = new("game.skyrim-special-edition");
    public static readonly GameAdapterId Adapter = new("adapter.mod-organizer-2");

    public Mo2ExecutableFixtureBuilder()
    {
        Root = Path.Combine(Path.GetTempPath(), $"grid-mo2-executables-{Guid.NewGuid():N}");
        Application = Path.Combine(Root, "application");
        Instance = Path.Combine(Root, "instance");
        Base = Path.Combine(Instance, "base");
        Game = Path.Combine(Root, "game");
        Mods = Path.Combine(Base, "mods");
        Profiles = Path.Combine(Base, "profiles");
        Downloads = Path.Combine(Base, "downloads");
        Overwrite = Path.Combine(Base, "overwrite");
        foreach (var directory in new[] { Application, Instance, Base, Game, Mods, Profiles, Downloads, Overwrite })
        {
            Directory.CreateDirectory(directory);
        }

        ExecutablePath = Path.Combine(Application, "ModOrganizer.exe");
        File.WriteAllBytes(ExecutablePath, [0x4D, 0x5A]);
        IniPath = Path.Combine(Instance, "ModOrganizer.ini");
        File.WriteAllText(IniPath, "[customExecutables]\r\nsize=0\r\n", new UTF8Encoding(false));
        Paths = new WindowsPathCanonicalizer();
        FileSystem = new Mo2FileSystem();
        Authorizations = new Mo2SessionExecutablePathAuthorization(Paths);
        Service = new(
            FileSystem,
            Paths,
            new Mo2TextDecoder(),
            Authorizations,
            timeProvider: new FixedTimeProvider(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero)));
    }

    public string Root { get; }

    public string Application { get; }

    public string Instance { get; }

    public string Base { get; }

    public string Game { get; }

    public string Mods { get; }

    public string Profiles { get; }

    public string Downloads { get; }

    public string Overwrite { get; }

    public string ExecutablePath { get; }

    public string IniPath { get; }

    public WindowsPathCanonicalizer Paths { get; }

    public Mo2FileSystem FileSystem { get; }

    public Mo2SessionExecutablePathAuthorization Authorizations { get; }

    public Mo2ExecutableConfigurationService Service { get; }

    public InstallationReferenceId ReferenceId => new("reference.mo2.executables-fixture");

    public void WriteConfiguration(string body) =>
        File.WriteAllText(IniPath, body, new UTF8Encoding(false));

    public string WriteExpectedBinary(string relativePath, byte[]? bytes = null)
    {
        var path = Path.Combine(Application, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? [0x4D, 0x5A]);
        return path;
    }

    public string WriteOutsideBinary(string name, byte[]? bytes = null)
    {
        var outside = Path.Combine(Root, "outside");
        Directory.CreateDirectory(outside);
        var path = Path.Combine(outside, name);
        File.WriteAllBytes(path, bytes ?? [0x4D, 0x5A]);
        return path;
    }

    public Mo2ExecutableObservationRequest Request() => new(Reference(), Validation());

    public Mo2InstallationReference Reference() => new(
        Mo2InstallationReference.CurrentSchemaVersion,
        ReferenceId,
        new("installation.mo2.executables-fixture"),
        SkyrimGame,
        Adapter,
        "Executable fixture",
        Mo2InstanceKind.Global,
        ExecutablePath,
        Instance);

    public Mo2InstallationValidation Validation() => new(
        Mo2ValidationStatus.Valid,
        Mo2SelectionKind.GlobalInstance,
        true,
        Mo2InstanceKind.Global,
        Application,
        ExecutablePath,
        Instance,
        IniPath,
        Base,
        Mods,
        Profiles,
        Downloads,
        Overwrite,
        Game,
        "Skyrim Special Edition",
        "fixture-connection",
        [],
        []);

    public string ContentHash()
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(Root, path), StringComparer.OrdinalIgnoreCase))
        {
            var relative = Encoding.UTF8.GetBytes(Path.GetRelativePath(Root, file));
            aggregate.AppendData(relative);
            aggregate.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(aggregate.GetHashAndReset());
    }

    public static byte[] MinimalPeWithIcon(byte[] iconImage)
    {
        const int peOffset = 0x80;
        const int optionalSize = 0xE0;
        const int sectionHeader = peOffset + 24 + optionalSize;
        const int rawOffset = 0x200;
        const int rawSize = 0x400;
        const uint resourceRva = 0x1000;
        var image = new byte[rawOffset + rawSize];
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0), 0x5A4D);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(peOffset), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 4), 0x14C);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 20), optionalSize);
        var optional = peOffset + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), 0x10B);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 92), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 112), resourceRva);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 116), 0x200);
        Encoding.ASCII.GetBytes(".rsrc\0\0\0").CopyTo(image.AsSpan(sectionHeader));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionHeader + 8), rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionHeader + 12), resourceRva);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionHeader + 16), rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(sectionHeader + 20), rawOffset);

        var resource = image.AsSpan(rawOffset, rawSize);
        WriteDirectory(resource, 0x00, 2);
        WriteEntry(resource, 0x10, 3, 0x20, directory: true);
        WriteEntry(resource, 0x18, 14, 0x70, directory: true);
        WriteDirectory(resource, 0x20, 1);
        WriteEntry(resource, 0x30, 1, 0x40, directory: true);
        WriteDirectory(resource, 0x40, 1);
        WriteEntry(resource, 0x50, 1033, 0x58, directory: false);
        WriteDataEntry(resource, 0x58, resourceRva + 0xE0, checked((uint)iconImage.Length));
        WriteDirectory(resource, 0x70, 1);
        WriteEntry(resource, 0x80, 1, 0x90, directory: true);
        WriteDirectory(resource, 0x90, 1);
        WriteEntry(resource, 0xA0, 1033, 0xA8, directory: false);
        WriteDataEntry(resource, 0xA8, resourceRva + 0xC0, 20);

        var group = resource[0xC0..];
        BinaryPrimitives.WriteUInt16LittleEndian(group, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(group[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(group[4..], 1);
        group[6] = 16;
        group[7] = 16;
        BinaryPrimitives.WriteUInt16LittleEndian(group[10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(group[12..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(group[14..], checked((uint)iconImage.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(group[18..], 1);
        iconImage.CopyTo(resource[0xE0..]);
        return image;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WriteDirectory(Span<byte> resource, int offset, ushort idCount) =>
        BinaryPrimitives.WriteUInt16LittleEndian(resource[(offset + 14)..], idCount);

    private static void WriteEntry(Span<byte> resource, int offset, uint id, uint target, bool directory)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(resource[offset..], id);
        BinaryPrimitives.WriteUInt32LittleEndian(resource[(offset + 4)..], directory ? target | 0x80000000u : target);
    }

    private static void WriteDataEntry(Span<byte> resource, int offset, uint rva, uint size)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(resource[offset..], rva);
        BinaryPrimitives.WriteUInt32LittleEndian(resource[(offset + 4)..], size);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
