using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.App.Services;

/// <summary>
/// Deterministic, game-neutral development data for the read-only catalog reviewer.
/// It is not a published catalog and carries a Rejected QCS status deliberately.
/// </summary>
public static class DeveloperCatalogInspectionFixture
{
    public static ImmutableArray<CatalogInspectionPackageOption> CreateOptions()
    {
        var package = CreatePackage();
        var invalid = package with
        {
            Id = new CatalogPackageId("grid.catalog-package.v2.sha256." + new string('0', 64)),
        };
        return
        [
            new("Universal conformance fixture · structurally valid · QCS Rejected", package),
            new("Universal conformance fixture · tampered package ID", invalid),
        ];
    }

    private static CanonicalCatalogPackage CreatePackage()
    {
        var gameId = new GameId("game.conformance.fixture");
        var gameVersion = SourceNativeVersion.FromExactUtf8("grid.conformance.game-version", "revision-7");
        var format = new KnowledgeFormatCoordinate("grid.conformance.fixture-kv", "1");
        var adapter = new GameKnowledgeAdapterDescriptor(
            new KnowledgeAdapterId("grid.adapter.conformance-fixture"),
            "1.0-exact",
            ContentDigest.ComputeSha256("grid-conformance-fixture-adapter"u8),
            1,
            "fixture.mapping.v1",
            [gameId],
            [new SupportedKnowledgeFormat(
                format.FormatId,
                format.ExactFormatVersion,
                ["frozen-bytes"],
                ["fixture-node"],
                [KnowledgeKind.Location, KnowledgeKind.Item, KnowledgeKind.Actor],
                supportsTerminology: true,
                supportsRelationships: true,
                supportsHierarchy: true)],
            new KnowledgeAdapterResourceLimits(4096, 4, 16, 16));

        var sourceBytes = Encoding.UTF8.GetBytes(
            "id=child\nname=  Café—North  \\r\\n\nparent=parent\nid=parent\nid=actor-keeper\nunknown=opaque-42\n");
        var responseBytes = Encoding.UTF8.GetBytes("{\"actor\":{\"name\":\"Keeper\"}}");
        var sourceArtifact = Artifact(sourceBytes);
        var responseArtifact = Artifact(responseBytes);
        var sourceNative = SourceNativeIdentifier.FromExactUtf8(
            "grid.conformance.fixture.source", "frozen-source-set", "game.conformance.fixture:grid.conformance.fixture-kv");
        var source = new CatalogSourceRecord(
            CatalogSourceId.DeriveV1(KnowledgeSourceKind.FrozenRepositoryDataset, sourceNative),
            KnowledgeSourceKind.FrozenRepositoryDataset,
            sourceNative);
        var nativeRevision = SourceNativeVersion.FromExactUtf8(
            "grid.conformance.fixture.source-revision",
            $"{sourceArtifact.Digest.HexValue}:{responseArtifact.Digest.HexValue}");
        var revisionId = CatalogSourceRevisionId.DeriveV2(
            source.Id, nativeRevision, [sourceArtifact.Id, responseArtifact.Id], adapter.RevisionId);
        var revision = new CatalogSourceRevisionRecord(
            revisionId, source.Id, nativeRevision, [sourceArtifact.Id, responseArtifact.Id]);

        var child = Record("child", KnowledgeKind.Location, gameId, gameVersion, revisionId);
        var parent = Record("parent", KnowledgeKind.Location, gameId, gameVersion, revisionId);
        var actor = Record("actor-keeper", KnowledgeKind.Actor, gameId, gameVersion, revisionId);
        var terminology = new[]
        {
            new TerminologyAssertion(child.Id, revisionId, TerminologyAssertionRole.PrimaryName,
                "  Café—North  \r\n", "fixture-node[child]/name", "und"),
            new TerminologyAssertion(child.Id, revisionId, TerminologyAssertionRole.PrimaryName,
                "Café—Nord", "fixture-node[child]/alternate-name", "und"),
            new TerminologyAssertion(actor.Id, revisionId, TerminologyAssertionRole.PrimaryName,
                "Keeper", "response/actor/name", "en"),
        };
        var relationships = new[]
        {
            new RelationshipAssertion(child.Id, revisionId, new RelationshipSemanticId("grid.relationship.parent"),
                "parent", "fixture-node[child]/parent", parent.NativeIdentity, parent.Id),
            new RelationshipAssertion(child.Id, revisionId, new RelationshipSemanticId("grid.relationship.parent"),
                "parent", "fixture-node[child]/competing-parent", actor.NativeIdentity, actor.Id),
            new RelationshipAssertion(parent.Id, revisionId, new RelationshipSemanticId("grid.relationship.contains"),
                "contains", "fixture-node[parent]/unknown-child",
                SourceNativeIdentifier.FromExactUtf8("grid.conformance.fixture.record", "opaque", "opaque-42")),
        };

        var fileReceipts = new[]
        {
            FileReceipt(revisionId, sourceArtifact, "fixture-node[child]", "fixture-node[child]/id"),
            FileReceipt(revisionId, sourceArtifact, "fixture-node[parent]", "fixture-node[parent]/id"),
            FileReceipt(revisionId, sourceArtifact, "fixture-node[actor-keeper]", "fixture-node[actor-keeper]/id"),
            FileReceipt(revisionId, sourceArtifact, "fixture-node[child]", terminology[0].SourceFieldPath),
            FileReceipt(revisionId, sourceArtifact, "fixture-node[child]", terminology[1].SourceFieldPath),
            FileReceipt(revisionId, sourceArtifact, "fixture-node[child]", relationships[0].SourceFieldPath),
            FileReceipt(revisionId, sourceArtifact, "fixture-node[child]", relationships[1].SourceFieldPath),
            FileReceipt(revisionId, sourceArtifact, "fixture-node[parent]", relationships[2].SourceFieldPath),
            FileReceipt(revisionId, sourceArtifact, "fixture-node[opaque-42]", "fixture-node[opaque-42]/kind"),
        };
        var reference = ReferenceReceipt(revisionId, source.Id, responseArtifact, terminology[2].SourceFieldPath);
        var bindings = new[]
        {
            Bind(fileReceipts[0], EvidenceClaimKind.KnowledgeIdentity, child.Id, revisionId, null),
            Bind(fileReceipts[1], EvidenceClaimKind.KnowledgeIdentity, parent.Id, revisionId, null),
            Bind(fileReceipts[2], EvidenceClaimKind.KnowledgeIdentity, actor.Id, revisionId, null),
            Bind(fileReceipts[3], EvidenceClaimKind.Terminology, child.Id, revisionId, EvidenceClaimContentId.DeriveV1(terminology[0])),
            Bind(fileReceipts[4], EvidenceClaimKind.Terminology, child.Id, revisionId, EvidenceClaimContentId.DeriveV1(terminology[1])),
            Bind(reference, EvidenceClaimKind.Terminology, actor.Id, revisionId, EvidenceClaimContentId.DeriveV1(terminology[2])),
            Bind(fileReceipts[5], EvidenceClaimKind.Relationship, child.Id, revisionId, EvidenceClaimContentId.DeriveV1(relationships[0])),
            Bind(fileReceipts[6], EvidenceClaimKind.Relationship, child.Id, revisionId, EvidenceClaimContentId.DeriveV1(relationships[1])),
            Bind(fileReceipts[7], EvidenceClaimKind.Relationship, parent.Id, revisionId, EvidenceClaimContentId.DeriveV1(relationships[2])),
        };

        var unresolvedNative = SourceNativeIdentifier.FromExactUtf8(
            "grid.conformance.fixture.record", "opaque", "opaque-42");
        var unresolvedId = UnresolvedSourceAssertionId.DeriveV1(
            gameId, revisionId, adapter.RevisionId, unresolvedNative, null, "fixture.unclassified", [fileReceipts[8].Id]);
        var unresolved = new UnresolvedSourceAssertion(
            unresolvedId, gameId, revisionId, adapter.RevisionId, unresolvedNative, null,
            "fixture.unclassified", [fileReceipts[8].Id]);
        var correlation = new CorrelationRecord(
            [child.Id, parent.Id], "fixture.ambiguous-correlation", "1", CorrelationOutcome.Ambiguous);
        var correlationEvidence = new[] { fileReceipts[0].Id, fileReceipts[1].Id }.ToImmutableArray();
        var correlationEnvelope = new CanonicalCorrelationEnvelope(
            CorrelationRecordId.DeriveV1(correlation, correlationEvidence), correlation, correlationEvidence);

        var payload = new CanonicalCatalogPayload(
            KnowledgeCoverageState.Partial,
            [adapter],
            [source],
            [sourceArtifact, responseArtifact],
            [new AdapterBoundCatalogSourceRevisionRecord(
                revision,
                adapter.RevisionId,
                KnowledgeSourceScope.BaseGame(gameId, gameVersion),
                [new SourceArtifactFormatBinding(sourceArtifact.Id, format), new SourceArtifactFormatBinding(responseArtifact.Id, format)])],
            [child, parent, actor],
            terminology.ToImmutableArray(),
            relationships.ToImmutableArray(),
            fileReceipts.ToImmutableArray(),
            [reference],
            bindings.ToImmutableArray(),
            [correlationEnvelope],
            [unresolved]);

        return CanonicalCatalogPackageKernel.CreateV2(
            CatalogPackageKind.BaseGameCatalog,
            new CatalogGameScope(gameId, gameVersion, [sourceArtifact.Id, responseArtifact.Id]),
            null,
            [],
            "grid.composition.v1",
            payload,
            new CatalogValidationSummary(
                CatalogValidationStatus.Rejected,
                "grid.qcs.fixture-policy",
                "1",
                ContentDigest.ComputeSha256("fixture-validation-rejected"u8)),
            new CatalogBuildProvenance("grid.fixture-builder", "1", "fixture-commit"));
    }

