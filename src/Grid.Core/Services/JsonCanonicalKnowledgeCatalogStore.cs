using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.Core.Services;

public sealed class JsonCanonicalKnowledgeCatalogStore : ICanonicalKnowledgeCatalogStore
{
    private const int CurrentSchemaVersion = 2;
    private const int LocationContractSchemaVersion = 3;
    private const int ProjectionContractSchemaVersion = 4;
    private const int CrossSourceAssertionSchemaVersion = 5;
    private const int LegacySchemaVersion = 1;
    private const int MaximumEntitiesPerCollection = 100_000;
    private const long MaximumStoreBytes = 512L * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim gate;
    private readonly string storePath;

    public JsonCanonicalKnowledgeCatalogStore(string storePath)
    {
        this.storePath = Path.GetFullPath(string.IsNullOrWhiteSpace(storePath)
            ? throw new ArgumentException("Canonical catalog store path is required.", nameof(storePath))
            : storePath);
        gate = Gates.GetOrAdd(this.storePath, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<CanonicalCatalogLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<CanonicalCatalogAppendResult> AppendAsync(
        long expectedRevision,
        CanonicalCatalogRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!loaded.IsValid)
                return new(CanonicalCatalogAppendStatus.Failed, loaded.Snapshot.Revision,
                    "The existing canonical catalog is invalid and was not overwritten.");

            CanonicalCatalogRegistration normalized;
            try { normalized = NormalizeAndValidateRegistration(registration, loaded.Snapshot); }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                return new(CanonicalCatalogAppendStatus.Invalid, loaded.Snapshot.Revision, exception.Message);
            }

            var existingRevision = loaded.Snapshot.SourceRevisions.FirstOrDefault(value => value.Id == normalized.SourceRevision.Id);
            if (existingRevision is not null)
            {
                return RegistrationAlreadyPresent(loaded.Snapshot, normalized)
                    ? new(CanonicalCatalogAppendStatus.Unchanged, loaded.Snapshot.Revision,
                        "The identical canonical source revision is already registered.")
                    : new(CanonicalCatalogAppendStatus.Invalid, loaded.Snapshot.Revision,
                        "The source revision identity is already registered with different immutable content.");
            }

            if (loaded.Snapshot.Revision != expectedRevision)
                return new(CanonicalCatalogAppendStatus.Conflict, loaded.Snapshot.Revision,
                    "The canonical catalog changed since this registration was prepared.");

            CanonicalKnowledgeCatalogSnapshot next;
            try
            {
                next = Merge(loaded.Snapshot, normalized) with { Revision = checked(loaded.Snapshot.Revision + 1) };
                ValidateSnapshot(next);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
            {
                return new(CanonicalCatalogAppendStatus.Invalid, loaded.Snapshot.Revision, exception.Message);
            }

            await WriteAtomicAsync(next, cancellationToken).ConfigureAwait(false);
            return new(CanonicalCatalogAppendStatus.Appended, next.Revision,
                "The canonical source revision was appended.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(CanonicalCatalogAppendStatus.Failed, expectedRevision,
                $"The canonical source revision was not appended ({exception.GetType().Name}).");
        }
        finally { gate.Release(); }
    }

    public async Task<CanonicalCatalogImportResult> ImportPackageAsync(
        long expectedRevision,
        CanonicalCatalogPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!loaded.IsValid)
                return new(CanonicalCatalogImportStatus.Failed, loaded.Snapshot.Revision, 0,
                    "The existing canonical catalog is invalid and was not overwritten.");

            var verification = CanonicalCatalogPackageKernel.Verify(package);
            if (!verification.IsStructurallyValid)
                return new(CanonicalCatalogImportStatus.Invalid, loaded.Snapshot.Revision, 0,
                    $"The canonical catalog package is structurally invalid: {string.Join("; ", verification.Issues)}");

            var existingPackage = loaded.Snapshot.ImportedPackages.FirstOrDefault(value => value.Id == package.Id);
            if (existingPackage is not null)
            {
                return PackageEquivalent(existingPackage, package) && PackageContained(loaded.Snapshot, package)
                    ? new(CanonicalCatalogImportStatus.Unchanged, loaded.Snapshot.Revision, 0,
                        "The identical canonical catalog package is already imported.")
                    : new(CanonicalCatalogImportStatus.Invalid, loaded.Snapshot.Revision, 0,
                        "The package identity is already imported with different immutable content.");
            }

            foreach (var incomingRevision in package.Payload.SourceRevisions)
            {
                if (loaded.Snapshot.SourceRevisions.Any(value => value.Id == incomingRevision.Revision.Id) &&
                    !SourceRevisionContentEquivalent(loaded.Snapshot, package.Payload, incomingRevision.Revision.Id))
                    return new(CanonicalCatalogImportStatus.Invalid, loaded.Snapshot.Revision, 0,
                        "An imported source revision cannot be reopened with different immutable canonical content.");
            }

            if (loaded.Snapshot.Revision != expectedRevision)
                return new(CanonicalCatalogImportStatus.Conflict, loaded.Snapshot.Revision, 0,
                    "The canonical catalog changed since this package import was prepared.");

