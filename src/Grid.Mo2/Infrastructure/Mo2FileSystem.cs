using Grid.Mo2.Models;
using Grid.Mo2.Services;
using System.Collections.Immutable;

namespace Grid.Mo2.Infrastructure;

public sealed class Mo2FileSystem : IMo2InventoryFileSystem, IGridAtomicFileStore
{
    public Mo2PathState ProbeFile(string path) => Probe(path, expectDirectory: false);

    public Mo2PathState ProbeDirectory(string path) => Probe(path, expectDirectory: true);

    public IReadOnlyList<string> EnumerateDirectories(string path)
        => EnumerateDirectoriesWithState(path).Directories;

    public Mo2DirectoryEnumeration EnumerateDirectoriesWithState(string path)
    {
        try
        {
            return new(
                Mo2PathState.Present,
                Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly).ToImmutableArray());
        }
        catch (DirectoryNotFoundException)
        {
            return new(Mo2PathState.Missing, []);
        }
        catch (UnauthorizedAccessException)
        {
            return new(Mo2PathState.Inaccessible, []);
        }
        catch (IOException)
        {
            return new(Mo2PathState.Inaccessible, []);
        }
    }

    public IReadOnlyList<string> EnumerateFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    public Mo2FileMetadata GetFileMetadata(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new(Mo2PathState.Invalid, null);
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new(Mo2PathState.Missing, null);
            }

            return new(
                Mo2PathState.Present,
                new Mo2FileStamp(info.Length, info.LastWriteTimeUtc.Ticks));
        }
        catch (UnauthorizedAccessException)
        {
            return new(Mo2PathState.Inaccessible, null);
        }
        catch (IOException)
        {
            return new(Mo2PathState.Inaccessible, null);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return new(Mo2PathState.Invalid, null);
        }
    }

    public Mo2DirectoryMetadata GetDirectoryMetadata(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new(Mo2PathState.Invalid, null, null);
        }

        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists)
            {
                return new(Mo2PathState.Missing, null, null);
            }

            return new(
                Mo2PathState.Present,
                new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (UnauthorizedAccessException)
        {
            return new(Mo2PathState.Inaccessible, null, null);
        }
        catch (IOException)
        {
            return new(Mo2PathState.Inaccessible, null, null);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return new(Mo2PathState.Invalid, null, null);
        }
    }

    public async Task<ImmutableArray<byte>> ReadBytesAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
        => (await ReadBytesWithMetadataAsync(path, maximumBytes, cancellationToken).ConfigureAwait(false)).Bytes;

    public async Task<Mo2FileReadResult> ReadBytesWithMetadataAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (maximumBytes == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException($"File exceeds the {maximumBytes}-byte read limit.");
        }

        var before = new Mo2FileStamp(stream.Length, File.GetLastWriteTimeUtc(path).Ticks);

        using var bounded = new MemoryStream(capacity: (int)Math.Min(stream.Length, 64 * 1024));
        var buffer = new byte[16 * 1024];
        var totalBytes = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalBytes += read;
            if (totalBytes > maximumBytes)
            {
                throw new InvalidDataException($"File exceeds the {maximumBytes}-byte read limit.");
            }

            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        var after = new Mo2FileStamp(stream.Length, File.GetLastWriteTimeUtc(path).Ticks);
        return new(ImmutableArray.Create(bounded.ToArray()), before, after);
    }

    public async Task<string> ReadTextAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        if (maximumBytes == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var bytes = await ReadBytesAsync(path, maximumBytes, cancellationToken).ConfigureAwait(false);
        using var bounded = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new StreamReader(bounded, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    bool IGridAtomicFileStore.Exists(string path) => File.Exists(path);

    async Task<string> IGridAtomicFileStore.ReadTextAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken) =>
        await ReadTextAsync(path, maximumBytes, cancellationToken).ConfigureAwait(false);

    public async Task WriteAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The reference-store path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task DeleteIfExistsAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(Path.GetFullPath(path));
        return Task.CompletedTask;
    }

    private static Mo2PathState Probe(string path, bool expectDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Mo2PathState.Invalid;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            return isDirectory == expectDirectory ? Mo2PathState.Present : Mo2PathState.Invalid;
        }
        catch (FileNotFoundException)
        {
            return Mo2PathState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return Mo2PathState.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return Mo2PathState.Inaccessible;
        }
        catch (IOException)
        {
            return Mo2PathState.Inaccessible;
        }
        catch (ArgumentException)
        {
            return Mo2PathState.Invalid;
        }
        catch (NotSupportedException)
        {
            return Mo2PathState.Invalid;
        }
    }
}