    private static SourceArtifactRecord Artifact(byte[] bytes)
    {
        var digest = ContentDigest.ComputeSha256(bytes);
        return new(SourceArtifactId.DeriveV1(digest), digest);
    }

    private static CanonicalKnowledgeRecord Record(
        string exactNativeValue,
        KnowledgeKind kind,
        GameId gameId,
        SourceNativeVersion gameVersion,
        CatalogSourceRevisionId revisionId)
    {
        var native = SourceNativeIdentifier.FromExactUtf8(
            "grid.conformance.fixture.record", "fixture-node", exactNativeValue);
        var nativeId = NativeRecordIdentityId.DeriveV1(gameId, native);
        return new(
            KnowledgeRecordId.DeriveV1(gameId, gameVersion, null, revisionId, kind, nativeId),
            gameId, gameVersion, null, revisionId, kind, nativeId, native);
    }

    private static CatalogFileEvidenceReceipt FileReceipt(
        CatalogSourceRevisionId revisionId,
        SourceArtifactRecord artifact,
        string recordLocator,
        string fieldPath)
    {
        var receipt = new FileEvidenceReceipt(
            revisionId, artifact.Id, artifact.Digest,
            "grid.conformance.fixture-parser", "1", recordLocator, fieldPath,
            null, null, null, DateTimeOffset.UnixEpoch);
        return new(EvidenceReceiptId.DeriveV1(receipt), receipt);
    }

