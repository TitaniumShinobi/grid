using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Immutable, authenticated local navigation reader. It has no catalog loader, adapter,
/// registration, network or projection capability. Only requested pages are materialized.
/// </summary>
public sealed class PreparedCanonicalNavigationStore : IDisposable
{
    public const int PageSize = 64;
    public const long MaximumCachedBytes = 32L * 1024 * 1024;
    public const string PublicationFileName = "current.receipt";
    internal const int MaximumPageBytes = 4 * 1024 * 1024;
    internal const int MaximumDirectoryBytes = 16 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string generationPath;
    private readonly FrozenDictionary<(KnowledgeKind Kind, CanonicalNavigationPathId Path), PreparedCanonicalPathEntry> paths;
    private readonly FrozenDictionary<string, PreparedCanonicalPageEntry> pages;
    private readonly FrozenDictionary<KnowledgeKind, PreparedCanonicalKindDescriptor> kinds;
    private readonly object cacheGate = new();
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly Dictionary<string, (object Value, long Charge, LinkedListNode<string> Usage)> cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> usage = [];
    private long cachedBytes;
    private long pageReads;
    private long cacheHits;
    private long rowsMaterialized;
    private int disposed;
    private int invalid;

    private PreparedCanonicalNavigationStore(string root, string generationId, PreparedCanonicalDirectory directory)
    {
        generationPath = Path.Combine(root, generationId);
        GenerationId = generationId;
        Descriptor = directory.Descriptor;
        paths = directory.Paths.ToFrozenDictionary(value => (value.Kind, value.PathId));
        pages = directory.Pages.ToFrozenDictionary(value => value.Id, StringComparer.Ordinal);
        kinds = Descriptor.Kinds.ToFrozenDictionary(value => value.KnowledgeKind);
    }

    public PreparedCanonicalGenerationDescriptor Descriptor { get; }
    public string GenerationId { get; }
    public bool IsInvalid => Volatile.Read(ref invalid) != 0;
    public PreparedCanonicalNavigationStatistics Statistics => new(
        Interlocked.Read(ref pageReads), Interlocked.Read(ref cacheHits), Interlocked.Read(ref rowsMaterialized),
        Interlocked.Read(ref cachedBytes), MaximumCachedBytes);

    public static async Task<PreparedCanonicalNavigationStore> OpenAsync(
        string root,
        CatalogPackageId expectedPackage,
        CanonicalTerminologyLocalePreference locale,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(locale);
        root = Path.GetFullPath(root);
        RejectReparsePoint(root);
        var protectedReceipt = await ReadBoundedAsync(Path.Combine(root, PublicationFileName), 64 * 1024,
            cancellationToken).ConfigureAwait(false);
        var receiptBytes = PreparedCanonicalReceiptProtection.Unprotect(protectedReceipt);
        try
        {
            var receipt = JsonSerializer.Deserialize<PreparedCanonicalPublicationReceipt>(receiptBytes, Json)
                ?? throw new InvalidDataException("Prepared publication receipt is empty.");
            if (receipt.FormatVersion != 1 || receipt.PackageId != expectedPackage ||
                !IsDigest(receipt.DirectorySha256) || !IsDigest(receipt.SourceStoreSha256))
                throw new InvalidDataException("Prepared publication receipt does not match the runtime binding.");
            var generation = Path.Combine(root, receipt.DirectorySha256);
            RejectReparsePoint(generation);
            var bytes = await ReadBoundedAsync(Path.Combine(generation, "directory.json"), MaximumDirectoryBytes,
                cancellationToken).ConfigureAwait(false);
            if (Digest(bytes) != receipt.DirectorySha256)
                throw new InvalidDataException("Prepared directory authentication failed.");
            var directory = JsonSerializer.Deserialize<PreparedCanonicalDirectory>(bytes, Json)
                ?? throw new InvalidDataException("Prepared directory is empty.");
            ValidateDirectory(directory, expectedPackage, receipt.SourceStoreSha256, locale);
            return new(root, receipt.DirectorySha256, directory);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Prepared navigation metadata is malformed.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Prepared navigation metadata has invalid coordinates.", exception);
        }
        finally { CryptographicOperations.ZeroMemory(receiptBytes); }
    }

