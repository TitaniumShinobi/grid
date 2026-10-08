using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>Projects immutable registration-prepared pages (level frame, level-slotted entities) for selector queries.</summary>
public sealed class RegistrationPreparedNavigationComposer
{
    private static readonly CanonicalOrganizationalSemanticId PreparedPathSemantic =
        new("grid.selector.location.registration-prepared-path");

    private readonly CanonicalRegistrationPreparedReader reader;
    private readonly PreparedCanonicalGenerationDescriptor canonicalDescriptor;
    private readonly string locationRootPathId;
    private readonly Dictionary<string, CanonicalNavigationPathId> registrationToCanonical = new(StringComparer.Ordinal);
    private readonly Dictionary<CanonicalNavigationPathId, string> canonicalToRegistration = new();
    private readonly Dictionary<string, RegistrationPreparedPath> pathEntries;
    private bool mapsBuilt;

    public RegistrationPreparedNavigationComposer(
        CanonicalRegistrationPreparedReader reader,
        PreparedCanonicalGenerationDescriptor canonicalDescriptor)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        this.canonicalDescriptor = canonicalDescriptor ?? throw new ArgumentNullException(nameof(canonicalDescriptor));
        var locationView = reader.Descriptor.Views.FirstOrDefault(value =>
            value.Selector == "Location" && value.Dimension is null)
            ?? throw new InvalidDataException("Registration prepared navigation lacks the primary Location view.");
        locationRootPathId = locationView.RootPathId;
        pathEntries = reader.Descriptor.Paths
            .Where(value => value.Selector == "Location")
            .ToDictionary(value => value.PathId, StringComparer.Ordinal);
    }

    public CanonicalNavigationPathId RootPath
    {
        get { EnsureMapsBuilt(); return registrationToCanonical[locationRootPathId]; }
    }

    public async Task<PreparedCanonicalNavigationPage?> QueryAsync(
        CanonicalNavigationPathId? currentPath,
        string? continuationCursor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureMapsBuilt();
        string? registrationPath;
        if (currentPath is null) registrationPath = locationRootPathId;
        else if (!canonicalToRegistration.TryGetValue(currentPath.Value, out registrationPath)) return null;

        var page = await reader.GetChildrenAsync(registrationPath, continuationCursor, cancellationToken).ConfigureAwait(false);
        var canonicalCurrent = registrationToCanonical[page.Current.PathId];
        var parentPath = page.Current.ParentPathId is null
            ? (CanonicalNavigationPathId?)null
            : registrationToCanonical[page.Current.ParentPathId];
        var rootPath = registrationToCanonical[locationRootPathId];
        var currentNode = ToNavigationNode(page.Current, canonicalCurrent, parentPath);
        var children = page.Children
            .Select(child => ToNavigationNode(
                child,
                registrationToCanonical[child.PathId],
                child.ParentPathId is null ? null : registrationToCanonical[child.ParentPathId]))
            .ToImmutableArray();
        var result = new CanonicalSelectorResult(
            canonicalDescriptor.CatalogRevisionId,
            canonicalDescriptor.CatalogCompositionId,
            KnowledgeKind.Location,
            CanonicalSelectorProjectionPolicy.LocationPrepared.Id,
            CanonicalSelectorProjectionPolicy.LocationPrepared.ExactVersion,
            rootPath,
            canonicalCurrent,
            parentPath,
            currentNode,
            children,
            KnowledgeCoverageState.Partial,
            canonicalDescriptor.TerminologyLocale);
        return new(result, page.ContinuationCursor);
    }

    public string? GetDisplayAnchor(CanonicalNavigationPathId path) =>
        GetDisplayAnchorAsync(path).GetAwaiter().GetResult();

    private async Task<string?> GetDisplayAnchorAsync(CanonicalNavigationPathId path)
    {
        EnsureMapsBuilt();
        if (!canonicalToRegistration.TryGetValue(path, out var registrationPath)) return null;
        if (!pathEntries.TryGetValue(registrationPath, out var entry)) return null;
        if (entry.ParentPathId is null) return "Location";
        var page = await reader.GetChildrenAsync(entry.ParentPathId, null).ConfigureAwait(false);
        return page.Children.FirstOrDefault(value => value.PathId == registrationPath)?.Label ?? page.Current.Label;
    }

    public bool ValidateSelection(CanonicalNavigationPathId path, KnowledgeRecordId recordId)
    {
        EnsureMapsBuilt();
        if (!canonicalToRegistration.TryGetValue(path, out var registrationPath)) return false;
        return reader.ValidateSelectionAsync(registrationPath, recordId.Value).GetAwaiter().GetResult();
    }

    private void EnsureMapsBuilt()
    {
        if (mapsBuilt) return;
        foreach (var pathId in pathEntries.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            var segments = BuildSegments(pathId);
            var canonicalPath = CanonicalNavigationPathId.DeriveV1(
                canonicalDescriptor.CatalogRevisionId,
                canonicalDescriptor.CatalogCompositionId,
                CanonicalSelectorProjectionPolicy.LocationPrepared.Id,
                CanonicalSelectorProjectionPolicy.LocationPrepared.ExactVersion,
                KnowledgeKind.Location,
                segments);
            registrationToCanonical[pathId] = canonicalPath;
            canonicalToRegistration[canonicalPath] = pathId;
        }
        mapsBuilt = true;
    }

    private ImmutableArray<CanonicalNavigationNodeId> BuildSegments(string registrationPathId)
    {
        var chain = new List<string>();
        var cursor = registrationPathId;
        while (true)
        {
            chain.Add(cursor);
            var parent = pathEntries[cursor].ParentPathId;
            if (parent is null) break;
            cursor = parent;
        }
        chain.Reverse();
        var segments = ImmutableArray.CreateBuilder<CanonicalNavigationNodeId>(chain.Count);
        foreach (var pathId in chain)
        {
            var entry = pathEntries[pathId];
            if (entry.EntityId is { } entityId)
                segments.Add(CanonicalNavigationNodeId.ForCanonicalRecord(new KnowledgeRecordId(entityId)));
            else
            {
                var organizationalNodeId = CanonicalOrganizationalNodeId.DeriveV1(
                    CanonicalSelectorProjectionPolicy.LocationPrepared.Id,
                    CanonicalSelectorProjectionPolicy.LocationPrepared.ExactVersion,
                    KnowledgeKind.Location,
                    PreparedPathSemantic,
                    SourceNativeIdentifier.FromExactUtf8("grid.registration.prepared", "path", pathId));
                segments.Add(CanonicalNavigationNodeId.ForOrganizationalNode(organizationalNodeId));
            }
        }
        return segments.ToImmutable();
    }

    private static CanonicalNavigationNode ToNavigationNode(
        RegistrationNavigationRow row,
        CanonicalNavigationPathId path,
        CanonicalNavigationPathId? parentPath)
    {
        KnowledgeRecordId? recordId = row.EntityId is not null ? new KnowledgeRecordId(row.EntityId) : null;
        var nodeKind = recordId is null
            ? CanonicalNavigationNodeKind.Organizational
            : CanonicalNavigationNodeKind.CanonicalRecordReference;
        CanonicalOrganizationalNodeId? organizationalNodeId = null;
        if (nodeKind == CanonicalNavigationNodeKind.Organizational)
        {
            organizationalNodeId = CanonicalOrganizationalNodeId.DeriveV1(
                CanonicalSelectorProjectionPolicy.LocationPrepared.Id,
                CanonicalSelectorProjectionPolicy.LocationPrepared.ExactVersion,
                KnowledgeKind.Location,
                PreparedPathSemantic,
                SourceNativeIdentifier.FromExactUtf8("grid.registration.prepared", "path", row.PathId));
        }
        return new CanonicalNavigationNode(
            nodeKind == CanonicalNavigationNodeKind.Organizational
                ? CanonicalNavigationNodeId.ForOrganizationalNode(organizationalNodeId!.Value)
                : CanonicalNavigationNodeId.ForCanonicalRecord(recordId!.Value),
            path,
            parentPath,
            0,
            nodeKind,
            organizationalNodeId,
            nodeKind == CanonicalNavigationNodeKind.Organizational ? PreparedPathSemantic : null,
            recordId,
            row.Label,
            recordId is null ? CanonicalNavigationDisplayKind.PolicyLabel : CanonicalNavigationDisplayKind.CanonicalTerminology,
            [],
            null,
            row.Selectable,
            row.CanDescend,
            false);
    }
}