    private static CatalogReferenceEvidenceReceipt ReferenceReceipt(
        CatalogSourceRevisionId revisionId,
        CatalogSourceId sourceId,
        SourceArtifactRecord artifact,
        string fieldPath)
    {
        var receipt = new ReferenceEvidenceReceipt(
            sourceId, revisionId, artifact.Id, artifact.Digest,
            SourceNativeIdentifier.FromExactUtf8("grid.conformance.reference", "actor", "actor-keeper"),
            SourceNativeVersion.FromExactUtf8("grid.conformance.reference", "response-1"),
            fieldPath, DateTimeOffset.UnixEpoch);
        return new(EvidenceReceiptId.DeriveV1(receipt), receipt);
    }

    private static EvidenceBinding Bind(
        CatalogFileEvidenceReceipt receipt,
        EvidenceClaimKind kind,
        KnowledgeRecordId recordId,
        CatalogSourceRevisionId revisionId,
        EvidenceClaimContentId? claimId) =>
        Bind(receipt.Id, receipt.Receipt.SourceFieldPath, kind, recordId, revisionId, claimId);

    private static EvidenceBinding Bind(
        CatalogReferenceEvidenceReceipt receipt,
        EvidenceClaimKind kind,
        KnowledgeRecordId recordId,
        CatalogSourceRevisionId revisionId,
        EvidenceClaimContentId? claimId) =>
        Bind(receipt.Id, receipt.Receipt.ResponseFieldPath, kind, recordId, revisionId, claimId);

    private static EvidenceBinding Bind(
        EvidenceReceiptId receiptId,
        string locator,
        EvidenceClaimKind kind,
        KnowledgeRecordId recordId,
        CatalogSourceRevisionId revisionId,
        EvidenceClaimContentId? claimId) => new(
            EvidenceBindingId.DeriveV2(receiptId, kind, recordId, revisionId, locator, claimId),
            receiptId, kind, recordId, revisionId, locator, claimId);
}

public sealed record CatalogInspectionPackageOption(string Label, CanonicalCatalogPackage Package);