    public async Task<PreparedCanonicalNavigationPage> ReadLevelAsync(
        KnowledgeKind kind,
        CanonicalNavigationPathId? path = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        EnsureReadable();
        cancellationToken.ThrowIfCancellationRequested();
        if (!kinds.TryGetValue(kind, out var kindDescriptor))
            throw new ArgumentException("Unknown prepared selector kind.", nameof(kind));
        var actualPath = path ?? kindDescriptor.RootPathId;
        if (!paths.TryGetValue((kind, actualPath), out var entry))
            throw new ArgumentException("The path does not belong to this prepared selector generation.", nameof(path));
        var offset = ParseCursor(cursor, kind, actualPath, entry.PageIds.Length);
        var pageId = entry.PageIds[offset];
        var result = await ReadPageAsync<CanonicalSelectorResult>(pageId, value =>
        {
            if (value.CatalogRevisionId != Descriptor.CatalogRevisionId ||
                value.CatalogCompositionId != Descriptor.CatalogCompositionId ||
                value.KnowledgeKind != kind || value.CurrentPathId != actualPath ||
                value.ParentPathId != entry.ParentPathId || value.CurrentNode.PathId != actualPath ||
                value.RootPathId != kindDescriptor.RootPathId ||
                value.ProjectionPolicyId != kindDescriptor.ProjectionPolicyId ||
                value.ExactProjectionPolicyVersion != kindDescriptor.ProjectionPolicyVersion ||
                !SameLocale(value.TerminologyLocale, Descriptor.TerminologyLocale) ||
                value.ImmediateChildren.IsDefault || value.ImmediateChildren.Length > PageSize ||
                value.CurrentNode.KnowledgeRecordId != entry.RecordId || value.CurrentNode.IsSelectable != entry.IsSelectable)
                throw new InvalidDataException("Prepared page coordinates do not match its authenticated directory.");
            foreach (var child in value.ImmediateChildren)
                if (!paths.TryGetValue((kind, child.PathId), out var childEntry) ||
                    child.ParentPathId != actualPath || childEntry.ParentPathId != actualPath ||
                    childEntry.RecordId != child.KnowledgeRecordId || childEntry.IsSelectable != child.IsSelectable)
                    throw new InvalidDataException("Prepared child coordinates do not match their directory.");
        }, cancellationToken).ConfigureAwait(false);
        EnsureReadable();
        return new(result, offset + 1 < entry.PageIds.Length ? CreateCursor(kind, actualPath, offset + 1) : null);
    }

    public async Task<CanonicalNavigationNode> ReadNodeAsync(KnowledgeKind kind,
        CanonicalNavigationPathId path, CancellationToken cancellationToken = default)
    {
        EnsureReadable();
        cancellationToken.ThrowIfCancellationRequested();
        if (!paths.TryGetValue((kind, path), out var entry))
            throw new ArgumentException("Node path is absent from this prepared generation.", nameof(path));
        var result = await ReadPageAsync<CanonicalNavigationNode>(entry.NodePageId, node =>
        {
            if (node.PathId != path || node.ParentPathId != entry.ParentPathId ||
                node.KnowledgeRecordId != entry.RecordId || node.IsSelectable != entry.IsSelectable)
                throw new InvalidDataException("Prepared node does not match its authenticated path.");
        }, cancellationToken).ConfigureAwait(false);
        EnsureReadable();
        return result;
    }

