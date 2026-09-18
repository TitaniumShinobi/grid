using System.Collections.Immutable;
using System.Text;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Services;

sealed class Mo2ResolvedStateFixtureBuilder : IDisposable
{
    private Mo2ResolvedStateFixtureBuilder(string root)
    {
        Root = root;
        RandomAccessFiles = new WindowsRandomAccessFileFactory();
        PluginParser = new Mo2PluginHeaderParser();
        ArchiveParser = new Mo2BsaIndexParser();
    }

    public string Root { get; }

    public WindowsRandomAccessFileFactory RandomAccessFiles { get; }

    public Mo2PluginHeaderParser PluginParser { get; }

    public Mo2BsaIndexParser ArchiveParser { get; }

    public static Task<Mo2ResolvedStateFixtureBuilder> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grid-mo2-resolved-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return Task.FromResult(new Mo2ResolvedStateFixtureBuilder(root));
    }

    public async Task<string> WritePluginAsync(
        string relativePath,
        uint flags,
        IEnumerable<string>? masters = null,
        bool useExtendedMasterSize = false)
    {
        var data = new MemoryStream();
        foreach (var master in masters ?? [])
        {
            var name = Encoding.Latin1.GetBytes(master + "\0");
            if (useExtendedMasterSize)
            {
                data.Write("XXXX"u8);
                WriteUInt16(data, 4);
                WriteUInt32(data, checked((uint)name.Length));
                data.Write("MAST"u8);
                WriteUInt16(data, 0);
            }
            else
            {
                data.Write("MAST"u8);
                WriteUInt16(data, checked((ushort)name.Length));
            }

            data.Write(name);
        }

        var output = new MemoryStream();
        output.Write("TES4"u8);
        WriteUInt32(output, checked((uint)data.Length));
        WriteUInt32(output, flags);
        WriteUInt32(output, 0);
        WriteUInt16(output, 0);
        WriteUInt16(output, 0);
        WriteUInt16(output, 1);
        WriteUInt16(output, 0);
        data.Position = 0;
        await data.CopyToAsync(output);
        return await WriteAsync(relativePath, output.ToArray());
    }

    public async Task<string> WriteBsa105Async(
        string relativePath,
        IEnumerable<string> virtualPaths,
        uint archiveFlags = 0x3,
        uint fileFlags = 0)
    {
        var grouped = virtualPaths
            .Select(path => path.Replace('/', '\\'))
            .GroupBy(path => Path.GetDirectoryName(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => (
                Folder: group.Key,
                Files: group.Select(path => Path.GetFileName(path) ?? throw new InvalidDataException()).ToArray()))
            .ToArray();
        var fileCount = grouped.Sum(group => group.Files.Length);
        var encodedFolders = grouped.Select(group => Encoding.Latin1.GetBytes(group.Folder + "\0")).ToArray();
        var encodedFiles = grouped.SelectMany(group => group.Files).Select(file => Encoding.Latin1.GetBytes(file + "\0")).ToArray();
        var totalFolderBytes = encodedFolders.Sum(folder => folder.Length);
        var totalFileBytes = encodedFiles.Sum(file => file.Length);
        var folderRecordOffset = 36;
        var recordsSize = checked(grouped.Length * 24);
        var indexLength = checked(folderRecordOffset + recordsSize + grouped.Length + totalFolderBytes + fileCount * 16 + totalFileBytes);

        using var output = new MemoryStream();
        output.Write("BSA\0"u8);
        WriteUInt32(output, 105);
        WriteUInt32(output, checked((uint)folderRecordOffset));
        WriteUInt32(output, archiveFlags);
        WriteUInt32(output, checked((uint)grouped.Length));
        WriteUInt32(output, checked((uint)fileCount));
        WriteUInt32(output, checked((uint)totalFolderBytes));
        WriteUInt32(output, checked((uint)totalFileBytes));
        WriteUInt32(output, fileFlags);

        var folderBlockOffset = folderRecordOffset + recordsSize;
        for (var index = 0; index < grouped.Length; index++)
        {
            WriteUInt64(output, StableHash(grouped[index].Folder));
            WriteUInt32(output, checked((uint)grouped[index].Files.Length));
            WriteUInt32(output, 0);
            WriteUInt64(output, checked((ulong)folderBlockOffset));
            folderBlockOffset += 1 + encodedFolders[index].Length + grouped[index].Files.Length * 16;
        }

        var sourceOrder = 0;
        for (var folderIndex = 0; folderIndex < grouped.Length; folderIndex++)
        {
            output.WriteByte(checked((byte)encodedFolders[folderIndex].Length));
            output.Write(encodedFolders[folderIndex]);
            foreach (var file in grouped[folderIndex].Files)
            {
                WriteUInt64(output, StableHash(file));
                WriteUInt32(output, 0);
                WriteUInt32(output, checked((uint)(indexLength + sourceOrder++)));
            }
        }

        foreach (var name in encodedFiles)
        {
            output.Write(name);
        }

        return await WriteAsync(relativePath, output.ToArray());
    }

    public async Task<string> WriteAsync(string relativePath, byte[] bytes)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
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

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static ulong StableHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