            var newRevisionCount = package.Payload.SourceRevisions.Count(value =>
                loaded.Snapshot.SourceRevisions.All(existing => existing.Id != value.Revision.Id));
            if (newRevisionCount == 0)
                return new(CanonicalCatalogImportStatus.Invalid, loaded.Snapshot.Revision, 0,
                    "A distinct package cannot mutate canonical semantics without introducing an immutable source revision.");
            CanonicalKnowledgeCatalogSnapshot next;
            try
            {
                next = MergePackage(loaded.Snapshot, package) with
                {
                    Revision = checked(loaded.Snapshot.Revision + newRevisionCount),
                };
                ValidateSnapshot(next);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
            {
                return new(CanonicalCatalogImportStatus.Invalid, loaded.Snapshot.Revision, 0, exception.Message);
            }

            await WriteAtomicAsync(next, cancellationToken).ConfigureAwait(false);
            return new(CanonicalCatalogImportStatus.Imported, next.Revision, newRevisionCount,
                "The structurally verified canonical catalog package was imported atomically.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(CanonicalCatalogImportStatus.Failed, expectedRevision, 0,
                $"The canonical catalog package was not imported ({exception.GetType().Name}).");
        }
        finally { gate.Release(); }
    }

    private async Task<CanonicalCatalogLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath)) return new(CanonicalKnowledgeCatalogSnapshot.Empty, []);

        try
        {
            var info = new FileInfo(storePath);
            if (info.Length > MaximumStoreBytes) throw new InvalidDataException("Canonical catalog store exceeds its size limit.");
            var json = await File.ReadAllTextAsync(storePath, cancellationToken).ConfigureAwait(false);
            using var parsed = JsonDocument.Parse(json);
            if (!parsed.RootElement.TryGetProperty("schemaVersion", out var schemaProperty) ||
                !schemaProperty.TryGetInt32(out var schemaVersion))
                throw new InvalidDataException("Canonical catalog store schema is absent.");
            if (schemaVersion < CrossSourceAssertionSchemaVersion &&
                HasNonEmptyCrossSourceState(parsed.RootElement))
                throw new InvalidDataException("A legacy store cannot carry cross-source assertion state.");

            CanonicalKnowledgeCatalogSnapshot snapshot = schemaVersion switch
            {
                LegacySchemaVersion => CreateSnapshot(
                    JsonSerializer.Deserialize<LegacyDocument>(json, JsonOptions)
                    ?? throw new InvalidDataException("Canonical catalog store is empty.")),
                CurrentSchemaVersion => CreateSnapshot(
                    JsonSerializer.Deserialize<CurrentDocument>(json, JsonOptions)
                    ?? throw new InvalidDataException("Canonical catalog store is empty.")),
                LocationContractSchemaVersion => CreateSnapshot(
                    JsonSerializer.Deserialize<LocationContractDocument>(json, JsonOptions)
                    ?? throw new InvalidDataException("Canonical catalog store is empty.")),
                ProjectionContractSchemaVersion => CreateSnapshot(
                    JsonSerializer.Deserialize<ProjectionContractDocument>(json, JsonOptions)
                    ?? throw new InvalidDataException("Canonical catalog store is empty.")),
                CrossSourceAssertionSchemaVersion => CreateSnapshot(
                    JsonSerializer.Deserialize<CrossSourceContractDocument>(json, JsonOptions)
                    ?? throw new InvalidDataException("Canonical catalog store is empty.")),
                _ => throw new InvalidDataException("Canonical catalog store schema is unsupported."),
            };
            ValidateSnapshot(snapshot);
            return new(snapshot, []);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            return new(CanonicalKnowledgeCatalogSnapshot.Empty,
                [$"Canonical catalog could not be loaded ({exception.GetType().Name})."]);
        }
    }

    private static bool HasNonEmptyCrossSourceState(JsonElement root)
    {
        string[] properties =
        [
            "crossSourceAssertions",
            "crossSourceTargetLinkClaims",
            "unresolvedCrossSourceClaimContents",
            "unresolvedCrossSourceEvidenceBindings",
            "unresolvedCrossSourceAssertions",
        ];
        foreach (var name in properties)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0) return true;
        }
        return false;
    }

    private async Task WriteAtomicAsync(CanonicalKnowledgeCatalogSnapshot snapshot, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(storePath)!;
        Directory.CreateDirectory(directory);
        var document = new CrossSourceContractDocument(
            CrossSourceAssertionSchemaVersion,
            snapshot.Revision,
            snapshot.Sources.ToArray(),
            snapshot.Artifacts.ToArray(),
            snapshot.SourceRevisions.ToArray(),
            snapshot.KnowledgeRecords.ToArray(),
            snapshot.TerminologyAssertions.ToArray(),
            snapshot.RelationshipAssertions.ToArray(),
            snapshot.FileEvidenceReceipts.ToArray(),
            snapshot.ReferenceEvidenceReceipts.ToArray(),
            snapshot.EvidenceBindings.ToArray(),
            snapshot.AdapterDescriptors.ToArray(),
            snapshot.AdapterBoundSourceRevisions.ToArray(),
            snapshot.CorrelationEnvelopes.ToArray(),
            snapshot.UnresolvedSourceAssertions.ToArray(),
            snapshot.AcquisitionReceipts.ToArray(),
            snapshot.ArtifactAcquisitionBindings.ToArray(),
            snapshot.ImportedPackages.ToArray(),
            snapshot.SourceNativeLocationTypeAssertions.ToArray(),
            snapshot.LocationSemanticClassificationAssertions.ToArray(),
            snapshot.RecordLifecycleAssertions.ToArray(),
            snapshot.CorrelatedRelationshipEnvelopes.ToArray(),
            snapshot.LocationCoverageReports.ToArray(),
            snapshot.SemanticClassificationAssertions.ToArray(),
            snapshot.RecordContributionAssertions.ToArray(),
            snapshot.OrganizationalValueAssertions.ToArray(),
            snapshot.InstructionAssertions.ToArray(),
            snapshot.InstructionEvidenceBindings.ToArray(),
            snapshot.InstructionConflictGroups.ToArray(),
            snapshot.CrossSourceAssertions.ToArray(),
            snapshot.CrossSourceTargetLinkClaims.ToArray(),
            snapshot.UnresolvedCrossSourceClaimContents.ToArray(),
            snapshot.UnresolvedCrossSourceEvidenceBindings.ToArray(),
            snapshot.UnresolvedCrossSourceAssertions.ToArray());
        var temporary = storePath + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, storePath, true);
    }

    private static CanonicalKnowledgeCatalogSnapshot CreateSnapshot(LegacyDocument document)
    {
        ValidateCommonDocument(
            document.SchemaVersion,
            LegacySchemaVersion,
            document.Revision,
            document.Sources,
            document.Artifacts,
            document.SourceRevisions,
            document.KnowledgeRecords,
            document.TerminologyAssertions,
            document.RelationshipAssertions,
            document.FileEvidenceReceipts,
            document.ReferenceEvidenceReceipts,
            document.EvidenceBindings);
        return new(
            document.Revision,
            document.Sources!.ToImmutableArray(),
            document.Artifacts!.ToImmutableArray(),
            document.SourceRevisions!.ToImmutableArray(),
            document.KnowledgeRecords!.ToImmutableArray(),
            document.TerminologyAssertions!.ToImmutableArray(),
            document.RelationshipAssertions!.ToImmutableArray(),
            document.FileEvidenceReceipts!.ToImmutableArray(),
            document.ReferenceEvidenceReceipts!.ToImmutableArray(),
            document.EvidenceBindings!.ToImmutableArray());
    }

    private static CanonicalKnowledgeCatalogSnapshot CreateSnapshot(CurrentDocument document)
    {
        ValidateCommonDocument(
            document.SchemaVersion,
            CurrentSchemaVersion,
            document.Revision,
            document.Sources,
            document.Artifacts,
            document.SourceRevisions,
            document.KnowledgeRecords,
            document.TerminologyAssertions,
            document.RelationshipAssertions,
            document.FileEvidenceReceipts,
            document.ReferenceEvidenceReceipts,
            document.EvidenceBindings);
        if (document.AdapterDescriptors is null || document.AdapterBoundSourceRevisions is null ||
            document.CorrelationEnvelopes is null || document.UnresolvedSourceAssertions is null ||
            document.AcquisitionReceipts is null || document.ArtifactAcquisitionBindings is null ||
            document.ImportedPackages is null)
            throw new InvalidDataException("Canonical catalog store schema-v2 content is incomplete.");

        return new(
            document.Revision,
            document.Sources!.ToImmutableArray(),
            document.Artifacts!.ToImmutableArray(),
            document.SourceRevisions!.ToImmutableArray(),
            document.KnowledgeRecords!.ToImmutableArray(),
            document.TerminologyAssertions!.ToImmutableArray(),
            document.RelationshipAssertions!.ToImmutableArray(),
            document.FileEvidenceReceipts!.ToImmutableArray(),
            document.ReferenceEvidenceReceipts!.ToImmutableArray(),
            document.EvidenceBindings!.ToImmutableArray())
        {
            AdapterDescriptors = document.AdapterDescriptors.ToImmutableArray(),
            AdapterBoundSourceRevisions = document.AdapterBoundSourceRevisions.ToImmutableArray(),
            CorrelationEnvelopes = document.CorrelationEnvelopes.ToImmutableArray(),
            UnresolvedSourceAssertions = document.UnresolvedSourceAssertions.ToImmutableArray(),
            AcquisitionReceipts = document.AcquisitionReceipts.ToImmutableArray(),
            ArtifactAcquisitionBindings = document.ArtifactAcquisitionBindings.ToImmutableArray(),
            ImportedPackages = document.ImportedPackages.ToImmutableArray(),
        };
    }

    private static CanonicalKnowledgeCatalogSnapshot CreateSnapshot(LocationContractDocument document)
    {
        ValidateCommonDocument(
            document.SchemaVersion,
            LocationContractSchemaVersion,
            document.Revision,
            document.Sources,
            document.Artifacts,
            document.SourceRevisions,
            document.KnowledgeRecords,
            document.TerminologyAssertions,
            document.RelationshipAssertions,
            document.FileEvidenceReceipts,
            document.ReferenceEvidenceReceipts,
            document.EvidenceBindings);
        if (document.AdapterDescriptors is null || document.AdapterBoundSourceRevisions is null ||
            document.CorrelationEnvelopes is null || document.UnresolvedSourceAssertions is null ||
            document.AcquisitionReceipts is null || document.ArtifactAcquisitionBindings is null ||
            document.ImportedPackages is null || document.SourceNativeLocationTypeAssertions is null ||
            document.LocationSemanticClassificationAssertions is null || document.RecordLifecycleAssertions is null ||
            document.CorrelatedRelationshipEnvelopes is null || document.LocationCoverageReports is null)
            throw new InvalidDataException("Canonical catalog store schema-v3 content is incomplete.");

        return new(
            document.Revision,
            document.Sources!.ToImmutableArray(),
            document.Artifacts!.ToImmutableArray(),
            document.SourceRevisions!.ToImmutableArray(),
            document.KnowledgeRecords!.ToImmutableArray(),
            document.TerminologyAssertions!.ToImmutableArray(),
            document.RelationshipAssertions!.ToImmutableArray(),
            document.FileEvidenceReceipts!.ToImmutableArray(),
            document.ReferenceEvidenceReceipts!.ToImmutableArray(),
            document.EvidenceBindings!.ToImmutableArray())
        {
            AdapterDescriptors = document.AdapterDescriptors.ToImmutableArray(),
            AdapterBoundSourceRevisions = document.AdapterBoundSourceRevisions.ToImmutableArray(),
            CorrelationEnvelopes = document.CorrelationEnvelopes.ToImmutableArray(),
            UnresolvedSourceAssertions = document.UnresolvedSourceAssertions.ToImmutableArray(),
            AcquisitionReceipts = document.AcquisitionReceipts.ToImmutableArray(),
            ArtifactAcquisitionBindings = document.ArtifactAcquisitionBindings.ToImmutableArray(),
            ImportedPackages = document.ImportedPackages.ToImmutableArray(),
            SourceNativeLocationTypeAssertions = document.SourceNativeLocationTypeAssertions.ToImmutableArray(),
            LocationSemanticClassificationAssertions = document.LocationSemanticClassificationAssertions.ToImmutableArray(),
            RecordLifecycleAssertions = document.RecordLifecycleAssertions.ToImmutableArray(),
            CorrelatedRelationshipEnvelopes = document.CorrelatedRelationshipEnvelopes.ToImmutableArray(),
            LocationCoverageReports = document.LocationCoverageReports.ToImmutableArray(),
        };
    }

    private static CanonicalKnowledgeCatalogSnapshot CreateSnapshot(ProjectionContractDocument document)
    {
        ValidateCommonDocument(
            document.SchemaVersion,
            ProjectionContractSchemaVersion,
            document.Revision,
            document.Sources,
            document.Artifacts,
            document.SourceRevisions,
            document.KnowledgeRecords,
            document.TerminologyAssertions,
            document.RelationshipAssertions,
            document.FileEvidenceReceipts,
            document.ReferenceEvidenceReceipts,
            document.EvidenceBindings);
        if (document.AdapterDescriptors is null || document.AdapterBoundSourceRevisions is null ||
            document.CorrelationEnvelopes is null || document.UnresolvedSourceAssertions is null ||
            document.AcquisitionReceipts is null || document.ArtifactAcquisitionBindings is null ||
            document.ImportedPackages is null || document.SourceNativeLocationTypeAssertions is null ||
            document.LocationSemanticClassificationAssertions is null || document.RecordLifecycleAssertions is null ||
            document.CorrelatedRelationshipEnvelopes is null || document.LocationCoverageReports is null ||
            document.SemanticClassificationAssertions is null || document.RecordContributionAssertions is null ||
            document.OrganizationalValueAssertions is null || document.InstructionAssertions is null ||
            document.InstructionEvidenceBindings is null || document.InstructionConflictGroups is null)
            throw new InvalidDataException("Canonical catalog store schema-v4 content is incomplete.");

        return new(
            document.Revision,
            document.Sources!.ToImmutableArray(),
            document.Artifacts!.ToImmutableArray(),
            document.SourceRevisions!.ToImmutableArray(),
            document.KnowledgeRecords!.ToImmutableArray(),
            document.TerminologyAssertions!.ToImmutableArray(),
            document.RelationshipAssertions!.ToImmutableArray(),
            document.FileEvidenceReceipts!.ToImmutableArray(),
            document.ReferenceEvidenceReceipts!.ToImmutableArray(),
            document.EvidenceBindings!.ToImmutableArray())
        {
            AdapterDescriptors = document.AdapterDescriptors.ToImmutableArray(),
            AdapterBoundSourceRevisions = document.AdapterBoundSourceRevisions.ToImmutableArray(),
            CorrelationEnvelopes = document.CorrelationEnvelopes.ToImmutableArray(),
            UnresolvedSourceAssertions = document.UnresolvedSourceAssertions.ToImmutableArray(),
            AcquisitionReceipts = document.AcquisitionReceipts.ToImmutableArray(),
            ArtifactAcquisitionBindings = document.ArtifactAcquisitionBindings.ToImmutableArray(),
            ImportedPackages = document.ImportedPackages.ToImmutableArray(),
            SourceNativeLocationTypeAssertions = document.SourceNativeLocationTypeAssertions.ToImmutableArray(),
            LocationSemanticClassificationAssertions = document.LocationSemanticClassificationAssertions.ToImmutableArray(),
            RecordLifecycleAssertions = document.RecordLifecycleAssertions.ToImmutableArray(),
            CorrelatedRelationshipEnvelopes = document.CorrelatedRelationshipEnvelopes.ToImmutableArray(),
            LocationCoverageReports = document.LocationCoverageReports.ToImmutableArray(),
            SemanticClassificationAssertions = document.SemanticClassificationAssertions.ToImmutableArray(),
            RecordContributionAssertions = document.RecordContributionAssertions.ToImmutableArray(),
            OrganizationalValueAssertions = document.OrganizationalValueAssertions.ToImmutableArray(),
            InstructionAssertions = document.InstructionAssertions.ToImmutableArray(),
            InstructionEvidenceBindings = document.InstructionEvidenceBindings.ToImmutableArray(),
            InstructionConflictGroups = document.InstructionConflictGroups.ToImmutableArray(),
        };
    }

    private static CanonicalKnowledgeCatalogSnapshot CreateSnapshot(CrossSourceContractDocument document)
    {
        if (document.SchemaVersion != CrossSourceAssertionSchemaVersion ||
            document.CrossSourceAssertions is null || document.CrossSourceTargetLinkClaims is null ||
            document.UnresolvedCrossSourceClaimContents is null ||
            document.UnresolvedCrossSourceEvidenceBindings is null ||
            document.UnresolvedCrossSourceAssertions is null)
            throw new InvalidDataException("Canonical catalog store schema-v5 content is incomplete.");

        var projection = new ProjectionContractDocument(
            ProjectionContractSchemaVersion,
            document.Revision,
            document.Sources,
            document.Artifacts,
            document.SourceRevisions,
            document.KnowledgeRecords,
            document.TerminologyAssertions,
            document.RelationshipAssertions,
            document.FileEvidenceReceipts,
            document.ReferenceEvidenceReceipts,
            document.EvidenceBindings,
            document.AdapterDescriptors,
            document.AdapterBoundSourceRevisions,
            document.CorrelationEnvelopes,
            document.UnresolvedSourceAssertions,
            document.AcquisitionReceipts,
            document.ArtifactAcquisitionBindings,
            document.ImportedPackages,
            document.SourceNativeLocationTypeAssertions,
            document.LocationSemanticClassificationAssertions,
            document.RecordLifecycleAssertions,
            document.CorrelatedRelationshipEnvelopes,
            document.LocationCoverageReports,
            document.SemanticClassificationAssertions,
            document.RecordContributionAssertions,
            document.OrganizationalValueAssertions,
            document.InstructionAssertions,
            document.InstructionEvidenceBindings,
            document.InstructionConflictGroups);

        return CreateSnapshot(projection) with
        {
            CrossSourceAssertions = document.CrossSourceAssertions.ToImmutableArray(),
            CrossSourceTargetLinkClaims = document.CrossSourceTargetLinkClaims.ToImmutableArray(),
            UnresolvedCrossSourceClaimContents = document.UnresolvedCrossSourceClaimContents.ToImmutableArray(),
            UnresolvedCrossSourceEvidenceBindings = document.UnresolvedCrossSourceEvidenceBindings.ToImmutableArray(),
            UnresolvedCrossSourceAssertions = document.UnresolvedCrossSourceAssertions.ToImmutableArray(),
        };
    }

    private static void ValidateCommonDocument(
        int actualSchemaVersion,
        int expectedSchemaVersion,
        long revision,
        CatalogSourceRecord[]? sources,
        SourceArtifactRecord[]? artifacts,
        CatalogSourceRevisionRecord[]? sourceRevisions,
        CanonicalKnowledgeRecord[]? knowledgeRecords,
        TerminologyAssertion[]? terminologyAssertions,
        RelationshipAssertion[]? relationshipAssertions,
        CatalogFileEvidenceReceipt[]? fileEvidenceReceipts,
        CatalogReferenceEvidenceReceipt[]? referenceEvidenceReceipts,
        EvidenceBinding[]? evidenceBindings)
    {
        if (actualSchemaVersion != expectedSchemaVersion || revision < 0 ||
            sources is null || artifacts is null || sourceRevisions is null || knowledgeRecords is null ||
            terminologyAssertions is null || relationshipAssertions is null || fileEvidenceReceipts is null ||
            referenceEvidenceReceipts is null || evidenceBindings is null)
            throw new InvalidDataException("Canonical catalog store document is incomplete.");
    }

    private static CanonicalCatalogRegistration NormalizeAndValidateRegistration(
        CanonicalCatalogRegistration registration,
        CanonicalKnowledgeCatalogSnapshot current)
    {
        if (registration.SourceNativeLocationTypeAssertions.IsDefault ||
            registration.LocationSemanticClassificationAssertions.IsDefault ||
            registration.RecordLifecycleAssertions.IsDefault)
            throw new InvalidDataException("Frozen Location contract claim collections must be initialized.");
        if (!registration.SourceNativeLocationTypeAssertions.IsEmpty ||
            !registration.LocationSemanticClassificationAssertions.IsEmpty ||
            !registration.RecordLifecycleAssertions.IsEmpty)
            throw new InvalidDataException(
                "Frozen Location contract claims require a structurally verified schema-v4 package import.");
        RequireInitialized(registration.Artifacts, nameof(registration.Artifacts));
        RequireInitialized(registration.SourceRevision.ArtifactIds, nameof(registration.SourceRevision.ArtifactIds));
        RequireInitialized(registration.KnowledgeRecords, nameof(registration.KnowledgeRecords));
        RequireInitialized(registration.TerminologyAssertions, nameof(registration.TerminologyAssertions));
        RequireInitialized(registration.RelationshipAssertions, nameof(registration.RelationshipAssertions));
        RequireInitialized(registration.FileEvidenceReceipts, nameof(registration.FileEvidenceReceipts));
        RequireInitialized(registration.ReferenceEvidenceReceipts, nameof(registration.ReferenceEvidenceReceipts));
        RequireInitialized(registration.EvidenceBindings, nameof(registration.EvidenceBindings));

        var normalized = registration with
        {
            Artifacts = registration.Artifacts.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            SourceRevision = registration.SourceRevision with
            {
                ArtifactIds = registration.SourceRevision.ArtifactIds.OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray(),
            },
            KnowledgeRecords = registration.KnowledgeRecords.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            TerminologyAssertions = registration.TerminologyAssertions
                .OrderBy(value => TerminologySortKey(value), StringComparer.Ordinal).ToImmutableArray(),
            RelationshipAssertions = registration.RelationshipAssertions
                .OrderBy(value => RelationshipSortKey(value), StringComparer.Ordinal).ToImmutableArray(),
            FileEvidenceReceipts = registration.FileEvidenceReceipts.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            ReferenceEvidenceReceipts = registration.ReferenceEvidenceReceipts.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            EvidenceBindings = registration.EvidenceBindings.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
        };

        ValidateRegistration(normalized, current);
        return normalized;
    }

    private static void ValidateRegistration(CanonicalCatalogRegistration registration, CanonicalKnowledgeCatalogSnapshot current)
    {
        if (registration.Source.NativeIdentity is null ||
            CatalogSourceId.DeriveV1(registration.Source.Kind, registration.Source.NativeIdentity) != registration.Source.Id)
            throw new InvalidDataException("Catalog source identity does not match its canonical inputs.");

        EnsureDistinct(registration.Artifacts, value => value.Id, "registration artifacts");
        foreach (var artifact in registration.Artifacts)
            if (SourceArtifactId.DeriveV1(artifact.Digest) != artifact.Id)
                throw new InvalidDataException("Source artifact identity does not match its digest.");

        if (registration.SourceRevision.SourceId != registration.Source.Id)
            throw new InvalidDataException("Source revision does not belong to the registered source.");
        var registrationArtifactIds = registration.Artifacts.Select(value => value.Id).ToImmutableArray();
        if (!registration.SourceRevision.ArtifactIds.SequenceEqual(registrationArtifactIds))
            throw new InvalidDataException("Registration must carry exactly the artifacts identified by its source revision.");
        if (CatalogSourceRevisionId.DeriveV1(
                registration.Source.Id,
                registration.SourceRevision.NativeRevision,
                registration.SourceRevision.ArtifactIds) != registration.SourceRevision.Id)
            throw new InvalidDataException("Source revision identity does not match its canonical inputs.");

        EnsureDistinct(registration.KnowledgeRecords, value => value.Id, "knowledge records");
        foreach (var record in registration.KnowledgeRecords)
        {
            if (record.SourceRevisionId != registration.SourceRevision.Id)
                throw new InvalidDataException("Knowledge record does not belong to the registered source revision.");
            if (NativeRecordIdentityId.DeriveV1(record.GameId, record.NativeIdentity) != record.NativeRecordIdentityId ||
                KnowledgeRecordId.DeriveV1(
                    record.GameId,
                    record.GameVersion,
                    record.ModVersion,
                    record.SourceRevisionId,
                    record.Kind,
                    record.NativeRecordIdentityId) != record.Id)
                throw new InvalidDataException("Knowledge record identity does not match its canonical inputs.");
        }

        EnsureNoDuplicateAssertions(registration.TerminologyAssertions, TerminologyEquivalent, "terminology assertions");
        EnsureNoDuplicateAssertions(registration.RelationshipAssertions, RelationshipEquivalent, "relationship assertions");
        var knownRecordIds = current.KnowledgeRecords.Select(value => value.Id)
            .Concat(registration.KnowledgeRecords.Select(value => value.Id)).ToHashSet();
        foreach (var term in registration.TerminologyAssertions)
            if (term.SourceRevisionId != registration.SourceRevision.Id || !registration.KnowledgeRecords.Any(value => value.Id == term.KnowledgeRecordId))
                throw new InvalidDataException("Terminology must belong to a knowledge record in the registered source revision.");
        foreach (var relationship in registration.RelationshipAssertions)
        {
            if (relationship.SourceRevisionId != registration.SourceRevision.Id ||
                !registration.KnowledgeRecords.Any(value => value.Id == relationship.SubjectKnowledgeRecordId))
                throw new InvalidDataException("Relationship must originate from a knowledge record in the registered source revision.");
            if (relationship.ResolvedTargetKnowledgeRecordId is KnowledgeRecordId target && !knownRecordIds.Contains(target))
                throw new InvalidDataException("Resolved relationship target is not registered.");
        }

        EnsureDistinct(registration.FileEvidenceReceipts, value => value.Id, "file evidence receipts");
        EnsureDistinct(registration.ReferenceEvidenceReceipts, value => value.Id, "reference evidence receipts");
        var evidenceIds = new HashSet<EvidenceReceiptId>();
        foreach (var evidence in registration.FileEvidenceReceipts)
        {
            if (!evidenceIds.Add(evidence.Id) || !FileEvidenceReceiptIdentityMatches(evidence) ||
                evidence.Receipt.SourceRevisionId != registration.SourceRevision.Id ||
                !registrationArtifactIds.Contains(evidence.Receipt.SourceArtifactId) || evidence.Receipt.ObservedAtUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException("File evidence receipt is not valid for the registered source revision.");
        }
        var knownSourceIds = current.Sources.Select(value => value.Id).Append(registration.Source.Id).ToHashSet();
        foreach (var evidence in registration.ReferenceEvidenceReceipts)
        {
            if (!evidenceIds.Add(evidence.Id) || EvidenceReceiptId.DeriveV1(evidence.Receipt) != evidence.Id ||
                evidence.Receipt.SourceRevisionId != registration.SourceRevision.Id ||
                !registrationArtifactIds.Contains(evidence.Receipt.ResponseArtifactId) ||
                !knownSourceIds.Contains(evidence.Receipt.ProviderCatalogSourceId) || evidence.Receipt.RetrievedAtUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Reference evidence receipt is not valid for the registered source revision.");
        }

        EnsureDistinct(registration.EvidenceBindings, value => value.Id, "evidence bindings");
        var knownEvidenceCoordinates = current.FileEvidenceReceipts
            .Select(value => new EvidenceReceiptCoordinate(value.Id, value.Receipt.SourceRevisionId, value.Receipt.SourceFieldPath))
            .Concat(current.ReferenceEvidenceReceipts.Select(value =>
                new EvidenceReceiptCoordinate(value.Id, value.Receipt.SourceRevisionId, value.Receipt.ResponseFieldPath)))
            .Concat(registration.FileEvidenceReceipts.Select(value =>
                new EvidenceReceiptCoordinate(value.Id, value.Receipt.SourceRevisionId, value.Receipt.SourceFieldPath)))
            .Concat(registration.ReferenceEvidenceReceipts.Select(value =>
                new EvidenceReceiptCoordinate(value.Id, value.Receipt.SourceRevisionId, value.Receipt.ResponseFieldPath)))
            .ToImmutableArray();
        foreach (var binding in registration.EvidenceBindings)
        {
            if (!Enum.IsDefined(binding.ClaimKind) ||
                EvidenceBindingId.DeriveV2(
                    binding.EvidenceReceiptId,
                    binding.ClaimKind,
                    binding.KnowledgeRecordId,
                    binding.SourceRevisionId,
                    binding.ClaimLocator,
                    binding.ClaimContentId) != binding.Id ||
                binding.SourceRevisionId != registration.SourceRevision.Id ||
                !registration.KnowledgeRecords.Any(value => value.Id == binding.KnowledgeRecordId) ||
                !knownEvidenceCoordinates.Any(value =>
                    value.Id == binding.EvidenceReceiptId &&
                    value.SourceRevisionId == binding.SourceRevisionId &&
                    string.Equals(value.FieldPath, binding.ClaimLocator, StringComparison.Ordinal)) ||
                !BindingTargetsExactClaim(
                    binding,
                    registration.TerminologyAssertions,
                     registration.RelationshipAssertions,
                     registration.SourceNativeLocationTypeAssertions,
                     registration.LocationSemanticClassificationAssertions,
                     registration.RecordLifecycleAssertions,
                      [],
                      [],
                      [],
                      []))
                throw new InvalidDataException("Evidence binding is invalid for the registered source revision.");
        }

        foreach (var record in registration.KnowledgeRecords)
            if (!registration.EvidenceBindings.Any(value => value.KnowledgeRecordId == record.Id && value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity))
                throw new InvalidDataException("Every knowledge record requires authoritative identity evidence.");
        foreach (var term in registration.TerminologyAssertions)
            if (!registration.EvidenceBindings.Any(value => BindingTargets(value, term)))
                throw new InvalidDataException("Every terminology assertion requires claim-level source evidence.");
        foreach (var relationship in registration.RelationshipAssertions)
            if (!registration.EvidenceBindings.Any(value => BindingTargets(value, relationship)))
                throw new InvalidDataException("Every relationship assertion requires claim-level source evidence.");
    }

    private static CanonicalKnowledgeCatalogSnapshot Merge(
        CanonicalKnowledgeCatalogSnapshot current,
        CanonicalCatalogRegistration registration)
    {
        var sources = MergeById(current.Sources, [registration.Source], value => value.Id, static (left, right) => left == right, "catalog source");
        var artifacts = MergeById(current.Artifacts, registration.Artifacts, value => value.Id, static (left, right) => left == right, "source artifact");
        var revisions = MergeById(current.SourceRevisions, [registration.SourceRevision], value => value.Id, SourceRevisionEquivalent, "source revision");
        var records = MergeById(current.KnowledgeRecords, registration.KnowledgeRecords, value => value.Id, static (left, right) => left == right, "knowledge record");
        var fileEvidence = MergeById(current.FileEvidenceReceipts, registration.FileEvidenceReceipts, value => value.Id, static (left, right) => left == right, "file evidence");
        var referenceEvidence = MergeById(current.ReferenceEvidenceReceipts, registration.ReferenceEvidenceReceipts, value => value.Id, static (left, right) => left == right, "reference evidence");
        var bindings = MergeById(current.EvidenceBindings, registration.EvidenceBindings, value => value.Id, static (left, right) => left == right, "evidence binding");

        return new CanonicalKnowledgeCatalogSnapshot(
            current.Revision,
            sources,
            artifacts,
            revisions,
            records,
            MergeAssertions(current.TerminologyAssertions, registration.TerminologyAssertions, TerminologyEquivalent, TerminologySortKey),
            MergeAssertions(current.RelationshipAssertions, registration.RelationshipAssertions, RelationshipEquivalent, RelationshipSortKey),
            fileEvidence,
            referenceEvidence,
            bindings)
        {
            AdapterDescriptors = current.AdapterDescriptors,
            AdapterBoundSourceRevisions = current.AdapterBoundSourceRevisions,
            CorrelationEnvelopes = current.CorrelationEnvelopes,
            UnresolvedSourceAssertions = current.UnresolvedSourceAssertions,
            AcquisitionReceipts = current.AcquisitionReceipts,
            ArtifactAcquisitionBindings = current.ArtifactAcquisitionBindings,
            ImportedPackages = current.ImportedPackages,
            SourceNativeLocationTypeAssertions = current.SourceNativeLocationTypeAssertions,
            LocationSemanticClassificationAssertions = current.LocationSemanticClassificationAssertions,
            RecordLifecycleAssertions = current.RecordLifecycleAssertions,
            CorrelatedRelationshipEnvelopes = current.CorrelatedRelationshipEnvelopes,
            LocationCoverageReports = current.LocationCoverageReports,
            SemanticClassificationAssertions = current.SemanticClassificationAssertions,
            RecordContributionAssertions = current.RecordContributionAssertions,
            OrganizationalValueAssertions = current.OrganizationalValueAssertions,
            InstructionAssertions = current.InstructionAssertions,
            InstructionEvidenceBindings = current.InstructionEvidenceBindings,
            InstructionConflictGroups = current.InstructionConflictGroups,
            CrossSourceAssertions = current.CrossSourceAssertions,
            CrossSourceTargetLinkClaims = current.CrossSourceTargetLinkClaims,
            UnresolvedCrossSourceClaimContents = current.UnresolvedCrossSourceClaimContents,
            UnresolvedCrossSourceEvidenceBindings = current.UnresolvedCrossSourceEvidenceBindings,
            UnresolvedCrossSourceAssertions = current.UnresolvedCrossSourceAssertions,
        };
    }

    private static CanonicalKnowledgeCatalogSnapshot MergePackage(
        CanonicalKnowledgeCatalogSnapshot current,
        CanonicalCatalogPackage package)
    {
        var payload = package.Payload;
        return new CanonicalKnowledgeCatalogSnapshot(
            current.Revision,
            MergeById(current.Sources, payload.Sources, value => value.Id, static (left, right) => left == right, "catalog source"),
            MergeById(current.Artifacts, payload.Artifacts, value => value.Id, static (left, right) => left == right, "source artifact"),
            MergeById(current.SourceRevisions, payload.SourceRevisions.Select(value => value.Revision).ToImmutableArray(),
                value => value.Id, SourceRevisionEquivalent, "source revision"),
            MergeById(current.KnowledgeRecords, payload.KnowledgeRecords, value => value.Id,
                static (left, right) => left == right, "knowledge record"),
            MergeAssertions(current.TerminologyAssertions, payload.TerminologyAssertions, TerminologyEquivalent, TerminologySortKey),
            MergeAssertions(current.RelationshipAssertions, payload.RelationshipAssertions, RelationshipEquivalent, RelationshipSortKey),
            MergeById(current.FileEvidenceReceipts, payload.FileEvidenceReceipts, value => value.Id,
                static (left, right) => left == right, "file evidence"),
            MergeById(current.ReferenceEvidenceReceipts, payload.ReferenceEvidenceReceipts, value => value.Id,
                static (left, right) => left == right, "reference evidence"),
            MergeById(current.EvidenceBindings, payload.EvidenceBindings, value => value.Id,
                static (left, right) => left == right, "evidence binding"))
        {
            AdapterDescriptors = MergeById(
                current.AdapterDescriptors,
                payload.AdapterDescriptors,
                value => value.RevisionId,
                AdapterDescriptorEquivalent,
                "adapter descriptor"),
            AdapterBoundSourceRevisions = MergeById(
                current.AdapterBoundSourceRevisions,
                payload.SourceRevisions,
                value => value.Revision.Id,
                AdapterBoundRevisionEquivalent,
                "adapter-bound source revision"),
            CorrelationEnvelopes = MergeById(
                current.CorrelationEnvelopes,
                payload.CorrelationEnvelopes,
                value => value.Id,
                CorrelationEquivalent,
                "correlation"),
            UnresolvedSourceAssertions = MergeById(
                current.UnresolvedSourceAssertions,
                payload.UnresolvedSourceAssertions,
                value => value.Id,
                UnresolvedAssertionEquivalent,
                "unresolved source assertion"),
            AcquisitionReceipts = MergeById(
                current.AcquisitionReceipts,
                payload.AcquisitionReceipts,
                value => value.Id,
                AcquisitionReceiptEquivalent,
                "source acquisition receipt"),
            ArtifactAcquisitionBindings = MergeAcquisitionBindings(
                current.ArtifactAcquisitionBindings,
                payload.ArtifactAcquisitionBindings),
            ImportedPackages = MergeById(
                current.ImportedPackages,
                [package],
                value => value.Id,
                PackageEquivalent,
                "imported package"),
            SourceNativeLocationTypeAssertions = MergeById(
                current.SourceNativeLocationTypeAssertions,
                payload.SourceNativeLocationTypeAssertions,
                value => value.Id,
                static (left, right) => left == right,
                "source-native location type assertion"),
            LocationSemanticClassificationAssertions = MergeById(
                current.LocationSemanticClassificationAssertions,
                payload.LocationSemanticClassificationAssertions,
                value => value.Id,
                static (left, right) => left == right,
                "location semantic classification assertion"),
            RecordLifecycleAssertions = MergeById(
                current.RecordLifecycleAssertions,
                payload.RecordLifecycleAssertions,
                value => value.Id,
                static (left, right) => left == right,
                "canonical record lifecycle assertion"),
            CorrelatedRelationshipEnvelopes = MergeById(
                current.CorrelatedRelationshipEnvelopes,
                payload.CorrelatedRelationshipEnvelopes,
                value => value.Id,
                static (left, right) => left == right,
                "correlated relationship envelope"),
            LocationCoverageReports = MergeById(
                current.LocationCoverageReports,
                payload.LocationCoverageReports,
                value => value.Id,
                JsonEquivalent,
                "location coverage report"),
            SemanticClassificationAssertions = MergeById(
                current.SemanticClassificationAssertions,
                payload.SemanticClassificationAssertions,
                value => value.Id,
                JsonEquivalent,
                "semantic classification assertion"),
            RecordContributionAssertions = MergeById(
                current.RecordContributionAssertions,
                payload.RecordContributionAssertions,
                value => value.Id,
                JsonEquivalent,
                "record contribution assertion"),
            OrganizationalValueAssertions = MergeById(
                current.OrganizationalValueAssertions,
                payload.OrganizationalValueAssertions,
                value => value.Id,
                JsonEquivalent,
                "organizational value assertion"),
            InstructionAssertions = MergeById(
                current.InstructionAssertions,
                payload.InstructionAssertions,
                value => value.Id,
                JsonEquivalent,
                "Instruction assertion"),
            InstructionEvidenceBindings = MergeById(
                current.InstructionEvidenceBindings,
                payload.InstructionEvidenceBindings,
                value => value.Id,
                JsonEquivalent,
                "Instruction evidence binding"),
            InstructionConflictGroups = MergeById(
                current.InstructionConflictGroups,
                payload.InstructionConflictGroups,
                value => value.Id,
                JsonEquivalent,
                "Instruction conflict group"),
            CrossSourceAssertions = MergeById(
                current.CrossSourceAssertions,
                payload.CrossSourceAssertions,
                value => value.Id,
                JsonEquivalent,
                "cross-source canonical assertion"),
            CrossSourceTargetLinkClaims = MergeById(
                current.CrossSourceTargetLinkClaims,
                payload.CrossSourceTargetLinkClaims,
                value => value.Id,
                JsonEquivalent,
                "cross-source target-link claim"),
            UnresolvedCrossSourceClaimContents = MergeById(
                current.UnresolvedCrossSourceClaimContents,
                payload.UnresolvedCrossSourceClaimContents,
                value => value.Id,
                JsonEquivalent,
                "unresolved cross-source typed claim"),
            UnresolvedCrossSourceEvidenceBindings = MergeById(
                current.UnresolvedCrossSourceEvidenceBindings,
                payload.UnresolvedCrossSourceEvidenceBindings,
                value => value.Id,
                JsonEquivalent,
                "unresolved cross-source evidence binding"),
            UnresolvedCrossSourceAssertions = MergeById(
                current.UnresolvedCrossSourceAssertions,
                payload.UnresolvedCrossSourceAssertions,
                value => value.Id,
                JsonEquivalent,
                "unresolved cross-source assertion"),
        };
    }

    private static bool RegistrationAlreadyPresent(CanonicalKnowledgeCatalogSnapshot snapshot, CanonicalCatalogRegistration registration)
    {
        var revisionId = registration.SourceRevision.Id;
        return snapshot.Sources.Any(value => value.Id == registration.Source.Id && value == registration.Source) &&
               registration.Artifacts.All(incoming => snapshot.Artifacts.Any(value => value.Id == incoming.Id && value == incoming)) &&
               snapshot.SourceRevisions.Any(value => value.Id == revisionId && SourceRevisionEquivalent(value, registration.SourceRevision)) &&
               EquivalentSet(
                   snapshot.KnowledgeRecords.Where(value => value.SourceRevisionId == revisionId),
                   registration.KnowledgeRecords,
                   static (left, right) => left.Id == right.Id && left == right) &&
               EquivalentSet(
                   snapshot.TerminologyAssertions.Where(value => value.SourceRevisionId == revisionId),
                   registration.TerminologyAssertions,
                   TerminologyEquivalent) &&
               EquivalentSet(
                   snapshot.RelationshipAssertions.Where(value => value.SourceRevisionId == revisionId),
                   registration.RelationshipAssertions,
                   RelationshipEquivalent) &&
               EquivalentSet(
                   snapshot.FileEvidenceReceipts.Where(value => value.Receipt.SourceRevisionId == revisionId),
                   registration.FileEvidenceReceipts,
                   static (left, right) => left.Id == right.Id && left == right) &&
               EquivalentSet(
                   snapshot.ReferenceEvidenceReceipts.Where(value => value.Receipt.SourceRevisionId == revisionId),
                   registration.ReferenceEvidenceReceipts,
                   static (left, right) => left.Id == right.Id && left == right) &&
               EquivalentSet(
                   snapshot.EvidenceBindings.Where(value => value.SourceRevisionId == revisionId),
                   registration.EvidenceBindings,
                   static (left, right) => left.Id == right.Id && left == right);
    }

    private static bool EquivalentSet<T>(
        IEnumerable<T> persisted,
        IEnumerable<T> incoming,
        Func<T, T, bool> equivalent)
    {
        var persistedValues = persisted.ToArray();
        var incomingValues = incoming.ToArray();
        return persistedValues.Length == incomingValues.Length &&
               incomingValues.All(candidate => persistedValues.Any(value => equivalent(value, candidate)));
    }

    private static void ValidateSnapshot(CanonicalKnowledgeCatalogSnapshot snapshot)
    {
        if (snapshot.Revision < 0) throw new InvalidDataException("Canonical catalog revision is invalid.");
        RequireBounded(snapshot.Sources, "catalog sources");
        RequireBounded(snapshot.Artifacts, "source artifacts");
        RequireBounded(snapshot.SourceRevisions, "source revisions");
        RequireBounded(snapshot.KnowledgeRecords, "knowledge records");
        RequireBounded(snapshot.TerminologyAssertions, "terminology assertions");
        RequireBounded(snapshot.RelationshipAssertions, "relationship assertions");
        RequireBounded(snapshot.FileEvidenceReceipts, "file evidence receipts");
        RequireBounded(snapshot.ReferenceEvidenceReceipts, "reference evidence receipts");
        RequireBounded(snapshot.EvidenceBindings, "evidence bindings");
        RequireBounded(snapshot.AdapterDescriptors, "adapter descriptors");
        RequireBounded(snapshot.AdapterBoundSourceRevisions, "adapter-bound source revisions");
        RequireBounded(snapshot.CorrelationEnvelopes, "correlations");
        RequireBounded(snapshot.UnresolvedSourceAssertions, "unresolved source assertions");
        RequireBounded(snapshot.AcquisitionReceipts, "source acquisition receipts");
        RequireBounded(snapshot.ArtifactAcquisitionBindings, "artifact acquisition bindings");
        RequireBounded(snapshot.ImportedPackages, "imported packages");
        RequireBounded(snapshot.SourceNativeLocationTypeAssertions, "source-native location type assertions");
        RequireBounded(snapshot.LocationSemanticClassificationAssertions, "location semantic classifications");
        RequireBounded(snapshot.RecordLifecycleAssertions, "canonical record lifecycle assertions");
        RequireBounded(snapshot.CorrelatedRelationshipEnvelopes, "correlated relationship envelopes");
        RequireBounded(snapshot.LocationCoverageReports, "location coverage reports");
        RequireBounded(snapshot.SemanticClassificationAssertions, "semantic classification assertions");
        RequireBounded(snapshot.RecordContributionAssertions, "record contribution assertions");
        RequireBounded(snapshot.OrganizationalValueAssertions, "organizational value assertions");
        RequireBounded(snapshot.InstructionAssertions, "Instruction assertions");
        RequireBounded(snapshot.InstructionEvidenceBindings, "Instruction evidence bindings");
        RequireBounded(snapshot.InstructionConflictGroups, "Instruction conflict groups");
        RequireBounded(snapshot.CrossSourceAssertions, "cross-source canonical assertions");
        RequireBounded(snapshot.CrossSourceTargetLinkClaims, "cross-source target-link claims");
        RequireBounded(snapshot.UnresolvedCrossSourceClaimContents, "unresolved cross-source typed claims");
        RequireBounded(snapshot.UnresolvedCrossSourceEvidenceBindings, "unresolved cross-source evidence bindings");
        RequireBounded(snapshot.UnresolvedCrossSourceAssertions, "unresolved cross-source assertions");
        if (snapshot.Revision != snapshot.SourceRevisions.Length)
            throw new InvalidDataException("Canonical catalog revision does not match its append-only source-revision history.");

        EnsureDistinct(snapshot.Sources, value => value.Id, "catalog sources");
        EnsureDistinct(snapshot.Artifacts, value => value.Id, "source artifacts");
        EnsureDistinct(snapshot.SourceRevisions, value => value.Id, "source revisions");
        EnsureDistinct(snapshot.KnowledgeRecords, value => value.Id, "knowledge records");
        EnsureDistinct(snapshot.FileEvidenceReceipts, value => value.Id, "file evidence receipts");
        EnsureDistinct(snapshot.ReferenceEvidenceReceipts, value => value.Id, "reference evidence receipts");
        EnsureDistinct(snapshot.EvidenceBindings, value => value.Id, "evidence bindings");
        EnsureDistinct(snapshot.AdapterDescriptors, value => value.RevisionId, "adapter descriptors");
        EnsureDistinct(snapshot.AdapterBoundSourceRevisions, value => value.Revision.Id, "adapter-bound source revisions");
        EnsureDistinct(snapshot.CorrelationEnvelopes, value => value.Id, "correlations");
        EnsureDistinct(snapshot.UnresolvedSourceAssertions, value => value.Id, "unresolved source assertions");
        EnsureDistinct(snapshot.AcquisitionReceipts, value => value.Id, "source acquisition receipts");
        EnsureDistinct(
            snapshot.ArtifactAcquisitionBindings,
            value => (value.ArtifactId, value.AcquisitionReceiptId),
            "artifact acquisition bindings");
        EnsureDistinct(snapshot.ImportedPackages, value => value.Id, "imported packages");
        EnsureDistinct(snapshot.SourceNativeLocationTypeAssertions, value => value.Id, "source-native location type assertions");
        EnsureDistinct(snapshot.LocationSemanticClassificationAssertions, value => value.Id, "location semantic classifications");
        EnsureDistinct(snapshot.RecordLifecycleAssertions, value => value.Id, "canonical record lifecycle assertions");
        EnsureDistinct(snapshot.CorrelatedRelationshipEnvelopes, value => value.Id, "correlated relationship envelopes");
        EnsureDistinct(snapshot.LocationCoverageReports, value => value.Id, "location coverage reports");
        EnsureDistinct(snapshot.SemanticClassificationAssertions, value => value.Id, "semantic classification assertions");
        EnsureDistinct(snapshot.RecordContributionAssertions, value => value.Id, "record contribution assertions");
        EnsureDistinct(snapshot.OrganizationalValueAssertions, value => value.Id, "organizational value assertions");
        EnsureDistinct(snapshot.InstructionAssertions, value => value.Id, "Instruction assertions");
        EnsureDistinct(snapshot.InstructionEvidenceBindings, value => value.Id, "Instruction evidence bindings");
        EnsureDistinct(snapshot.InstructionConflictGroups, value => value.Id, "Instruction conflict groups");
        EnsureDistinct(snapshot.CrossSourceAssertions, value => value.Id, "cross-source canonical assertions");
        EnsureDistinct(snapshot.CrossSourceTargetLinkClaims, value => value.Id, "cross-source target-link claims");
        EnsureDistinct(snapshot.UnresolvedCrossSourceClaimContents, value => value.Id, "unresolved cross-source typed claims");
        EnsureDistinct(snapshot.UnresolvedCrossSourceEvidenceBindings, value => value.Id, "unresolved cross-source evidence bindings");
        EnsureDistinct(snapshot.UnresolvedCrossSourceAssertions, value => value.Id, "unresolved cross-source assertions");
        EnsureNoDuplicateAssertions(snapshot.TerminologyAssertions, TerminologyEquivalent, "terminology assertions");
        EnsureNoDuplicateAssertions(snapshot.RelationshipAssertions, RelationshipEquivalent, "relationship assertions");

        foreach (var source in snapshot.Sources)
            if (CatalogSourceId.DeriveV1(source.Kind, source.NativeIdentity) != source.Id)
                throw new InvalidDataException("Persisted catalog source identity is invalid.");
        foreach (var artifact in snapshot.Artifacts)
            if (SourceArtifactId.DeriveV1(artifact.Digest) != artifact.Id)
                throw new InvalidDataException("Persisted source artifact identity is invalid.");
        foreach (var revision in snapshot.SourceRevisions)
        {
            if (!snapshot.Sources.Any(value => value.Id == revision.SourceId) ||
                revision.ArtifactIds.IsDefault || revision.ArtifactIds.Any(id => !snapshot.Artifacts.Any(value => value.Id == id)))
                throw new InvalidDataException("Persisted source revision is invalid.");

            var adapterBound = snapshot.AdapterBoundSourceRevisions
                .Where(value => value.Revision.Id == revision.Id)
                .ToImmutableArray();
            if (IsLegacySourceRevisionId(revision.Id))
            {
                if (!adapterBound.IsEmpty ||
                    CatalogSourceRevisionId.DeriveV1(revision.SourceId, revision.NativeRevision, revision.ArtifactIds) != revision.Id)
                    throw new InvalidDataException("Persisted v1 source revision is invalid.");
                continue;
            }

            if (adapterBound.Length != 1 || !SourceRevisionEquivalent(adapterBound[0].Revision, revision) ||
                !snapshot.AdapterDescriptors.Any(value => value.RevisionId == adapterBound[0].AdapterRevisionId) ||
                CatalogSourceRevisionId.DeriveV2(
                    revision.SourceId,
                    revision.NativeRevision,
                    revision.ArtifactIds,
                    adapterBound[0].AdapterRevisionId) != revision.Id)
                throw new InvalidDataException("Persisted adapter-bound source revision is invalid.");
        }
        foreach (var adapterBound in snapshot.AdapterBoundSourceRevisions)
        {
            var descriptor = snapshot.AdapterDescriptors.FirstOrDefault(value => value.RevisionId == adapterBound.AdapterRevisionId);
            if (descriptor is null ||
                !snapshot.SourceRevisions.Any(value => SourceRevisionEquivalent(value, adapterBound.Revision)) ||
                adapterBound.ArtifactFormats.Any(value => !descriptor.Supports(adapterBound.SourceScope.GameId, value.Format)))
                throw new InvalidDataException("Persisted adapter-bound source-revision coordinates are invalid.");
        }
        foreach (var descriptor in snapshot.AdapterDescriptors)
        {
            var semanticContractDigest = KnowledgeAdapterSemanticContractDigest.DeriveV1(
                descriptor.SupportedGameIds,
                descriptor.SupportedFormats,
                descriptor.ResourceLimits);
            var rederived = descriptor.RevisionId.AlgorithmVersion switch
            {
                KnowledgeAdapterRevisionId.LegacyAlgorithmVersion when descriptor.Revision.SemanticContractDigest is null =>
                    KnowledgeAdapterRevisionId.DeriveV1(
                        descriptor.AdapterId,
                        descriptor.ExactAdapterVersion,
                        descriptor.AdapterArtifactDigest,
                        descriptor.AdapterContractVersion,
                        descriptor.MappingRulesVersion),
                KnowledgeAdapterRevisionId.CurrentAlgorithmVersion when
                    descriptor.Revision.SemanticContractDigest is { } declared &&
                    declared == semanticContractDigest =>
                    KnowledgeAdapterRevisionId.DeriveV2(
                        descriptor.AdapterId,
                        descriptor.ExactAdapterVersion,
                        descriptor.AdapterContractVersion,
                        descriptor.MappingRulesVersion,
                        declared),
                _ => default,
            };
            if (rederived != descriptor.RevisionId ||
                !snapshot.AdapterBoundSourceRevisions.Any(value => value.AdapterRevisionId == descriptor.RevisionId))
                throw new InvalidDataException("Persisted adapter descriptor is invalid or unreachable.");
        }
        foreach (var source in snapshot.Sources)
            if (!snapshot.SourceRevisions.Any(value => value.SourceId == source.Id))
                throw new InvalidDataException("Persisted catalog source is not reachable from a source revision.");
        foreach (var artifact in snapshot.Artifacts)
            if (!snapshot.SourceRevisions.Any(value => value.ArtifactIds.Contains(artifact.Id)))
                throw new InvalidDataException("Persisted source artifact is not reachable from a source revision.");
        foreach (var record in snapshot.KnowledgeRecords)
            if (!snapshot.SourceRevisions.Any(value => value.Id == record.SourceRevisionId) ||
                NativeRecordIdentityId.DeriveV1(record.GameId, record.NativeIdentity) != record.NativeRecordIdentityId ||
                KnowledgeRecordId.DeriveV1(record.GameId, record.GameVersion, record.ModVersion, record.SourceRevisionId, record.Kind, record.NativeRecordIdentityId) != record.Id)
                throw new InvalidDataException("Persisted knowledge record is invalid.");
        foreach (var term in snapshot.TerminologyAssertions)
            if (!SnapshotAssertionSourceIsPermitted(
                    snapshot,
                    term.KnowledgeRecordId,
                    term.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.Terminology,
                    EvidenceClaimContentId.DeriveV1(term)))
                throw new InvalidDataException("Persisted terminology assertion is invalid.");
        foreach (var relationship in snapshot.RelationshipAssertions)
            if (!SnapshotAssertionSourceIsPermitted(
                    snapshot,
                    relationship.SubjectKnowledgeRecordId,
                    relationship.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.Relationship,
                    EvidenceClaimContentId.DeriveV1(relationship)) ||
                relationship.ResolvedTargetKnowledgeRecordId is KnowledgeRecordId target && !snapshot.KnowledgeRecords.Any(value => value.Id == target))
                throw new InvalidDataException("Persisted relationship assertion is invalid.");

        var receiptCoordinates = new Dictionary<EvidenceReceiptId, EvidenceReceiptCoordinate>();
        foreach (var evidence in snapshot.FileEvidenceReceipts)
            if (!receiptCoordinates.TryAdd(evidence.Id,
                    new(evidence.Id, evidence.Receipt.SourceRevisionId, evidence.Receipt.SourceFieldPath)) ||
                !FileEvidenceReceiptIdentityMatches(evidence) ||
                !snapshot.SourceRevisions.Any(value => value.Id == evidence.Receipt.SourceRevisionId &&
                    value.ArtifactIds.Contains(evidence.Receipt.SourceArtifactId)) ||
                evidence.Receipt.ObservedAtUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Persisted file evidence receipt is invalid.");
        foreach (var evidence in snapshot.ReferenceEvidenceReceipts)
            if (!receiptCoordinates.TryAdd(evidence.Id,
                    new(evidence.Id, evidence.Receipt.SourceRevisionId, evidence.Receipt.ResponseFieldPath)) ||
                EvidenceReceiptId.DeriveV1(evidence.Receipt) != evidence.Id ||
                !snapshot.Sources.Any(value => value.Id == evidence.Receipt.ProviderCatalogSourceId) ||
                !snapshot.SourceRevisions.Any(value => value.Id == evidence.Receipt.SourceRevisionId &&
                    value.ArtifactIds.Contains(evidence.Receipt.ResponseArtifactId)) ||
                evidence.Receipt.RetrievedAtUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Persisted reference evidence receipt is invalid.");
        foreach (var binding in snapshot.EvidenceBindings)
            if (!receiptCoordinates.TryGetValue(binding.EvidenceReceiptId, out var evidenceCoordinate) ||
                evidenceCoordinate.SourceRevisionId != binding.SourceRevisionId ||
                !string.Equals(evidenceCoordinate.FieldPath, binding.ClaimLocator, StringComparison.Ordinal) ||
                !SnapshotBindingSourceIsPermitted(snapshot, binding) ||
                EvidenceBindingId.DeriveV2(
                    binding.EvidenceReceiptId,
                    binding.ClaimKind,
                    binding.KnowledgeRecordId,
                    binding.SourceRevisionId,
                    binding.ClaimLocator,
                    binding.ClaimContentId) != binding.Id ||
                !BindingTargetsExactClaim(
                    binding,
                    snapshot.TerminologyAssertions,
                     snapshot.RelationshipAssertions,
                     snapshot.SourceNativeLocationTypeAssertions,
                     snapshot.LocationSemanticClassificationAssertions,
                     snapshot.RecordLifecycleAssertions,
                      snapshot.SemanticClassificationAssertions,
                      snapshot.RecordContributionAssertions,
                      snapshot.OrganizationalValueAssertions,
                      snapshot.CrossSourceTargetLinkClaims))
                throw new InvalidDataException("Persisted evidence binding is invalid.");

        foreach (var record in snapshot.KnowledgeRecords)
            if (!snapshot.EvidenceBindings.Any(value => value.KnowledgeRecordId == record.Id && value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity))
                throw new InvalidDataException("Persisted knowledge record lacks identity evidence.");
        foreach (var term in snapshot.TerminologyAssertions)
            if (!snapshot.EvidenceBindings.Any(value => BindingTargets(value, term)))
                throw new InvalidDataException("Persisted terminology assertion lacks source evidence.");
        foreach (var relationship in snapshot.RelationshipAssertions)
            if (!snapshot.EvidenceBindings.Any(value => BindingTargets(value, relationship)))
                throw new InvalidDataException("Persisted relationship assertion lacks source evidence.");

        foreach (var assertion in snapshot.SourceNativeLocationTypeAssertions)
            if (!snapshot.KnowledgeRecords.Any(value =>
                    value.Id == assertion.KnowledgeRecordId && value.Kind == KnowledgeKind.Location) ||
                !SnapshotAssertionSourceIsPermitted(
                    snapshot,
                    assertion.KnowledgeRecordId,
                    assertion.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.LocationNativeType,
                    EvidenceClaimContentId.DeriveV1(assertion)) ||
                SourceNativeLocationTypeAssertionId.DeriveV1(
                    assertion.KnowledgeRecordId,
                    assertion.SourceRevisionId,
                    assertion.ExactNativeType,
                    assertion.SourceFieldPath) != assertion.Id ||
                !snapshot.EvidenceBindings.Any(value => BindingTargets(value, assertion)))
                throw new InvalidDataException("Persisted Location native-type assertion is invalid.");
        foreach (var assertion in snapshot.LocationSemanticClassificationAssertions)
        {
            var nativeType = assertion.SourceNativeTypeAssertionId is null
                ? null
                : snapshot.SourceNativeLocationTypeAssertions.FirstOrDefault(value =>
                    value.Id == assertion.SourceNativeTypeAssertionId.Value);
            if (!snapshot.KnowledgeRecords.Any(value =>
                    value.Id == assertion.KnowledgeRecordId && value.Kind == KnowledgeKind.Location) ||
                !SnapshotAssertionSourceIsPermitted(
                    snapshot,
                    assertion.KnowledgeRecordId,
                    assertion.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.LocationSemanticClassification,
                    EvidenceClaimContentId.DeriveV1(assertion)) ||
                assertion.SourceNativeTypeAssertionId is not null && nativeType is null ||
                nativeType is not null && (nativeType.KnowledgeRecordId != assertion.KnowledgeRecordId ||
                    nativeType.SourceRevisionId != assertion.SourceRevisionId) ||
                LocationSemanticClassificationAssertionId.DeriveV1(
                    assertion.KnowledgeRecordId,
                    assertion.SourceRevisionId,
                    assertion.SourceNativeTypeAssertionId,
                    assertion.RoleId,
                    assertion.VocabularyVersion,
                    assertion.ClassificationMethodId,
                    assertion.ClassificationMethodVersion,
                    assertion.SourceFieldPath) != assertion.Id ||
                !snapshot.EvidenceBindings.Any(value => BindingTargets(value, assertion)))
                throw new InvalidDataException("Persisted Location semantic classification is invalid.");
        }
        foreach (var assertion in snapshot.RecordLifecycleAssertions)
            if (!SnapshotAssertionSourceIsPermitted(
                    snapshot,
                    assertion.KnowledgeRecordId,
                    assertion.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.RecordLifecycle,
                    EvidenceClaimContentId.DeriveV1(assertion)) ||
                CanonicalRecordLifecycleAssertionId.DeriveV1(
                    assertion.KnowledgeRecordId,
                    assertion.SourceRevisionId,
                    assertion.State,
                    assertion.SourceNativeLifecycleType,
                    assertion.SourceFieldPath) != assertion.Id ||
                !snapshot.EvidenceBindings.Any(value => BindingTargets(value, assertion)))
                throw new InvalidDataException("Persisted canonical record lifecycle assertion is invalid.");

        var persistedRelationshipClaimIds = snapshot.RelationshipAssertions
            .Select(EvidenceClaimContentId.DeriveV1)
            .ToHashSet();
        foreach (var envelope in snapshot.CorrelatedRelationshipEnvelopes)
            if (!snapshot.KnowledgeRecords.Any(value =>
                    value.Id == envelope.SubjectKnowledgeRecordId && value.Kind == KnowledgeKind.Location) ||
                envelope.CandidateTargetKnowledgeRecordIds.Any(id => !snapshot.KnowledgeRecords.Any(value =>
                    value.Id == id && value.Kind == KnowledgeKind.Location)) ||
                envelope.InputRelationshipClaimIds.Any(id => !persistedRelationshipClaimIds.Contains(id)) ||
                envelope.CorrelationRecordIds.Any(id => snapshot.CorrelationEnvelopes.All(value => value.Id != id)) ||
                CorrelatedRelationshipEnvelopeId.DeriveV1(
                    envelope.SubjectKnowledgeRecordId,
                    envelope.SemanticId,
                    envelope.CandidateTargetKnowledgeRecordIds,
                    envelope.InputRelationshipClaimIds,
                    envelope.CorrelationRecordIds,
                    envelope.MethodId,
                    envelope.MethodVersion,
                    envelope.Outcome) != envelope.Id)
                throw new InvalidDataException("Persisted correlated Location relationship is invalid.");
        foreach (var report in snapshot.LocationCoverageReports)
            if (LocationCoverageReport.Create(
                    report.Manifest,
                    report.SourceFamilies,
                    report.SemanticCategories,
                    report.Terminology,
                    report.Hierarchy,
                    report.Relationships,
                    report.Unresolved).Id != report.Id)
                throw new InvalidDataException("Persisted Location coverage report is invalid.");

        foreach (var correlation in snapshot.CorrelationEnvelopes)
            if (CorrelationRecordId.DeriveV1(correlation.Record, correlation.SupportingEvidenceReceiptIds) != correlation.Id ||
                correlation.Record.MemberIds.Any(id => !snapshot.KnowledgeRecords.Any(value => value.Id == id)) ||
                correlation.SupportingEvidenceReceiptIds.Any(id => !receiptCoordinates.ContainsKey(id)))
                throw new InvalidDataException("Persisted correlation is invalid.");
        foreach (var unresolved in snapshot.UnresolvedSourceAssertions)
            if (UnresolvedSourceAssertionId.DeriveV1(
                    unresolved.GameId,
                    unresolved.SourceRevisionId,
                    unresolved.AdapterRevisionId,
                    unresolved.NativeIdentity,
                    unresolved.CandidateKind,
                    unresolved.ReasonCode,
                    unresolved.SupportingEvidenceReceiptIds) != unresolved.Id ||
                !snapshot.AdapterBoundSourceRevisions.Any(value =>
                    value.Revision.Id == unresolved.SourceRevisionId &&
                    value.AdapterRevisionId == unresolved.AdapterRevisionId &&
                    value.SourceScope.GameId == unresolved.GameId) ||
                unresolved.SupportingEvidenceReceiptIds.Any(id => !receiptCoordinates.ContainsKey(id)))
                throw new InvalidDataException("Persisted unresolved source assertion is invalid.");

        foreach (var receipt in snapshot.AcquisitionReceipts)
            if (SourceAcquisitionReceiptId.DeriveV1(
                    receipt.ReceiptSchemaVersion,
                    receipt.GameId,
                    receipt.DistributionApplicationIdentity,
                    receipt.DistributionBuildVersion,
                    receipt.ContainerCoordinate,
                    receipt.ContainerByteLength,
                    receipt.ContainerDigest,
                    receipt.AcquisitionMethod,
                    receipt.Members) != receipt.Id ||
                !snapshot.ArtifactAcquisitionBindings.Any(value => value.AcquisitionReceiptId == receipt.Id))
                throw new InvalidDataException("Persisted source acquisition receipt is invalid or unreachable.");
        foreach (var binding in snapshot.ArtifactAcquisitionBindings)
        {
            var receipt = snapshot.AcquisitionReceipts.FirstOrDefault(value => value.Id == binding.AcquisitionReceiptId);
            var member = receipt?.Members.FirstOrDefault(value =>
                value.ArtifactId == binding.ArtifactId &&
                value.MemberCoordinate == binding.MemberCoordinate);
            if (!snapshot.Artifacts.Any(value =>
                    value.Id == binding.ArtifactId && value.Digest == binding.MemberDigest) ||
                member is null || member.ByteLength != binding.MemberByteLength || member.Digest != binding.MemberDigest)
                throw new InvalidDataException("Persisted artifact acquisition binding is invalid.");
        }

        foreach (var package in snapshot.ImportedPackages)
        {
            if (!CanonicalCatalogPackageKernel.Verify(package).IsStructurallyValid || !PackageContained(snapshot, package))
                throw new InvalidDataException("Persisted imported package is invalid or incomplete.");
        }
        foreach (var adapterBound in snapshot.AdapterBoundSourceRevisions)
            if (!snapshot.ImportedPackages.Any(package =>
                    package.Payload.SourceRevisions.Any(value =>
                        value.Revision.Id == adapterBound.Revision.Id && AdapterBoundRevisionEquivalent(value, adapterBound)) &&
                    SourceRevisionContentEquivalent(snapshot, package.Payload, adapterBound.Revision.Id)))
                throw new InvalidDataException("Persisted adapter-bound source revision is not reachable from an imported package.");
        foreach (var correlation in snapshot.CorrelationEnvelopes)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.CorrelationEnvelopes.Any(value =>
                    value.Id == correlation.Id && CorrelationEquivalent(value, correlation))))
                throw new InvalidDataException("Persisted correlation is not reachable from an imported package.");
        foreach (var unresolved in snapshot.UnresolvedSourceAssertions)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.UnresolvedSourceAssertions.Any(value =>
                    value.Id == unresolved.Id && UnresolvedAssertionEquivalent(value, unresolved))))
                throw new InvalidDataException("Persisted unresolved assertion is not reachable from an imported package.");
        foreach (var receipt in snapshot.AcquisitionReceipts)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.AcquisitionReceipts.Any(value =>
                    value.Id == receipt.Id && AcquisitionReceiptEquivalent(value, receipt))))
                throw new InvalidDataException("Persisted acquisition receipt is not reachable from an imported package.");
        foreach (var binding in snapshot.ArtifactAcquisitionBindings)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.ArtifactAcquisitionBindings.Any(value =>
                    AcquisitionBindingEquivalent(value, binding))))
                throw new InvalidDataException("Persisted artifact acquisition binding is not reachable from an imported package.");
        foreach (var assertion in snapshot.SourceNativeLocationTypeAssertions)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.SourceNativeLocationTypeAssertions.Any(value =>
                    value.Id == assertion.Id)))
                throw new InvalidDataException("Persisted Location native-type assertion is not reachable from an imported package.");
        foreach (var assertion in snapshot.LocationSemanticClassificationAssertions)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.LocationSemanticClassificationAssertions.Any(value =>
                    value.Id == assertion.Id)))
                throw new InvalidDataException("Persisted Location classification is not reachable from an imported package.");
        foreach (var assertion in snapshot.RecordLifecycleAssertions)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.RecordLifecycleAssertions.Any(value =>
                    value.Id == assertion.Id)))
                throw new InvalidDataException("Persisted lifecycle assertion is not reachable from an imported package.");
        foreach (var envelope in snapshot.CorrelatedRelationshipEnvelopes)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.CorrelatedRelationshipEnvelopes.Any(value =>
                    value.Id == envelope.Id)))
                throw new InvalidDataException("Persisted correlated relationship is not reachable from an imported package.");
        foreach (var report in snapshot.LocationCoverageReports)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.LocationCoverageReports.Any(value =>
                    value.Id == report.Id)))
                throw new InvalidDataException("Persisted Location coverage report is not reachable from an imported package.");
        foreach (var assertion in snapshot.SemanticClassificationAssertions)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.SemanticClassificationAssertions.Any(value =>
                    value.Id == assertion.Id && JsonEquivalent(value, assertion))))
                throw new InvalidDataException("Persisted semantic classification is not reachable from an imported package.");
        foreach (var assertion in snapshot.RecordContributionAssertions)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.RecordContributionAssertions.Any(value =>
                    value.Id == assertion.Id && JsonEquivalent(value, assertion))))
                throw new InvalidDataException("Persisted record contribution is not reachable from an imported package.");
        foreach (var assertion in snapshot.OrganizationalValueAssertions)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.OrganizationalValueAssertions.Any(value =>
                    value.Id == assertion.Id && JsonEquivalent(value, assertion))))
                throw new InvalidDataException("Persisted organizational value is not reachable from an imported package.");
        foreach (var assertion in snapshot.InstructionAssertions)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.InstructionAssertions.Any(value =>
                    value.Id == assertion.Id && JsonEquivalent(value, assertion))))
                throw new InvalidDataException("Persisted Instruction assertion is not reachable from an imported package.");
        foreach (var binding in snapshot.InstructionEvidenceBindings)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.InstructionEvidenceBindings.Any(value =>
                    value.Id == binding.Id && JsonEquivalent(value, binding))))
                throw new InvalidDataException("Persisted Instruction evidence binding is not reachable from an imported package.");
        foreach (var conflict in snapshot.InstructionConflictGroups)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.InstructionConflictGroups.Any(value =>
                    value.Id == conflict.Id && JsonEquivalent(value, conflict))))
                throw new InvalidDataException("Persisted Instruction conflict group is not reachable from an imported package.");
        foreach (var envelope in snapshot.CrossSourceAssertions)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.CrossSourceAssertions.Any(value =>
                    value.Id == envelope.Id && JsonEquivalent(value, envelope))))
                throw new InvalidDataException("Persisted cross-source canonical assertion is not reachable from an imported package.");
        foreach (var claim in snapshot.CrossSourceTargetLinkClaims)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.CrossSourceTargetLinkClaims.Any(value =>
                    value.Id == claim.Id && JsonEquivalent(value, claim))))
                throw new InvalidDataException("Persisted cross-source target-link claim is not reachable from an imported package.");
        foreach (var claim in snapshot.UnresolvedCrossSourceClaimContents)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.UnresolvedCrossSourceClaimContents.Any(value =>
                    value.Id == claim.Id && JsonEquivalent(value, claim))))
                throw new InvalidDataException("Persisted unresolved cross-source typed claim is not reachable from an imported package.");
        foreach (var binding in snapshot.UnresolvedCrossSourceEvidenceBindings)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.UnresolvedCrossSourceEvidenceBindings.Any(value =>
                    value.Id == binding.Id && JsonEquivalent(value, binding))))
                throw new InvalidDataException("Persisted unresolved cross-source evidence binding is not reachable from an imported package.");
        foreach (var assertion in snapshot.UnresolvedCrossSourceAssertions)
            if (!snapshot.ImportedPackages.Any(package => package.Payload.UnresolvedCrossSourceAssertions.Any(value =>
                    value.Id == assertion.Id && JsonEquivalent(value, assertion))))
                throw new InvalidDataException("Persisted unresolved cross-source assertion is not reachable from an imported package.");
    }

    private static bool SnapshotAssertionSourceIsPermitted(
        CanonicalKnowledgeCatalogSnapshot snapshot,
        KnowledgeRecordId recordId,
        CatalogSourceRevisionId assertingRevisionId,
        CrossSourceCanonicalAssertionKind kind,
        EvidenceClaimContentId claimContentId)
    {
        var record = snapshot.KnowledgeRecords.FirstOrDefault(value => value.Id == recordId);
        return record is not null &&
               (record.SourceRevisionId == assertingRevisionId ||
                snapshot.CrossSourceAssertions.Any(value =>
                    value.TargetKnowledgeRecordId == recordId &&
                    value.TargetOriginSourceRevisionId == record.SourceRevisionId &&
                    value.AssertingSourceRevisionId == assertingRevisionId &&
                    value.AssertionKind == kind &&
                    value.UnderlyingClaimContentId == claimContentId));
    }

    private static bool SnapshotBindingSourceIsPermitted(
        CanonicalKnowledgeCatalogSnapshot snapshot,
        EvidenceBinding binding)
    {
        var record = snapshot.KnowledgeRecords.FirstOrDefault(value => value.Id == binding.KnowledgeRecordId);
        return record is not null &&
               (record.SourceRevisionId == binding.SourceRevisionId ||
                binding.ClaimKind == EvidenceClaimKind.CrossSourceTargetLink &&
                binding.ClaimContentId is EvidenceClaimContentId linkClaimContentId &&
                snapshot.CrossSourceAssertions.Any(value =>
                    value.TargetKnowledgeRecordId == binding.KnowledgeRecordId &&
                    value.AssertingSourceRevisionId == binding.SourceRevisionId &&
                    value.SupportingEvidenceBindingIds.Contains(binding.Id) &&
                    snapshot.CrossSourceTargetLinkClaims.Any(claim =>
                        claim.Id == value.TargetLinkClaimId &&
                        EvidenceClaimContentId.DeriveV1(claim) == linkClaimContentId)) ||
                snapshot.CrossSourceAssertions.Any(value =>
                    value.TargetKnowledgeRecordId == binding.KnowledgeRecordId &&
                    value.AssertingSourceRevisionId == binding.SourceRevisionId &&
                    value.ClaimKind == binding.ClaimKind &&
                    value.UnderlyingClaimContentId == binding.ClaimContentId &&
                    value.SupportingEvidenceBindingIds.Contains(binding.Id)));
    }

    private static bool BindingTargetsExactClaim(
        EvidenceBinding binding,
        ImmutableArray<TerminologyAssertion> terminologyAssertions,
        ImmutableArray<RelationshipAssertion> relationshipAssertions,
        ImmutableArray<SourceNativeLocationTypeAssertion> nativeTypeAssertions,
        ImmutableArray<LocationSemanticClassificationAssertion> classificationAssertions,
        ImmutableArray<CanonicalRecordLifecycleAssertion> lifecycleAssertions,
        ImmutableArray<CanonicalSemanticClassificationAssertion> semanticClassifications,
        ImmutableArray<CanonicalRecordContributionAssertion> recordContributions,
        ImmutableArray<CanonicalOrganizationalValueAssertion> organizationalValues,
        ImmutableArray<CrossSourceTargetLinkClaim> targetLinkClaims) =>
        binding.ClaimKind switch
        {
            EvidenceClaimKind.KnowledgeIdentity => binding.ClaimContentId is null,
            EvidenceClaimKind.Terminology => terminologyAssertions.Any(value => BindingTargets(binding, value)),
            EvidenceClaimKind.Relationship => relationshipAssertions.Any(value => BindingTargets(binding, value)),
            EvidenceClaimKind.LocationNativeType => nativeTypeAssertions.Any(value => BindingTargets(binding, value)),
            EvidenceClaimKind.LocationSemanticClassification => classificationAssertions.Any(value => BindingTargets(binding, value)),
            EvidenceClaimKind.RecordLifecycle => lifecycleAssertions.Any(value => BindingTargets(binding, value)),
            EvidenceClaimKind.SemanticClassification => semanticClassifications.Any(value => BindingTargets(binding, value)),
            EvidenceClaimKind.RecordContribution => recordContributions.Any(value => BindingTargets(binding, value)),
            EvidenceClaimKind.OrganizationalValue => organizationalValues.Any(value => BindingTargets(binding, value)),
            EvidenceClaimKind.CrossSourceTargetLink => targetLinkClaims.Any(value => BindingTargets(binding, value)),
            _ => false,
        };

    private static bool BindingTargets(EvidenceBinding binding, TerminologyAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.Terminology &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool BindingTargets(EvidenceBinding binding, RelationshipAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.Relationship &&
        binding.KnowledgeRecordId == assertion.SubjectKnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool BindingTargets(EvidenceBinding binding, SourceNativeLocationTypeAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.LocationNativeType &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool BindingTargets(EvidenceBinding binding, LocationSemanticClassificationAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.LocationSemanticClassification &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool BindingTargets(EvidenceBinding binding, CanonicalRecordLifecycleAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.RecordLifecycle &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool BindingTargets(EvidenceBinding binding, CanonicalSemanticClassificationAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.SemanticClassification &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool BindingTargets(EvidenceBinding binding, CanonicalRecordContributionAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.RecordContribution &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool BindingTargets(EvidenceBinding binding, CanonicalOrganizationalValueAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.OrganizationalValue &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        IsExactOrStructuredChildLocator(binding.ClaimLocator, assertion.SourceFieldPath) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool IsExactOrStructuredChildLocator(string locator, string claimLocator) =>
        string.Equals(locator, claimLocator, StringComparison.Ordinal) ||
        locator.StartsWith(claimLocator + "/", StringComparison.Ordinal);

    private static bool BindingTargets(EvidenceBinding binding, CrossSourceTargetLinkClaim claim) =>
        binding.ClaimKind == EvidenceClaimKind.CrossSourceTargetLink &&
        binding.KnowledgeRecordId == claim.TargetKnowledgeRecordId &&
        binding.SourceRevisionId == claim.AssertingSourceRevisionId &&
        (string.Equals(binding.ClaimLocator, claim.TargetCoordinateFieldPath, StringComparison.Ordinal) ||
         string.Equals(binding.ClaimLocator, claim.AssertingCoordinateFieldPath, StringComparison.Ordinal)) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(claim);

    private static bool PackageContained(
        CanonicalKnowledgeCatalogSnapshot snapshot,
        CanonicalCatalogPackage package)
    {
        var payload = package.Payload;
        return payload.Sources.All(incoming => snapshot.Sources.Any(value => value.Id == incoming.Id && value == incoming)) &&
               payload.Artifacts.All(incoming => snapshot.Artifacts.Any(value => value.Id == incoming.Id && value == incoming)) &&
               payload.SourceRevisions.All(incoming => snapshot.AdapterBoundSourceRevisions.Any(value =>
                   value.Revision.Id == incoming.Revision.Id && AdapterBoundRevisionEquivalent(value, incoming))) &&
               payload.KnowledgeRecords.All(incoming => snapshot.KnowledgeRecords.Any(value => value.Id == incoming.Id && value == incoming)) &&
               payload.TerminologyAssertions.All(incoming => snapshot.TerminologyAssertions.Any(value => TerminologyEquivalent(value, incoming))) &&
               payload.RelationshipAssertions.All(incoming => snapshot.RelationshipAssertions.Any(value => RelationshipEquivalent(value, incoming))) &&
               payload.FileEvidenceReceipts.All(incoming => snapshot.FileEvidenceReceipts.Any(value => value.Id == incoming.Id && value == incoming)) &&
               payload.ReferenceEvidenceReceipts.All(incoming => snapshot.ReferenceEvidenceReceipts.Any(value => value.Id == incoming.Id && value == incoming)) &&
               payload.EvidenceBindings.All(incoming => snapshot.EvidenceBindings.Any(value => value.Id == incoming.Id && value == incoming)) &&
               payload.AdapterDescriptors.All(incoming => snapshot.AdapterDescriptors.Any(value =>
                   value.RevisionId == incoming.RevisionId && AdapterDescriptorEquivalent(value, incoming))) &&
               payload.CorrelationEnvelopes.All(incoming => snapshot.CorrelationEnvelopes.Any(value =>
                   value.Id == incoming.Id && CorrelationEquivalent(value, incoming))) &&
               payload.UnresolvedSourceAssertions.All(incoming => snapshot.UnresolvedSourceAssertions.Any(value =>
                   value.Id == incoming.Id && UnresolvedAssertionEquivalent(value, incoming))) &&
               payload.AcquisitionReceipts.All(incoming => snapshot.AcquisitionReceipts.Any(value =>
                   value.Id == incoming.Id && AcquisitionReceiptEquivalent(value, incoming))) &&
               payload.ArtifactAcquisitionBindings.All(incoming => snapshot.ArtifactAcquisitionBindings.Any(value =>
                   AcquisitionBindingEquivalent(value, incoming))) &&
               payload.SourceNativeLocationTypeAssertions.All(incoming =>
                   snapshot.SourceNativeLocationTypeAssertions.Any(value => value.Id == incoming.Id)) &&
               payload.LocationSemanticClassificationAssertions.All(incoming =>
                   snapshot.LocationSemanticClassificationAssertions.Any(value => value.Id == incoming.Id)) &&
               payload.RecordLifecycleAssertions.All(incoming =>
                   snapshot.RecordLifecycleAssertions.Any(value => value.Id == incoming.Id)) &&
               payload.CorrelatedRelationshipEnvelopes.All(incoming =>
                   snapshot.CorrelatedRelationshipEnvelopes.Any(value => value.Id == incoming.Id)) &&
               payload.LocationCoverageReports.All(incoming =>
                   snapshot.LocationCoverageReports.Any(value => value.Id == incoming.Id)) &&
               payload.SemanticClassificationAssertions.All(incoming =>
                   snapshot.SemanticClassificationAssertions.Any(value =>
                       value.Id == incoming.Id && JsonEquivalent(value, incoming))) &&
               payload.RecordContributionAssertions.All(incoming =>
                   snapshot.RecordContributionAssertions.Any(value =>
                       value.Id == incoming.Id && JsonEquivalent(value, incoming))) &&
               payload.OrganizationalValueAssertions.All(incoming =>
                   snapshot.OrganizationalValueAssertions.Any(value =>
                       value.Id == incoming.Id && JsonEquivalent(value, incoming))) &&
               payload.InstructionAssertions.All(incoming =>
                   snapshot.InstructionAssertions.Any(value =>
                       value.Id == incoming.Id && JsonEquivalent(value, incoming))) &&
               payload.InstructionEvidenceBindings.All(incoming =>
                   snapshot.InstructionEvidenceBindings.Any(value =>
                       value.Id == incoming.Id && JsonEquivalent(value, incoming))) &&
                payload.InstructionConflictGroups.All(incoming =>
                    snapshot.InstructionConflictGroups.Any(value =>
                        value.Id == incoming.Id && JsonEquivalent(value, incoming))) &&
                payload.CrossSourceAssertions.All(incoming =>
                    snapshot.CrossSourceAssertions.Any(value =>
                        value.Id == incoming.Id && JsonEquivalent(value, incoming))) &&
                payload.CrossSourceTargetLinkClaims.All(incoming =>
                    snapshot.CrossSourceTargetLinkClaims.Any(value =>
                        value.Id == incoming.Id && JsonEquivalent(value, incoming))) &&
                payload.UnresolvedCrossSourceClaimContents.All(incoming =>
                    snapshot.UnresolvedCrossSourceClaimContents.Any(value =>
                        value.Id == incoming.Id && JsonEquivalent(value, incoming))) &&
                payload.UnresolvedCrossSourceEvidenceBindings.All(incoming =>
                    snapshot.UnresolvedCrossSourceEvidenceBindings.Any(value =>
                        value.Id == incoming.Id && JsonEquivalent(value, incoming))) &&
                payload.UnresolvedCrossSourceAssertions.All(incoming =>
                    snapshot.UnresolvedCrossSourceAssertions.Any(value =>
                        value.Id == incoming.Id && JsonEquivalent(value, incoming)));
    }

    private static bool SourceRevisionContentEquivalent(
        CanonicalKnowledgeCatalogSnapshot snapshot,
        CanonicalCatalogPayload payload,
        CatalogSourceRevisionId revisionId) =>
        EquivalentSet(
            snapshot.AdapterBoundSourceRevisions.Where(value => value.Revision.Id == revisionId),
            payload.SourceRevisions.Where(value => value.Revision.Id == revisionId),
            AdapterBoundRevisionEquivalent) &&
        EquivalentSet(
            snapshot.KnowledgeRecords.Where(value => value.SourceRevisionId == revisionId),
            payload.KnowledgeRecords.Where(value => value.SourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && left == right) &&
        EquivalentSet(
            snapshot.TerminologyAssertions.Where(value => value.SourceRevisionId == revisionId),
            payload.TerminologyAssertions.Where(value => value.SourceRevisionId == revisionId),
            TerminologyEquivalent) &&
        EquivalentSet(
            snapshot.RelationshipAssertions.Where(value => value.SourceRevisionId == revisionId),
            payload.RelationshipAssertions.Where(value => value.SourceRevisionId == revisionId),
            RelationshipEquivalent) &&
        EquivalentSet(
            snapshot.FileEvidenceReceipts.Where(value => value.Receipt.SourceRevisionId == revisionId),
            payload.FileEvidenceReceipts.Where(value => value.Receipt.SourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && left == right) &&
        EquivalentSet(
            snapshot.ReferenceEvidenceReceipts.Where(value => value.Receipt.SourceRevisionId == revisionId),
            payload.ReferenceEvidenceReceipts.Where(value => value.Receipt.SourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && left == right) &&
        EquivalentSet(
            snapshot.EvidenceBindings.Where(value => value.SourceRevisionId == revisionId),
            payload.EvidenceBindings.Where(value => value.SourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && left == right) &&
        EquivalentSet(
            snapshot.UnresolvedSourceAssertions.Where(value => value.SourceRevisionId == revisionId),
            payload.UnresolvedSourceAssertions.Where(value => value.SourceRevisionId == revisionId),
            UnresolvedAssertionEquivalent) &&
        EquivalentSet(
            snapshot.SourceNativeLocationTypeAssertions.Where(value => value.SourceRevisionId == revisionId),
            payload.SourceNativeLocationTypeAssertions.Where(value => value.SourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id) &&
        EquivalentSet(
            snapshot.LocationSemanticClassificationAssertions.Where(value => value.SourceRevisionId == revisionId),
            payload.LocationSemanticClassificationAssertions.Where(value => value.SourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id) &&
        EquivalentSet(
            snapshot.RecordLifecycleAssertions.Where(value => value.SourceRevisionId == revisionId),
            payload.RecordLifecycleAssertions.Where(value => value.SourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id) &&
        EquivalentSet(
            snapshot.SemanticClassificationAssertions.Where(value => value.SourceRevisionId == revisionId),
            payload.SemanticClassificationAssertions.Where(value => value.SourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && JsonEquivalent(left, right)) &&
        EquivalentSet(
            snapshot.RecordContributionAssertions.Where(value => value.SourceRevisionId == revisionId),
            payload.RecordContributionAssertions.Where(value => value.SourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && JsonEquivalent(left, right)) &&
        EquivalentSet(
            snapshot.OrganizationalValueAssertions.Where(value => value.SourceRevisionId == revisionId),
            payload.OrganizationalValueAssertions.Where(value => value.SourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && JsonEquivalent(left, right)) &&
        EquivalentSet(
            snapshot.InstructionAssertions.Where(value => value.SourceRevisionId == revisionId),
            payload.InstructionAssertions.Where(value => value.SourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && JsonEquivalent(left, right)) &&
        EquivalentSet(
            snapshot.InstructionEvidenceBindings.Where(value => value.SourceRevisionId == revisionId),
            payload.InstructionEvidenceBindings.Where(value => value.SourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && JsonEquivalent(left, right)) &&
        EquivalentSet(
            snapshot.CrossSourceAssertions.Where(value => value.AssertingSourceRevisionId == revisionId),
            payload.CrossSourceAssertions.Where(value => value.AssertingSourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && JsonEquivalent(left, right)) &&
        EquivalentSet(
            snapshot.CrossSourceTargetLinkClaims.Where(value => value.AssertingSourceRevisionId == revisionId),
            payload.CrossSourceTargetLinkClaims.Where(value => value.AssertingSourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && JsonEquivalent(left, right)) &&
        EquivalentSet(
            snapshot.UnresolvedCrossSourceClaimContents.Where(value => value.AssertingSourceRevisionId == revisionId),
            payload.UnresolvedCrossSourceClaimContents.Where(value => value.AssertingSourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && JsonEquivalent(left, right)) &&
        EquivalentSet(
            snapshot.UnresolvedCrossSourceEvidenceBindings.Where(value => value.AssertingSourceRevisionId == revisionId),
            payload.UnresolvedCrossSourceEvidenceBindings.Where(value => value.AssertingSourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && JsonEquivalent(left, right)) &&
        EquivalentSet(
            snapshot.UnresolvedCrossSourceAssertions.Where(value => value.AssertingSourceRevisionId == revisionId),
            payload.UnresolvedCrossSourceAssertions.Where(value => value.AssertingSourceRevisionId == revisionId),
            static (left, right) => left.Id == right.Id && JsonEquivalent(left, right)) &&
        EquivalentSet(
            InstructionConflictsForRevision(
                snapshot.InstructionConflictGroups,
                snapshot.InstructionAssertions,
                revisionId),
            InstructionConflictsForRevision(
                payload.InstructionConflictGroups,
                payload.InstructionAssertions,
                revisionId),
            static (left, right) => left.Id == right.Id && JsonEquivalent(left, right));

    private static IEnumerable<InstructionConflictGroup> InstructionConflictsForRevision(
        IEnumerable<InstructionConflictGroup> conflicts,
        IEnumerable<InstructionAssertion> assertions,
        CatalogSourceRevisionId revisionId)
    {
        var revisionAssertions = assertions.Where(value => value.SourceRevisionId == revisionId)
            .Select(value => value.Id).ToHashSet();
        return conflicts.Where(value => value.MemberIds.Any(revisionAssertions.Contains));
    }

    private static bool AdapterDescriptorEquivalent(
        GameKnowledgeAdapterDescriptor left,
        GameKnowledgeAdapterDescriptor right) =>
        left.RevisionId == right.RevisionId &&
        left.AdapterId == right.AdapterId &&
        string.Equals(left.ExactAdapterVersion, right.ExactAdapterVersion, StringComparison.Ordinal) &&
        // v2 identity deliberately separates deterministic adapter semantics from incidental
        // build bytes. Exact producing DLLs remain in each package's build-provenance receipt;
        // the append-only store may therefore retain its first descriptor representation.
        (left.RevisionId.AlgorithmVersion == KnowledgeAdapterRevisionId.CurrentAlgorithmVersion ||
         left.AdapterArtifactDigest == right.AdapterArtifactDigest) &&
        left.AdapterContractVersion == right.AdapterContractVersion &&
        string.Equals(left.MappingRulesVersion, right.MappingRulesVersion, StringComparison.Ordinal) &&
        left.SupportedGameIds.SequenceEqual(right.SupportedGameIds) &&
        left.SupportedFormats.Length == right.SupportedFormats.Length &&
        left.SupportedFormats.Zip(right.SupportedFormats).All(pair => SupportedFormatEquivalent(pair.First, pair.Second)) &&
        left.ResourceLimits == right.ResourceLimits;

    private static bool SupportedFormatEquivalent(SupportedKnowledgeFormat left, SupportedKnowledgeFormat right) =>
        string.Equals(left.FormatId, right.FormatId, StringComparison.Ordinal) &&
        string.Equals(left.ExactFormatVersion, right.ExactFormatVersion, StringComparison.Ordinal) &&
        left.ContainerKinds.SequenceEqual(right.ContainerKinds, StringComparer.Ordinal) &&
        left.ResourceObjectTypes.SequenceEqual(right.ResourceObjectTypes, StringComparer.Ordinal) &&
        left.SupportedKnowledgeKinds.SequenceEqual(right.SupportedKnowledgeKinds) &&
        left.SupportsTerminology == right.SupportsTerminology &&
        left.SupportsRelationships == right.SupportsRelationships &&
        left.SupportsHierarchy == right.SupportsHierarchy;

    private static bool AdapterBoundRevisionEquivalent(
        AdapterBoundCatalogSourceRevisionRecord left,
        AdapterBoundCatalogSourceRevisionRecord right) =>
        SourceRevisionEquivalent(left.Revision, right.Revision) &&
        left.AdapterRevisionId == right.AdapterRevisionId &&
        left.SourceScope == right.SourceScope &&
        left.ArtifactFormats.SequenceEqual(right.ArtifactFormats);

    private static bool CorrelationEquivalent(
        CanonicalCorrelationEnvelope left,
        CanonicalCorrelationEnvelope right) =>
        left.Id == right.Id &&
        left.Record.MemberIds.SequenceEqual(right.Record.MemberIds) &&
        string.Equals(left.Record.MethodId, right.Record.MethodId, StringComparison.Ordinal) &&
        string.Equals(left.Record.MethodVersion, right.Record.MethodVersion, StringComparison.Ordinal) &&
        left.Record.Outcome == right.Record.Outcome &&
        left.SupportingEvidenceReceiptIds.SequenceEqual(right.SupportingEvidenceReceiptIds);

    private static bool UnresolvedAssertionEquivalent(
        UnresolvedSourceAssertion left,
        UnresolvedSourceAssertion right) =>
        left.Id == right.Id &&
        left.GameId == right.GameId &&
        left.SourceRevisionId == right.SourceRevisionId &&
        left.AdapterRevisionId == right.AdapterRevisionId &&
        left.NativeIdentity == right.NativeIdentity &&
        left.CandidateKind == right.CandidateKind &&
        string.Equals(left.ReasonCode, right.ReasonCode, StringComparison.Ordinal) &&
        left.SupportingEvidenceReceiptIds.SequenceEqual(right.SupportingEvidenceReceiptIds);

    private static bool AcquisitionReceiptEquivalent(
        SourceAcquisitionReceipt left,
        SourceAcquisitionReceipt right) =>
        left.Id == right.Id &&
        string.Equals(
            JsonSerializer.Serialize(left, JsonOptions),
            JsonSerializer.Serialize(right, JsonOptions),
            StringComparison.Ordinal);

    private static bool AcquisitionBindingEquivalent(
        SourceArtifactAcquisitionBinding left,
        SourceArtifactAcquisitionBinding right) =>
        left.ArtifactId == right.ArtifactId &&
        left.AcquisitionReceiptId == right.AcquisitionReceiptId &&
        left.MemberCoordinate == right.MemberCoordinate &&
        left.MemberByteLength == right.MemberByteLength &&
        left.MemberDigest == right.MemberDigest;

    private static ImmutableArray<SourceArtifactAcquisitionBinding> MergeAcquisitionBindings(
        ImmutableArray<SourceArtifactAcquisitionBinding> existing,
        ImmutableArray<SourceArtifactAcquisitionBinding> incoming)
    {
        var values = existing.ToList();
        foreach (var candidate in incoming)
        {
            var match = values.FirstOrDefault(value =>
                value.ArtifactId == candidate.ArtifactId &&
                value.AcquisitionReceiptId == candidate.AcquisitionReceiptId);
            if (match is null) values.Add(candidate);
            else if (!AcquisitionBindingEquivalent(match, candidate))
                throw new InvalidDataException("An artifact acquisition binding conflicts with immutable persisted content.");
        }
        return values
            .OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal)
            .ThenBy(value => value.AcquisitionReceiptId.Value, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static bool PackageEquivalent(CanonicalCatalogPackage left, CanonicalCatalogPackage right) =>
        left.Id == right.Id &&
        JsonEquivalent(left, right);

    private static bool JsonEquivalent<T>(T left, T right) =>
        string.Equals(
            JsonSerializer.Serialize(left, JsonOptions),
            JsonSerializer.Serialize(right, JsonOptions),
            StringComparison.Ordinal);

    private static bool IsLegacySourceRevisionId(CatalogSourceRevisionId id) =>
        id.Value.StartsWith(
            $"grid.catalog-source-revision.v{CatalogSourceRevisionId.LegacyAlgorithmVersion}.sha256.",
            StringComparison.Ordinal);

    private static ImmutableArray<T> MergeById<T, TId>(
        ImmutableArray<T> existing,
        ImmutableArray<T> incoming,
        Func<T, TId> id,
        Func<T, T, bool> equivalent,
        string description)
        where TId : notnull
    {
        var values = existing.ToList();
        foreach (var candidate in incoming)
        {
            var match = values.FirstOrDefault(value => EqualityComparer<TId>.Default.Equals(id(value), id(candidate)));
            if (match is null)
            {
                values.Add(candidate);
                continue;
            }
            if (!equivalent(match, candidate))
                throw new InvalidDataException($"A {description} identity conflicts with immutable persisted content.");
        }
        return values.OrderBy(value => id(value)?.ToString(), StringComparer.Ordinal).ToImmutableArray();
    }

    private static ImmutableArray<T> MergeAssertions<T>(
        ImmutableArray<T> existing,
        ImmutableArray<T> incoming,
        Func<T, T, bool> equivalent,
        Func<T, string> sortKey)
    {
        var values = existing.ToList();
        foreach (var candidate in incoming)
            if (!values.Any(value => equivalent(value, candidate))) values.Add(candidate);
        return values.OrderBy(sortKey, StringComparer.Ordinal).ToImmutableArray();
    }

    private static bool SourceRevisionEquivalent(CatalogSourceRevisionRecord left, CatalogSourceRevisionRecord right) =>
        left.Id == right.Id && left.SourceId == right.SourceId && left.NativeRevision == right.NativeRevision &&
        left.ArtifactIds.SequenceEqual(right.ArtifactIds);

    private static bool TerminologyEquivalent(TerminologyAssertion left, TerminologyAssertion right) =>
        left.KnowledgeRecordId == right.KnowledgeRecordId && left.SourceRevisionId == right.SourceRevisionId &&
        left.Role == right.Role && string.Equals(left.VerbatimValue, right.VerbatimValue, StringComparison.Ordinal) &&
        string.Equals(left.SourceFieldPath, right.SourceFieldPath, StringComparison.Ordinal) &&
        string.Equals(left.LanguageTag, right.LanguageTag, StringComparison.Ordinal) && left.NativeStringIdentifier == right.NativeStringIdentifier;

    private static bool RelationshipEquivalent(RelationshipAssertion left, RelationshipAssertion right) =>
        left.SubjectKnowledgeRecordId == right.SubjectKnowledgeRecordId && left.SourceRevisionId == right.SourceRevisionId &&
        left.SemanticId == right.SemanticId &&
        string.Equals(left.SourceNativeRelationshipType, right.SourceNativeRelationshipType, StringComparison.Ordinal) &&
        string.Equals(left.SourceFieldPath, right.SourceFieldPath, StringComparison.Ordinal) &&
        left.SourceNativeTarget == right.SourceNativeTarget && left.ResolvedTargetKnowledgeRecordId == right.ResolvedTargetKnowledgeRecordId;

    private static bool FileEvidenceReceiptIdentityMatches(CatalogFileEvidenceReceipt evidence) =>
        evidence.Id.AlgorithmVersion switch
        {
            EvidenceReceiptId.LegacyAlgorithmVersion =>
                evidence.Receipt.NativeObjectIdentity is null &&
                EvidenceReceiptId.DeriveV1(evidence.Receipt) == evidence.Id,
            EvidenceReceiptId.CurrentAlgorithmVersion =>
                evidence.Receipt.NativeObjectIdentity is not null &&
                EvidenceReceiptId.DeriveV2(evidence.Receipt) == evidence.Id,
            _ => false,
        };

    private static string TerminologySortKey(TerminologyAssertion value) =>
        string.Join('\u001f', value.KnowledgeRecordId.Value, value.SourceRevisionId.Value, ((int)value.Role).ToString(System.Globalization.CultureInfo.InvariantCulture),
            value.SourceFieldPath, value.LanguageTag ?? string.Empty, value.VerbatimValue);

    private static string RelationshipSortKey(RelationshipAssertion value) =>
        string.Join('\u001f', value.SubjectKnowledgeRecordId.Value, value.SourceRevisionId.Value, value.SemanticId.Value,
            value.SourceFieldPath, value.SourceNativeRelationshipType, value.SourceNativeTarget.ExactRepresentation,
            value.ResolvedTargetKnowledgeRecordId?.Value ?? string.Empty);

    private static void EnsureDistinct<T, TId>(ImmutableArray<T> values, Func<T, TId> id, string description)
        where TId : notnull
    {
        if (values.Select(id).Distinct().Count() != values.Length)
            throw new InvalidDataException($"{description} contain duplicate identities.");
    }

    private static void EnsureNoDuplicateAssertions<T>(ImmutableArray<T> values, Func<T, T, bool> equivalent, string description)
    {
        for (var left = 0; left < values.Length; left++)
            for (var right = left + 1; right < values.Length; right++)
                if (equivalent(values[left], values[right]))
                    throw new InvalidDataException($"{description} contain duplicate assertions.");
    }

    private static void RequireInitialized<T>(ImmutableArray<T> values, string name)
    {
        if (values.IsDefault) throw new ArgumentException("Immutable collection must be initialized.", name);
    }

    private static void RequireBounded<T>(ImmutableArray<T> values, string description)
    {
        if (values.IsDefault || values.Length > MaximumEntitiesPerCollection)
            throw new InvalidDataException($"Canonical catalog {description} collection is invalid.");
    }

    private readonly record struct EvidenceReceiptCoordinate(
        EvidenceReceiptId Id,
        CatalogSourceRevisionId SourceRevisionId,
        string FieldPath);

    private sealed record LegacyDocument(
        int SchemaVersion,
        long Revision,
        CatalogSourceRecord[]? Sources,
        SourceArtifactRecord[]? Artifacts,
        CatalogSourceRevisionRecord[]? SourceRevisions,
        CanonicalKnowledgeRecord[]? KnowledgeRecords,
        TerminologyAssertion[]? TerminologyAssertions,
        RelationshipAssertion[]? RelationshipAssertions,
        CatalogFileEvidenceReceipt[]? FileEvidenceReceipts,
        CatalogReferenceEvidenceReceipt[]? ReferenceEvidenceReceipts,
        EvidenceBinding[]? EvidenceBindings);

    private sealed record CurrentDocument(
        int SchemaVersion,
        long Revision,
        CatalogSourceRecord[]? Sources,
        SourceArtifactRecord[]? Artifacts,
        CatalogSourceRevisionRecord[]? SourceRevisions,
        CanonicalKnowledgeRecord[]? KnowledgeRecords,
        TerminologyAssertion[]? TerminologyAssertions,
        RelationshipAssertion[]? RelationshipAssertions,
        CatalogFileEvidenceReceipt[]? FileEvidenceReceipts,
        CatalogReferenceEvidenceReceipt[]? ReferenceEvidenceReceipts,
        EvidenceBinding[]? EvidenceBindings,
        GameKnowledgeAdapterDescriptor[]? AdapterDescriptors,
        AdapterBoundCatalogSourceRevisionRecord[]? AdapterBoundSourceRevisions,
        CanonicalCorrelationEnvelope[]? CorrelationEnvelopes,
        UnresolvedSourceAssertion[]? UnresolvedSourceAssertions,
        SourceAcquisitionReceipt[]? AcquisitionReceipts,
        SourceArtifactAcquisitionBinding[]? ArtifactAcquisitionBindings,
        CanonicalCatalogPackage[]? ImportedPackages);

    private sealed record LocationContractDocument(
        int SchemaVersion,
        long Revision,
        CatalogSourceRecord[]? Sources,
        SourceArtifactRecord[]? Artifacts,
        CatalogSourceRevisionRecord[]? SourceRevisions,
        CanonicalKnowledgeRecord[]? KnowledgeRecords,
        TerminologyAssertion[]? TerminologyAssertions,
        RelationshipAssertion[]? RelationshipAssertions,
        CatalogFileEvidenceReceipt[]? FileEvidenceReceipts,
        CatalogReferenceEvidenceReceipt[]? ReferenceEvidenceReceipts,
        EvidenceBinding[]? EvidenceBindings,
        GameKnowledgeAdapterDescriptor[]? AdapterDescriptors,
        AdapterBoundCatalogSourceRevisionRecord[]? AdapterBoundSourceRevisions,
        CanonicalCorrelationEnvelope[]? CorrelationEnvelopes,
        UnresolvedSourceAssertion[]? UnresolvedSourceAssertions,
        SourceAcquisitionReceipt[]? AcquisitionReceipts,
        SourceArtifactAcquisitionBinding[]? ArtifactAcquisitionBindings,
        CanonicalCatalogPackage[]? ImportedPackages,
        SourceNativeLocationTypeAssertion[]? SourceNativeLocationTypeAssertions,
        LocationSemanticClassificationAssertion[]? LocationSemanticClassificationAssertions,
        CanonicalRecordLifecycleAssertion[]? RecordLifecycleAssertions,
        CorrelatedRelationshipEnvelope[]? CorrelatedRelationshipEnvelopes,
        LocationCoverageReport[]? LocationCoverageReports);

    private sealed record ProjectionContractDocument(
        int SchemaVersion,
        long Revision,
        CatalogSourceRecord[]? Sources,
        SourceArtifactRecord[]? Artifacts,
        CatalogSourceRevisionRecord[]? SourceRevisions,
        CanonicalKnowledgeRecord[]? KnowledgeRecords,
        TerminologyAssertion[]? TerminologyAssertions,
        RelationshipAssertion[]? RelationshipAssertions,
        CatalogFileEvidenceReceipt[]? FileEvidenceReceipts,
        CatalogReferenceEvidenceReceipt[]? ReferenceEvidenceReceipts,
        EvidenceBinding[]? EvidenceBindings,
        GameKnowledgeAdapterDescriptor[]? AdapterDescriptors,
        AdapterBoundCatalogSourceRevisionRecord[]? AdapterBoundSourceRevisions,
        CanonicalCorrelationEnvelope[]? CorrelationEnvelopes,
        UnresolvedSourceAssertion[]? UnresolvedSourceAssertions,
        SourceAcquisitionReceipt[]? AcquisitionReceipts,
        SourceArtifactAcquisitionBinding[]? ArtifactAcquisitionBindings,
        CanonicalCatalogPackage[]? ImportedPackages,
        SourceNativeLocationTypeAssertion[]? SourceNativeLocationTypeAssertions,
        LocationSemanticClassificationAssertion[]? LocationSemanticClassificationAssertions,
        CanonicalRecordLifecycleAssertion[]? RecordLifecycleAssertions,
        CorrelatedRelationshipEnvelope[]? CorrelatedRelationshipEnvelopes,
        LocationCoverageReport[]? LocationCoverageReports,
        CanonicalSemanticClassificationAssertion[]? SemanticClassificationAssertions,
        CanonicalRecordContributionAssertion[]? RecordContributionAssertions,
        CanonicalOrganizationalValueAssertion[]? OrganizationalValueAssertions,
        InstructionAssertion[]? InstructionAssertions,
        InstructionEvidenceBinding[]? InstructionEvidenceBindings,
        InstructionConflictGroup[]? InstructionConflictGroups);

    private sealed record CrossSourceContractDocument(
        int SchemaVersion,
        long Revision,
        CatalogSourceRecord[]? Sources,
        SourceArtifactRecord[]? Artifacts,
        CatalogSourceRevisionRecord[]? SourceRevisions,
        CanonicalKnowledgeRecord[]? KnowledgeRecords,
        TerminologyAssertion[]? TerminologyAssertions,
        RelationshipAssertion[]? RelationshipAssertions,
        CatalogFileEvidenceReceipt[]? FileEvidenceReceipts,
        CatalogReferenceEvidenceReceipt[]? ReferenceEvidenceReceipts,
        EvidenceBinding[]? EvidenceBindings,
        GameKnowledgeAdapterDescriptor[]? AdapterDescriptors,
        AdapterBoundCatalogSourceRevisionRecord[]? AdapterBoundSourceRevisions,
        CanonicalCorrelationEnvelope[]? CorrelationEnvelopes,
        UnresolvedSourceAssertion[]? UnresolvedSourceAssertions,
        SourceAcquisitionReceipt[]? AcquisitionReceipts,
        SourceArtifactAcquisitionBinding[]? ArtifactAcquisitionBindings,
        CanonicalCatalogPackage[]? ImportedPackages,
        SourceNativeLocationTypeAssertion[]? SourceNativeLocationTypeAssertions,
        LocationSemanticClassificationAssertion[]? LocationSemanticClassificationAssertions,
        CanonicalRecordLifecycleAssertion[]? RecordLifecycleAssertions,
        CorrelatedRelationshipEnvelope[]? CorrelatedRelationshipEnvelopes,
        LocationCoverageReport[]? LocationCoverageReports,
        CanonicalSemanticClassificationAssertion[]? SemanticClassificationAssertions,
        CanonicalRecordContributionAssertion[]? RecordContributionAssertions,
        CanonicalOrganizationalValueAssertion[]? OrganizationalValueAssertions,
        InstructionAssertion[]? InstructionAssertions,
        InstructionEvidenceBinding[]? InstructionEvidenceBindings,
        InstructionConflictGroup[]? InstructionConflictGroups,
        CrossSourceCanonicalAssertion[]? CrossSourceAssertions,
        CrossSourceTargetLinkClaim[]? CrossSourceTargetLinkClaims,
        UnresolvedCrossSourceClaimContent[]? UnresolvedCrossSourceClaimContents,
        UnresolvedCrossSourceEvidenceBinding[]? UnresolvedCrossSourceEvidenceBindings,
        UnresolvedCrossSourceAssertion[]? UnresolvedCrossSourceAssertions);
}