    public Task<bool> ValidateSelectionAsync(CanonicalSelectorSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        EnsureReadable();
        cancellationToken.ThrowIfCancellationRequested();
        var valid = selection.CatalogRevisionId == Descriptor.CatalogRevisionId &&
            selection.CatalogCompositionId == Descriptor.CatalogCompositionId &&
            kinds.TryGetValue(selection.KnowledgeKind, out var kind) &&
            selection.ProjectionPolicyId == kind.ProjectionPolicyId &&
            selection.ProjectionPolicyVersion == kind.ProjectionPolicyVersion;
        if (!valid) return Task.FromResult(false);
        if (selection.SelectionKind == CanonicalSelectorSelectionKind.OtherContext)
            return Task.FromResult(!string.IsNullOrEmpty(selection.UnresolvedOtherContextId) &&
                selection.SelectedPathId is null && selection.KnowledgeRecordId is null);
        return Task.FromResult(selection.SelectionKind == CanonicalSelectorSelectionKind.CanonicalRecord &&
            selection.SelectedPathId is { } path && selection.KnowledgeRecordId is { } record &&
            paths.TryGetValue((selection.KnowledgeKind, path), out var entry) &&
            entry.IsSelectable && entry.RecordId == record);
    }

    private async Task<T> ReadPageAsync<T>(string id, Action<T> validate, CancellationToken cancellationToken) where T : class
    {
        await readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureReadable();
            lock (cacheGate)
            {
                if (cache.TryGetValue(id, out var found))
                {
                    usage.Remove(found.Usage);
                    usage.AddLast(found.Usage);
                    Interlocked.Increment(ref cacheHits);
                    if (found.Value is not T typed)
                        throw new InvalidDataException("Prepared page type differs from its retained immutable contract.");
                    return typed;
                }
            }
            var page = pages[id];
            T value;
            try
            {
                var bytes = await ReadBoundedAsync(Path.Combine(generationPath, id + ".page.json"),
                    page.ByteLength, cancellationToken).ConfigureAwait(false);
                if (bytes.Length != page.ByteLength || Digest(bytes) != page.Sha256)
                    throw new InvalidDataException("Prepared page authentication failed.");
                cancellationToken.ThrowIfCancellationRequested();
                value = JsonSerializer.Deserialize<T>(bytes, Json)
                    ?? throw new InvalidDataException("Prepared page is empty.");
                validate(value);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                UnauthorizedAccessException or JsonException or ArgumentException)
            {
                MarkInvalid();
                throw new InvalidDataException("Prepared page cannot be read with its authenticated content.", exception);
            }
            EnsureReadable();
            Interlocked.Increment(ref pageReads);
            Interlocked.Add(ref rowsMaterialized, value is CanonicalSelectorResult result ? result.ImmediateChildren.Length + 1 : 1);
            // The retained cache owns decoded immutable contracts, never JSON buffers. Charge
            // sixteen times serialized size plus a fixed allowance for object/array overhead.
            // Oversized pages remain usable but are not retained in this cache.
            var charge = checked(page.ByteLength * 16L + 1024);
            lock (cacheGate)
            {
                EnsureReadable();
                while (cachedBytes + charge > MaximumCachedBytes && usage.First is { } oldest)
                {
                    cachedBytes -= cache[oldest.Value].Charge;
                    cache.Remove(oldest.Value);
                    usage.RemoveFirst();
                }
                if (charge <= MaximumCachedBytes)
                {
                    cache.Add(id, (value, charge, usage.AddLast(id)));
                    cachedBytes += charge;
                }
            }
            return value;
        }
        finally { readGate.Release(); }
    }

    private string CreateCursor(KnowledgeKind kind, CanonicalNavigationPathId path, int offset) =>
        $"{GenerationId}/{(int)kind}/{Digest(Encoding.UTF8.GetBytes(path.Value))}/{offset}";

    private int ParseCursor(string? cursor, KnowledgeKind kind, CanonicalNavigationPathId path, int count)
    {
        if (cursor is null) return 0;
        var parts = cursor.Split('/');
        if (parts.Length != 4 || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
            offset <= 0 || offset >= count || cursor != CreateCursor(kind, path, offset))
            throw new ArgumentException("Continuation cursor does not belong to this generation and level.", nameof(cursor));
        return offset;
    }

    private static void ValidateDirectory(PreparedCanonicalDirectory directory, CatalogPackageId package,
        string sourceDigest, CanonicalTerminologyLocalePreference locale)
    {
        var descriptor = directory.Descriptor;
        if (descriptor is null || descriptor.FormatVersion != 1 || !descriptor.IsBaseGameOnly ||
            descriptor.PackageId != package || descriptor.SourceStoreSha256 != sourceDigest ||
            descriptor.SharedLibraryRevision < 0 || descriptor.ValidationStatus == CatalogValidationStatus.Rejected ||
            !Enum.IsDefined(descriptor.ValidationStatus) || !SameLocale(descriptor.TerminologyLocale, locale) ||
            descriptor.Kinds.IsDefault || descriptor.Kinds.Length != Enum.GetValues<KnowledgeKind>().Length ||
            descriptor.Kinds.Select(value => value.KnowledgeKind).Distinct().Count() != descriptor.Kinds.Length ||
            directory.Paths.IsDefaultOrEmpty || directory.Paths.Length > 100_000 || directory.Pages.IsDefaultOrEmpty)
            throw new InvalidDataException("Prepared directory metadata is invalid or incompatible.");
        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in directory.Pages)
            if (!IsDigest(page.Id) || page.Sha256 != page.Id || page.ByteLength is <= 0 or > MaximumPageBytes ||
                !pageIds.Add(page.Id)) throw new InvalidDataException("Prepared page directory is invalid.");
        var pathIds = new HashSet<(KnowledgeKind, CanonicalNavigationPathId)>();
        foreach (var path in directory.Paths)
            if (!Enum.IsDefined(path.Kind) || !pathIds.Add((path.Kind, path.PathId)) ||
                path.PageIds.IsDefaultOrEmpty || path.PageIds.Any(id => !pageIds.Contains(id)) ||
                !pageIds.Contains(path.NodePageId) ||
                (path.IsSelectable && path.RecordId is null))
                throw new InvalidDataException("Prepared path directory is invalid.");
        foreach (var kind in descriptor.Kinds)
            if (!Enum.IsDefined(kind.KnowledgeKind) || !pathIds.Contains((kind.KnowledgeKind, kind.RootPathId)) ||
                string.IsNullOrWhiteSpace(kind.ProjectionPolicyVersion) || kind.PathCount <= 0)
                throw new InvalidDataException("Prepared kind directory is invalid.");
        foreach (var path in directory.Paths)
            if (path.ParentPathId is { } parent && !pathIds.Contains((path.Kind, parent)))
                throw new InvalidDataException("Prepared parent path is missing.");
    }

    internal static bool SameLocale(CanonicalTerminologyLocalePreference? left, CanonicalTerminologyLocalePreference? right) =>
        left is not null && right is not null && left.RequestedLanguageTag == right.RequestedLanguageTag &&
        left.ApprovedLanguageFallbackTags.SequenceEqual(right.ApprovedLanguageFallbackTags);

    internal static bool IsDigest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        RejectReparsePoint(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("Prepared navigation file exceeds its expected size boundary.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Prepared file changed while it was read.");
        return bytes;
    }

    internal static void RejectReparsePoint(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Prepared navigation does not follow reparse points.");
            current = Path.GetDirectoryName(current);
        }
    }

    private void EnsureReadable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (IsInvalid) throw new InvalidDataException("Prepared navigation generation was invalidated by a failed read.");
    }

    private void MarkInvalid()
    {
        Interlocked.Exchange(ref invalid, 1);
        lock (cacheGate) { cache.Clear(); usage.Clear(); Interlocked.Exchange(ref cachedBytes, 0); }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        lock (cacheGate) { cache.Clear(); usage.Clear(); Interlocked.Exchange(ref cachedBytes, 0); }
        // Outstanding readers release their semaphore normally; no wait handle is allocated.
    }
}
