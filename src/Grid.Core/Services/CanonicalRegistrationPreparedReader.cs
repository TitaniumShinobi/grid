using System.Text.Json;
using Grid.Core.Models;

namespace Grid.Core.Services;

internal sealed record RegistrationPreparedNode(RegistrationNavigationRow Row, RegistrationEvidence[] Evidence);
public sealed record RegistrationPreparedReaderStatistics(long PageReads, long CacheHits, long RowsMaterialized, long CachedBytes);

/// <summary>Candidate-only reader. The caller supplies the trusted expected digest; no publication or live binding exists here.</summary>
public sealed class CanonicalRegistrationPreparedReader : IDisposable
{
    public const int PageSize = PreparedCanonicalNavigationStore.PageSize;
    public const long MaximumCachedBytes = PreparedCanonicalNavigationStore.MaximumCachedBytes;
    private readonly string directory;
    private readonly RegistrationPreparedDescriptor descriptor;
    private readonly Dictionary<string, RegistrationPreparedPath> paths;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (object Value, long Charge, LinkedListNode<string> Usage)> cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> usage = [];
    private long cachedBytes, pageReads, cacheHits, rowsMaterialized;
    private int disposed, invalid;
    private CanonicalRegistrationPreparedReader(string directory, string digest, RegistrationPreparedDescriptor descriptor)
    {
        this.directory = directory; Digest = digest; this.descriptor = descriptor;
        paths = descriptor.Paths.ToDictionary(p => p.PathId, StringComparer.Ordinal);
    }
    public string Digest { get; }
    public RegistrationPreparedDescriptor Descriptor => descriptor with
    {
        Views = descriptor.Views.ToArray(),
        Paths = descriptor.Paths.Select(p => p with { PageDigests = p.PageDigests.ToArray() }).ToArray(),
        PageDigests = descriptor.PageDigests.ToArray(),
    };
    public long CachedBytes => Interlocked.Read(ref cachedBytes);
    public RegistrationPreparedReaderStatistics Statistics => new(Interlocked.Read(ref pageReads), Interlocked.Read(ref cacheHits), Interlocked.Read(ref rowsMaterialized), CachedBytes);
    public static async Task<CanonicalRegistrationPreparedReader> OpenAsync(string directory, string expectedDigest,
        RegistrationNavigationContext expectedContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedContext);
        directory = Path.GetFullPath(directory);
        if (!CanonicalRegistrationEncoding.IsDigest(expectedDigest) || Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)) != expectedDigest)
            throw new InvalidDataException("Candidate generation directory does not match the expected digest.");
        RejectLinks(directory);
        var bytes = await ReadBoundedAsync(Path.Combine(directory, "descriptor.json"), 64 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        if (CanonicalRegistrationEncoding.Digest(bytes) != expectedDigest) throw new InvalidDataException("Candidate descriptor digest mismatch.");
        var descriptor = JsonSerializer.Deserialize<RegistrationPreparedDescriptor>(bytes, CanonicalRegistrationEncoding.Json)
            ?? throw new InvalidDataException("Candidate descriptor is empty.");
        if (descriptor.Paths is null || descriptor.Views is null || descriptor.PageDigests is null || descriptor.FormatVersion != 1 || descriptor.Context != expectedContext || descriptor.PublicationState != CanonicalRegistrationEncoding.NotPublished ||
            !CanonicalRegistrationEncoding.IsDigest(descriptor.CandidateDigest) || !CanonicalRegistrationEncoding.IsDigest(descriptor.ChecklistDigest))
            throw new InvalidDataException("Candidate generation coordinates mismatch.");
        if (descriptor.Paths.Select(p => p.PathId).Distinct(StringComparer.Ordinal).Count() != descriptor.Paths.Length ||
            descriptor.Views.Select(v => v.Id).Distinct(StringComparer.Ordinal).Count() != descriptor.Views.Length)
            throw new InvalidDataException("Candidate contains duplicate paths or views.");
        var entries = descriptor.Paths.ToDictionary(p => p.PathId, StringComparer.Ordinal);
        var pageDigests = descriptor.PageDigests.ToHashSet(StringComparer.Ordinal);
        foreach (var path in descriptor.Paths)
            if (string.IsNullOrEmpty(path.PathId) || !CanonicalRegistrationEncoding.IsDigest(path.NodeDigest) || path.PageDigests is null || path.PageDigests.Length == 0 ||
                path.PageDigests.Any(d => !CanonicalRegistrationEncoding.IsDigest(d) || !pageDigests.Contains(d)) ||
                (path.ParentPathId is not null && (!entries.TryGetValue(path.ParentPathId, out var parent) || parent.Selector != path.Selector)) ||
                (path.Selectable && path.EntityId is null))
                throw new InvalidDataException("Candidate path directory is inconsistent.");
        if (descriptor.PageDigests.Any(d => !CanonicalRegistrationEncoding.IsDigest(d)) || descriptor.Views.Any(v => !entries.TryGetValue(v.RootPathId, out var root) || root.ParentPathId is not null || root.Selector != v.Selector))
            throw new InvalidDataException("Candidate view directory is inconsistent.");
        foreach (var entry in descriptor.Paths)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal); var cursor = entry;
            while (cursor.ParentPathId is not null)
            {
                if (!seen.Add(cursor.PathId)) throw new InvalidDataException("Candidate parent cycle.");
                cursor = entries[cursor.ParentPathId];
            }
        }
        return new(directory, expectedDigest, descriptor);
    }
    public async Task<RegistrationPreparedPage> GetChildrenAsync(string pathId, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var path = Resolve(pathId); var offset = 0;
        if (cursor is not null)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 3 || parts[0] != Digest || parts[1] != CanonicalRegistrationEncoding.Digest(CanonicalRegistrationEncoding.Bytes(pathId)) ||
                !int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out offset) || offset <= 0 || offset >= path.PageDigests.Length)
                throw new InvalidDataException("Invalid or stale continuation cursor.");
        }
        var page = await ReadAsync<RegistrationPreparedPage>(path.PageDigests[offset], cancellationToken).ConfigureAwait(false);
        if (page.PathId != pathId || page.Current.PathId != pathId || page.Children.Length > PageSize || !Matches(page.Current, path) ||
            page.Children.Any(row => !paths.TryGetValue(row.PathId, out var entry) || entry.ParentPathId != pathId || !Matches(row, entry)))
        { Interlocked.Exchange(ref invalid, 1); throw new InvalidDataException("Candidate page is inconsistent with its directory."); }
        var next = offset + 1 < path.PageDigests.Length ? MakeCursor(pathId, offset + 1) : null;
        return page with { Current = Copy(page.Current), Children = page.Children.Select(Copy).ToArray(), ContinuationCursor = next };
    }
    public async Task<RegistrationNavigationRow?> GetParentAsync(string pathId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = Resolve(pathId); return entry.ParentPathId is null ? null : Copy((await NodeAsync(Resolve(entry.ParentPathId), cancellationToken).ConfigureAwait(false)).Row);
    }
    public Task<bool> ValidateSelectionAsync(string pathId, string entityId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Check();
        return Task.FromResult(paths.TryGetValue(pathId, out var entry) && entry.Selectable && entry.EntityId == entityId);
    }
    public async Task<RegistrationEvidence[]> GetEvidenceAsync(string pathId, CancellationToken cancellationToken = default) =>
        (await NodeAsync(Resolve(pathId), cancellationToken).ConfigureAwait(false)).Evidence.ToArray();
    private async Task<RegistrationPreparedNode> NodeAsync(RegistrationPreparedPath entry, CancellationToken ct)
    {
        var node = await ReadAsync<RegistrationPreparedNode>(entry.NodeDigest, ct).ConfigureAwait(false);
        if (!Matches(node.Row, entry) || !node.Row.EvidenceIds.Order(StringComparer.Ordinal).SequenceEqual(node.Evidence.Select(e => e.Id).Order(StringComparer.Ordinal)))
        { Interlocked.Exchange(ref invalid, 1); throw new InvalidDataException("Candidate node mismatch."); }
        return node;
    }
    private static bool Matches(RegistrationNavigationRow row, RegistrationPreparedPath entry) => row.PathId == entry.PathId && row.ParentPathId == entry.ParentPathId && row.Selector == entry.Selector && row.EntityId == entry.EntityId && row.Selectable == entry.Selectable;
    private static RegistrationNavigationRow Copy(RegistrationNavigationRow row) => row with { EvidenceIds = row.EvidenceIds.ToArray() };
    private string MakeCursor(string path, int offset) => $"{Digest}:{CanonicalRegistrationEncoding.Digest(CanonicalRegistrationEncoding.Bytes(path))}:{offset.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    private RegistrationPreparedPath Resolve(string path) { Check(); return paths.TryGetValue(path, out var entry) ? entry : throw new KeyNotFoundException("Prepared path not found in this generation."); }
    private void Check() { ObjectDisposedException.ThrowIf(disposed != 0, this); if (invalid != 0) throw new InvalidDataException("Candidate reader was invalidated."); }
    private async Task<T> ReadAsync<T>(string digest, CancellationToken ct) where T : class
    {
        Check(); await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Check();
            if (cache.TryGetValue(digest, out var hit))
            { usage.Remove(hit.Usage); usage.AddLast(hit.Usage); Interlocked.Increment(ref cacheHits); return (T)hit.Value; }
            var bytes = await ReadBoundedAsync(Path.Combine(directory, digest + ".json"), 4 * 1024 * 1024, ct).ConfigureAwait(false);
            if (CanonicalRegistrationEncoding.Digest(bytes) != digest) throw new InvalidDataException("Candidate page digest mismatch.");
            var value = JsonSerializer.Deserialize<T>(bytes, CanonicalRegistrationEncoding.Json) ?? throw new InvalidDataException("Candidate page is empty.");
            Interlocked.Increment(ref pageReads);
            if (value is RegistrationPreparedPage page) Interlocked.Add(ref rowsMaterialized, page.Children.Length);
            var charge = bytes.LongLength * 8 + 1024;
            while (cachedBytes + charge > MaximumCachedBytes && usage.First is { } first)
            { var old = cache[first.Value]; cache.Remove(first.Value); usage.RemoveFirst(); Interlocked.Add(ref cachedBytes, -old.Charge); }
            if (charge <= MaximumCachedBytes) { var item = usage.AddLast(digest); cache.Add(digest, (value, charge, item)); Interlocked.Add(ref cachedBytes, charge); }
            return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { Interlocked.Exchange(ref invalid, 1); throw; }
        finally { gate.Release(); }
    }
    internal static void RejectLinks(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null) { if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Candidate paths cannot use reparse points."); current = current.Parent; }
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken ct)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Candidate file cannot be a reparse point.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length <= 0 || stream.Length > maximum) throw new InvalidDataException("Candidate file size outside bounds.");
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false); return bytes;
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; gate.Wait(); try { cache.Clear(); usage.Clear(); Interlocked.Exchange(ref cachedBytes, 0); } finally { gate.Release(); } }
}
