using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Builds and verifies the semantic, game-independent catalog package contract.
/// Archive production, publication, distribution, and persistence are intentionally outside this kernel.
/// </summary>
public static class CanonicalCatalogPackageKernel
{
    public static CatalogPayloadDigest ComputePayloadDigest(CanonicalCatalogPayload payload) =>
        CanonicalKnowledgePackageEncoding.DerivePayloadDigest(payload);

    public static CatalogPayloadDigest ComputePayloadDigestV3(CanonicalCatalogPayload payload) =>
        CanonicalKnowledgePackageEncoding.DerivePayloadDigestV3(payload);

    public static CatalogPayloadDigest ComputePayloadDigestV4(CanonicalCatalogPayload payload) =>
        CanonicalKnowledgePackageEncoding.DerivePayloadDigestV4(payload);

    public static CatalogPayloadDigest ComputePayloadDigestV5(CanonicalCatalogPayload payload) =>
        CanonicalKnowledgePackageEncoding.DerivePayloadDigestV5(payload);

    public static CatalogPayloadDigest ComputePayloadDigestV6(CanonicalCatalogPayload payload) =>
        CanonicalKnowledgePackageEncoding.DerivePayloadDigestV6(payload);

    public static CatalogPayloadDigest ComputePayloadDigestV7(CanonicalCatalogPayload payload) =>
        CanonicalKnowledgePackageEncoding.DerivePayloadDigestV7(payload);

    public static CanonicalCatalogPackage CreateV2(
        CatalogPackageKind packageKind,
        CatalogGameScope gameScope,
        CatalogModScope? modScope,
        ImmutableArray<CatalogPackageId> requiredBasePackageIds,
        string compositionPolicyVersion,
        CanonicalCatalogPayload payload,
        CatalogValidationSummary validationSummary,
        CatalogBuildProvenance buildProvenance)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(validationSummary);

        var adapterRevisions = payload.AdapterDescriptors
            .Select(value => value.Revision)
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var adapterRevisionIds = adapterRevisions.Select(value => value.Id).ToImmutableArray();
        var sourceRevisionIds = payload.SourceRevisions
            .Select(value => value.Revision.Id)
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var payloadDigest = ComputePayloadDigest(payload);
        var catalogRevisionId = CatalogRevisionId.DeriveV2(
            packageKind,
            gameScope,
            modScope,
            payload.EffectiveCoverage,
            adapterRevisionIds,
            sourceRevisionIds,
            requiredBasePackageIds,
            compositionPolicyVersion,
            payloadDigest);
        var manifest = new CatalogPackageManifest(
            CatalogPackageManifest.LegacySchemaVersion,
            catalogRevisionId,
            packageKind,
            gameScope,
            modScope,
            payload.EffectiveCoverage,
            adapterRevisions,
            sourceRevisionIds,
            requiredBasePackageIds,
            compositionPolicyVersion,
            payloadDigest,
            validationSummary.Status,
            validationSummary.PolicyId,
            validationSummary.ExactPolicyVersion,
            validationSummary.ResultDigest,
            buildProvenance);
        var package = new CanonicalCatalogPackage(
            CanonicalKnowledgePackageEncoding.DerivePackageId(manifest),
            manifest,
            payload,
            validationSummary);
        var verification = Verify(package);
        if (!verification.IsStructurallyValid)
            throw new InvalidDataException($"Catalog package is invalid: {string.Join("; ", verification.Issues)}");
        return package;
    }

    public static CanonicalCatalogPackage CreateV3(
        CatalogPackageKind packageKind,
        CatalogGameScope gameScope,
        CatalogModScope? modScope,
        ImmutableArray<CatalogPackageId> requiredBasePackageIds,
        string compositionPolicyVersion,
        CanonicalCatalogPayload payload,
        CatalogValidationSummary validationSummary,
        CatalogBuildProvenance buildProvenance)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(validationSummary);
        ArgumentNullException.ThrowIfNull(buildProvenance);
        if (buildProvenance.ProvenanceSchemaVersion != CatalogBuildProvenance.CurrentSchemaVersion)
            throw new ArgumentException("Schema-v3 packages require committed build provenance closure.", nameof(buildProvenance));

        var adapterRevisions = payload.AdapterDescriptors
            .Select(value => value.Revision)
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var adapterRevisionIds = adapterRevisions.Select(value => value.Id).ToImmutableArray();
        var sourceRevisionIds = payload.SourceRevisions
            .Select(value => value.Revision.Id)
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var payloadDigest = ComputePayloadDigestV3(payload);
        var catalogRevisionId = CatalogRevisionId.DeriveV3(
            packageKind,
            gameScope,
            modScope,
            payload.EffectiveCoverage,
            adapterRevisionIds,
            sourceRevisionIds,
            requiredBasePackageIds,
            compositionPolicyVersion,
            payloadDigest);
        var manifest = new CatalogPackageManifest(
            CatalogPackageManifest.CurrentSchemaVersion,
            catalogRevisionId,
            packageKind,
            gameScope,
            modScope,
            payload.EffectiveCoverage,
            adapterRevisions,
            sourceRevisionIds,
            requiredBasePackageIds,
            compositionPolicyVersion,
            payloadDigest,
            validationSummary.Status,
            validationSummary.PolicyId,
            validationSummary.ExactPolicyVersion,
            validationSummary.ResultDigest,
            buildProvenance);
        var package = new CanonicalCatalogPackage(
            CanonicalKnowledgePackageEncoding.DerivePackageId(manifest),
            manifest,
            payload,
            validationSummary);
        var verification = Verify(package);
        if (!verification.IsStructurallyValid)
            throw new InvalidDataException($"Catalog package is invalid: {string.Join("; ", verification.Issues)}");
        return package;
    }

    public static CanonicalCatalogPackage CreateV4(
        CatalogPackageKind packageKind,
        CatalogGameScope gameScope,
        CatalogModScope? modScope,
        ImmutableArray<CatalogPackageId> requiredBasePackageIds,
        string compositionPolicyVersion,
        CanonicalCatalogPayload payload,
        CatalogValidationSummary validationSummary,
        CatalogBuildProvenance buildProvenance)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(validationSummary);
        ArgumentNullException.ThrowIfNull(buildProvenance);
        if (buildProvenance.ProvenanceSchemaVersion != CatalogBuildProvenance.CurrentSchemaVersion)
            throw new ArgumentException("Schema-v4 packages require committed build provenance closure.", nameof(buildProvenance));

        var adapterRevisions = payload.AdapterDescriptors
            .Select(value => value.Revision)
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var adapterRevisionIds = adapterRevisions.Select(value => value.Id).ToImmutableArray();
        var sourceRevisionIds = payload.SourceRevisions
            .Select(value => value.Revision.Id)
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var payloadDigest = ComputePayloadDigestV4(payload);
        var catalogRevisionId = CatalogRevisionId.DeriveV4(
            packageKind,
            gameScope,
            modScope,
            payload.EffectiveCoverage,
            adapterRevisionIds,
            sourceRevisionIds,
            requiredBasePackageIds,
            compositionPolicyVersion,
            payloadDigest);
        var manifest = new CatalogPackageManifest(
            CatalogPackageManifest.LocationContractSchemaVersion,
            catalogRevisionId,
            packageKind,
            gameScope,
            modScope,
            payload.EffectiveCoverage,
            adapterRevisions,
            sourceRevisionIds,
            requiredBasePackageIds,
            compositionPolicyVersion,
            payloadDigest,
            validationSummary.Status,
            validationSummary.PolicyId,
            validationSummary.ExactPolicyVersion,
            validationSummary.ResultDigest,
            buildProvenance);
        var package = new CanonicalCatalogPackage(
            CanonicalKnowledgePackageEncoding.DerivePackageId(manifest),
            manifest,
            payload,
            validationSummary);
        var verification = Verify(package);
        if (!verification.IsStructurallyValid)
            throw new InvalidDataException($"Catalog package is invalid: {string.Join("; ", verification.Issues)}");
        return package;
    }

    public static CanonicalCatalogPackage CreateV5(
        CatalogPackageKind packageKind,
        CatalogGameScope gameScope,
        CatalogModScope? modScope,
        ImmutableArray<CatalogPackageId> requiredBasePackageIds,
        string compositionPolicyVersion,
        CanonicalCatalogPayload payload,
        CatalogValidationSummary validationSummary,
        CatalogBuildProvenance buildProvenance)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(validationSummary);
        ArgumentNullException.ThrowIfNull(buildProvenance);
        if (buildProvenance.ProvenanceSchemaVersion != CatalogBuildProvenance.CurrentSchemaVersion)
            throw new ArgumentException("Schema-v5 packages require committed build provenance closure.", nameof(buildProvenance));
        var adapterRevisions = payload.AdapterDescriptors.Select(value => value.Revision)
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        var adapterRevisionIds = adapterRevisions.Select(value => value.Id).ToImmutableArray();
        var sourceRevisionIds = payload.SourceRevisions.Select(value => value.Revision.Id)
            .OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        var payloadDigest = ComputePayloadDigestV5(payload);
        var catalogRevisionId = CatalogRevisionId.DeriveV5(
            packageKind, gameScope, modScope, payload.EffectiveCoverage,
            adapterRevisionIds, sourceRevisionIds, requiredBasePackageIds,
            compositionPolicyVersion, payloadDigest);
        var manifest = new CatalogPackageManifest(
            CatalogPackageManifest.ProjectionContractSchemaVersion,
            catalogRevisionId, packageKind, gameScope, modScope, payload.EffectiveCoverage,
            adapterRevisions, sourceRevisionIds, requiredBasePackageIds,
            compositionPolicyVersion, payloadDigest, validationSummary.Status,
            validationSummary.PolicyId, validationSummary.ExactPolicyVersion,
            validationSummary.ResultDigest, buildProvenance);
        var package = new CanonicalCatalogPackage(
            CanonicalKnowledgePackageEncoding.DerivePackageId(manifest), manifest, payload, validationSummary);
        var verification = Verify(package);
        if (!verification.IsStructurallyValid)
            throw new InvalidDataException($"Catalog package is invalid: {string.Join("; ", verification.Issues)}");
        return package;
    }

    public static CanonicalCatalogPackage CreateV6(
        CatalogPackageKind packageKind,
        CatalogGameScope gameScope,
        CatalogModScope? modScope,
        ImmutableArray<CatalogPackageId> requiredBasePackageIds,
        string compositionPolicyVersion,
        CanonicalCatalogPayload payload,
        CatalogValidationSummary validationSummary,
        CatalogBuildProvenance buildProvenance)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(validationSummary);
        ArgumentNullException.ThrowIfNull(buildProvenance);
        if (buildProvenance.ProvenanceSchemaVersion is not (
                CatalogBuildProvenance.CurrentSchemaVersion or
                CatalogBuildProvenance.DevelopmentSchemaVersion or
                CatalogBuildProvenance.AdapterBuildReceiptSchemaVersion))
            throw new ArgumentException(
                "Schema-v6 packages require committed, reviewed-adapter-build, or explicitly developmental provenance closure.",
                nameof(buildProvenance));
        if (buildProvenance.IsDevelopment && validationSummary.Status != CatalogValidationStatus.Candidate)
            throw new ArgumentException("Development-provenance schema-v6 packages must remain Candidate.", nameof(validationSummary));
        var adapterRevisions = payload.AdapterDescriptors.Select(value => value.Revision)
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        var adapterRevisionIds = adapterRevisions.Select(value => value.Id).ToImmutableArray();
        var sourceRevisionIds = payload.SourceRevisions.Select(value => value.Revision.Id)
            .OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        var payloadDigest = ComputePayloadDigestV6(payload);
        var catalogRevisionId = CatalogRevisionId.DeriveV6(
            packageKind, gameScope, modScope, payload.EffectiveCoverage,
            adapterRevisionIds, sourceRevisionIds, requiredBasePackageIds,
            compositionPolicyVersion, payloadDigest);
        var manifest = new CatalogPackageManifest(
            CatalogPackageManifest.CrossSourceAssertionSchemaVersion,
            catalogRevisionId, packageKind, gameScope, modScope, payload.EffectiveCoverage,
            adapterRevisions, sourceRevisionIds, requiredBasePackageIds,
            compositionPolicyVersion, payloadDigest, validationSummary.Status,
            validationSummary.PolicyId, validationSummary.ExactPolicyVersion,
            validationSummary.ResultDigest, buildProvenance);
        var package = new CanonicalCatalogPackage(
            CanonicalKnowledgePackageEncoding.DerivePackageId(manifest), manifest, payload, validationSummary);
        var verification = Verify(package);
        if (!verification.IsStructurallyValid)
            throw new InvalidDataException($"Catalog package is invalid: {string.Join("; ", verification.Issues)}");
        return package;
    }

    public static CanonicalCatalogPackage CreateV7(
        CatalogPackageKind packageKind,
        CatalogGameScope gameScope,
        CatalogModScope? modScope,
        ImmutableArray<CatalogPackageId> requiredBasePackageIds,
        string compositionPolicyVersion,
        CanonicalCatalogPayload payload,
        CatalogValidationSummary validationSummary,
        CatalogBuildProvenance buildProvenance)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(validationSummary);
        ArgumentNullException.ThrowIfNull(buildProvenance);
        if (buildProvenance.ProvenanceSchemaVersion != CatalogBuildProvenance.AdapterBuildReceiptSchemaVersion)
            throw new ArgumentException(
                "Schema-v7 packages require reviewed adapter-build provenance closure.",
                nameof(buildProvenance));
        if (payload.AdapterDescriptors.Any(value =>
                value.RevisionId.AlgorithmVersion != KnowledgeAdapterRevisionId.CurrentAlgorithmVersion))
            throw new ArgumentException(
                "Schema-v7 packages require semantic adapter revision identities.",
                nameof(payload));
        var adapterRevisions = payload.AdapterDescriptors.Select(value => value.Revision)
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        var adapterRevisionIds = adapterRevisions.Select(value => value.Id).ToImmutableArray();
        var sourceRevisionIds = payload.SourceRevisions.Select(value => value.Revision.Id)
            .OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        var payloadDigest = ComputePayloadDigestV7(payload);
        var catalogRevisionId = CatalogRevisionId.DeriveV7(
            packageKind, gameScope, modScope, payload.EffectiveCoverage,
            adapterRevisionIds, sourceRevisionIds, requiredBasePackageIds,
            compositionPolicyVersion, payloadDigest);
        var manifest = new CatalogPackageManifest(
            CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion,
            catalogRevisionId, packageKind, gameScope, modScope, payload.EffectiveCoverage,
            adapterRevisions, sourceRevisionIds, requiredBasePackageIds,
            compositionPolicyVersion, payloadDigest, validationSummary.Status,
            validationSummary.PolicyId, validationSummary.ExactPolicyVersion,
            validationSummary.ResultDigest, buildProvenance);
        var package = new CanonicalCatalogPackage(
            CanonicalKnowledgePackageEncoding.DerivePackageId(manifest), manifest, payload, validationSummary);
        var verification = Verify(package);
        if (!verification.IsStructurallyValid)
            throw new InvalidDataException($"Catalog package is invalid: {string.Join("; ", verification.Issues)}");
        return package;
    }

    public static CatalogPackageVerificationResult Verify(CanonicalCatalogPackage? package)
    {
        var issues = ImmutableArray.CreateBuilder<string>();
        if (package is null)
        {
            issues.Add("Package is absent.");
            return new(issues.ToImmutable());
        }

        try
        {
            VerifyCore(package, issues);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                                           InvalidOperationException or OverflowException or NullReferenceException)
        {
            issues.Add($"Package validation failed closed ({exception.GetType().Name}).");
        }

        return new(issues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray());
    }

    private static void VerifyCore(
        CanonicalCatalogPackage package,
        ImmutableArray<string>.Builder issues)
    {
        var manifest = package.Manifest;
        var payload = package.Payload;
        var validation = package.ValidationSummary;
        if (manifest is null || payload is null || validation is null)
        {
            issues.Add("Package components must be present.");
            return;
        }

        var payloadDigest = manifest.PackageSchemaVersion switch
        {
            CatalogPackageManifest.LegacySchemaVersion => ComputePayloadDigest(payload),
            CatalogPackageManifest.CurrentSchemaVersion => ComputePayloadDigestV3(payload),
            CatalogPackageManifest.LocationContractSchemaVersion => ComputePayloadDigestV4(payload),
            CatalogPackageManifest.ProjectionContractSchemaVersion => ComputePayloadDigestV5(payload),
            CatalogPackageManifest.CrossSourceAssertionSchemaVersion => ComputePayloadDigestV6(payload),
            CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion => ComputePayloadDigestV7(payload),
            _ => throw new InvalidDataException("Unsupported package schema version."),
        };
        if (payloadDigest != manifest.PayloadDigest)
            issues.Add("Payload digest does not match the semantic payload.");

        var sourceRevisionIds = payload.SourceRevisions.Select(value => value.Revision.Id).ToImmutableArray();
        var adapterRevisionIds = payload.AdapterDescriptors.Select(value => value.RevisionId).ToImmutableArray();
        if (!SameIds(sourceRevisionIds.Select(value => value.Value), manifest.SourceRevisionIds.Select(value => value.Value)))
            issues.Add("Manifest source revisions do not match the payload.");
        if (!SameIds(adapterRevisionIds.Select(value => value.Value), manifest.AdapterRevisionIds.Select(value => value.Value)))
            issues.Add("Manifest adapter revisions do not match the payload.");
        if (manifest.EffectiveCoverage != payload.EffectiveCoverage)
            issues.Add("Manifest coverage does not match the semantic payload.");

        foreach (var descriptor in payload.AdapterDescriptors)
        {
            var semanticContractDigest = KnowledgeAdapterSemanticContractDigest.DeriveV1(
                descriptor.SupportedGameIds,
                descriptor.SupportedFormats,
                descriptor.ResourceLimits);
            var rederived = RederiveAdapterRevision(descriptor.Revision, semanticContractDigest);
            if (rederived != descriptor.RevisionId || descriptor.Revision.Id != descriptor.RevisionId ||
                descriptor.Revision.AdapterId != descriptor.AdapterId ||
                !string.Equals(descriptor.Revision.ExactAdapterVersion, descriptor.ExactAdapterVersion, StringComparison.Ordinal) ||
                descriptor.Revision.AdapterArtifactDigest != descriptor.AdapterArtifactDigest ||
                descriptor.Revision.AdapterContractVersion != descriptor.AdapterContractVersion ||
                !string.Equals(descriptor.Revision.MappingRulesVersion, descriptor.MappingRulesVersion, StringComparison.Ordinal) ||
                descriptor.Revision.SemanticContractDigest !=
                    (descriptor.RevisionId.AlgorithmVersion == KnowledgeAdapterRevisionId.CurrentAlgorithmVersion
                        ? semanticContractDigest
                        : null))
                issues.Add("An adapter revision identity does not match its exact coordinates.");
            if (!payload.SourceRevisions.Any(value => value.AdapterRevisionId == descriptor.RevisionId))
                issues.Add("An adapter descriptor is not reachable from a source revision.");
        }
        if (manifest.PackageSchemaVersion == CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion &&
            payload.AdapterDescriptors.Any(value =>
                value.RevisionId.AlgorithmVersion != KnowledgeAdapterRevisionId.CurrentAlgorithmVersion))
            issues.Add("Schema-v7 packages require semantic adapter revision identities.");
        foreach (var coordinate in manifest.AdapterRevisions)
        {
            var rederived = RederiveAdapterRevision(coordinate, coordinate.SemanticContractDigest);
            if (rederived != coordinate.Id)
                issues.Add("A manifest adapter revision identity does not match its exact coordinates.");
        }
        if (!manifest.AdapterRevisions.All(value => payload.AdapterDescriptors.Any(descriptor =>
                descriptor.Revision == value)))
            issues.Add("Manifest adapter revision coordinates do not match the payload descriptors.");

        var revisionId = manifest.PackageSchemaVersion switch
        {
            CatalogPackageManifest.LegacySchemaVersion => CatalogRevisionId.DeriveV2(
                manifest.PackageKind,
                manifest.GameScope,
                manifest.ModScope,
                manifest.EffectiveCoverage,
                manifest.AdapterRevisionIds,
                manifest.SourceRevisionIds,
                manifest.RequiredBasePackageIds,
                manifest.CompositionPolicyVersion,
                manifest.PayloadDigest),
            CatalogPackageManifest.CurrentSchemaVersion => CatalogRevisionId.DeriveV3(
                manifest.PackageKind,
                manifest.GameScope,
                manifest.ModScope,
                manifest.EffectiveCoverage,
                manifest.AdapterRevisionIds,
                manifest.SourceRevisionIds,
                manifest.RequiredBasePackageIds,
                manifest.CompositionPolicyVersion,
                manifest.PayloadDigest),
            CatalogPackageManifest.LocationContractSchemaVersion => CatalogRevisionId.DeriveV4(
                manifest.PackageKind,
                manifest.GameScope,
                manifest.ModScope,
                manifest.EffectiveCoverage,
                manifest.AdapterRevisionIds,
                manifest.SourceRevisionIds,
                manifest.RequiredBasePackageIds,
                manifest.CompositionPolicyVersion,
                manifest.PayloadDigest),
            CatalogPackageManifest.ProjectionContractSchemaVersion => CatalogRevisionId.DeriveV5(
                manifest.PackageKind,
                manifest.GameScope,
                manifest.ModScope,
                manifest.EffectiveCoverage,
                manifest.AdapterRevisionIds,
                manifest.SourceRevisionIds,
                manifest.RequiredBasePackageIds,
                manifest.CompositionPolicyVersion,
                manifest.PayloadDigest),
            CatalogPackageManifest.CrossSourceAssertionSchemaVersion => CatalogRevisionId.DeriveV6(
                manifest.PackageKind,
                manifest.GameScope,
                manifest.ModScope,
                manifest.EffectiveCoverage,
                manifest.AdapterRevisionIds,
                manifest.SourceRevisionIds,
                manifest.RequiredBasePackageIds,
                manifest.CompositionPolicyVersion,
                manifest.PayloadDigest),
            CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion => CatalogRevisionId.DeriveV7(
                manifest.PackageKind,
                manifest.GameScope,
                manifest.ModScope,
                manifest.EffectiveCoverage,
                manifest.AdapterRevisionIds,
                manifest.SourceRevisionIds,
                manifest.RequiredBasePackageIds,
                manifest.CompositionPolicyVersion,
                manifest.PayloadDigest),
            _ => throw new InvalidDataException("Unsupported package schema version."),
        };
        if (revisionId != manifest.CatalogRevisionId)
            issues.Add("Catalog revision identity does not match the manifest.");
        if (CanonicalKnowledgePackageEncoding.DerivePackageId(manifest) != package.Id)
            issues.Add("Catalog package identity does not match the manifest.");

        if (validation.Status != manifest.ValidationStatus ||
            !string.Equals(validation.PolicyId, manifest.ValidationPolicyId, StringComparison.Ordinal) ||
            !string.Equals(validation.ExactPolicyVersion, manifest.ValidationPolicyVersion, StringComparison.Ordinal) ||
            validation.ResultDigest != manifest.ValidationResultDigest)
            issues.Add("Validation summary does not match the manifest.");

        if (manifest.PackageSchemaVersion == CatalogPackageManifest.LegacySchemaVersion)
        {
            if (!payload.AcquisitionReceipts.IsEmpty || !payload.ArtifactAcquisitionBindings.IsEmpty)
                issues.Add("Schema-v2 packages cannot carry schema-v3 acquisition provenance.");
            if (manifest.BuildProvenance.ProvenanceSchemaVersion != CatalogBuildProvenance.LegacySchemaVersion)
                issues.Add("Schema-v2 package build provenance is invalid.");
        }
        else if (manifest.PackageSchemaVersion == CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion)
        {
            var reviewedAdapterBuild = manifest.BuildProvenance.ProvenanceSchemaVersion ==
                                           CatalogBuildProvenance.AdapterBuildReceiptSchemaVersion &&
                                       !manifest.BuildProvenance.CommittedBuildInputs.IsEmpty &&
                                       manifest.BuildProvenance.DevelopmentBuildInputs.IsEmpty &&
                                       !manifest.BuildProvenance.AdapterBuildReceipts.IsEmpty;
            if (!reviewedAdapterBuild)
                issues.Add("Schema-v7 packages require reviewed adapter-build provenance closure.");
        }
        else if (manifest.PackageSchemaVersion != CatalogPackageManifest.CrossSourceAssertionSchemaVersion &&
                 (manifest.BuildProvenance.ProvenanceSchemaVersion != CatalogBuildProvenance.CurrentSchemaVersion ||
                  manifest.BuildProvenance.CommittedBuildInputs.IsEmpty))
        {
            issues.Add("Schema-v3 package lacks committed build provenance closure.");
        }
        else if (manifest.PackageSchemaVersion == CatalogPackageManifest.CrossSourceAssertionSchemaVersion)
        {
            var committed = manifest.BuildProvenance.ProvenanceSchemaVersion ==
                                CatalogBuildProvenance.CurrentSchemaVersion &&
                            !manifest.BuildProvenance.CommittedBuildInputs.IsEmpty &&
                            manifest.BuildProvenance.DevelopmentBuildInputs.IsEmpty &&
                            manifest.BuildProvenance.AdapterBuildReceipts.IsEmpty;
            var reviewedAdapterBuild = manifest.BuildProvenance.ProvenanceSchemaVersion ==
                                           CatalogBuildProvenance.AdapterBuildReceiptSchemaVersion &&
                                       !manifest.BuildProvenance.CommittedBuildInputs.IsEmpty &&
                                       manifest.BuildProvenance.DevelopmentBuildInputs.IsEmpty &&
                                       !manifest.BuildProvenance.AdapterBuildReceipts.IsEmpty;
            var development = manifest.BuildProvenance.ProvenanceSchemaVersion ==
                                  CatalogBuildProvenance.DevelopmentSchemaVersion &&
                              manifest.BuildProvenance.CommittedBuildInputs.IsEmpty &&
                              !manifest.BuildProvenance.DevelopmentBuildInputs.IsEmpty &&
                              manifest.BuildProvenance.AdapterBuildReceipts.IsEmpty &&
                              manifest.ValidationStatus == CatalogValidationStatus.Candidate;
            if (!committed && !reviewedAdapterBuild && !development)
                issues.Add(
                    "Schema-v6 build provenance is neither committed closure, reviewed adapter-build closure, nor non-publishable Candidate development closure.");
        }

        VerifyAdapterBuildProvenance(manifest, payload, issues);

        VerifyPayloadClosure(manifest, payload, issues);
        VerifyLocationContract(manifest, payload, issues);
        VerifyProjectionContract(manifest, payload, issues);
        VerifyCrossSourceAssertions(manifest, payload, issues);
    }

    private static void VerifyAdapterBuildProvenance(
        CatalogPackageManifest manifest,
        CanonicalCatalogPayload payload,
        ImmutableArray<string>.Builder issues)
    {
        var provenance = manifest.BuildProvenance;
        if (provenance.ProvenanceSchemaVersion != CatalogBuildProvenance.AdapterBuildReceiptSchemaVersion)
        {
            if (!provenance.AdapterBuildReceipts.IsEmpty)
                issues.Add("Historical build provenance unexpectedly carries adapter build receipts.");
            return;
        }

        var expected = payload.AdapterDescriptors
            .Select(value => value.AdapterArtifactDigest)
            .Concat(manifest.AdapterRevisions.Select(value => value.AdapterArtifactDigest))
            .Distinct()
            .OrderBy(value => value.HexValue, StringComparer.Ordinal)
            .ToImmutableArray();
        var actual = provenance.AdapterBuildReceipts
            .Select(value => value.AdapterArtifactDigest)
            .OrderBy(value => value.HexValue, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!expected.SequenceEqual(actual))
            issues.Add("Adapter build provenance receipts do not exactly cover the package adapter artifacts.");
    }

    private static void VerifyPayloadClosure(
        CatalogPackageManifest manifest,
        CanonicalCatalogPayload payload,
        ImmutableArray<string>.Builder issues)
    {
        var sources = payload.Sources.ToDictionary(value => value.Id);
        var artifacts = payload.Artifacts.ToDictionary(value => value.Id);
        var revisions = payload.SourceRevisions.ToDictionary(value => value.Revision.Id);
        var adapters = payload.AdapterDescriptors.ToDictionary(value => value.RevisionId);
        var records = payload.KnowledgeRecords.ToDictionary(value => value.Id);
        var fileEvidence = payload.FileEvidenceReceipts.ToDictionary(value => value.Id);
        var referenceEvidence = payload.ReferenceEvidenceReceipts.ToDictionary(value => value.Id);
        var allEvidenceIds = fileEvidence.Keys.Concat(referenceEvidence.Keys).ToHashSet();

        VerifyAcquisitionClosure(manifest, payload, artifacts, issues);

        foreach (var source in payload.Sources)
        {
            if (CatalogSourceId.DeriveV1(source.Kind, source.NativeIdentity) != source.Id)
                issues.Add("A catalog source identity is invalid.");
            if (!payload.SourceRevisions.Any(value => value.Revision.SourceId == source.Id))
                issues.Add("A catalog source is not reachable from a source revision.");
        }

        foreach (var artifact in payload.Artifacts)
        {
            if (SourceArtifactId.DeriveV1(artifact.Digest) != artifact.Id)
                issues.Add("A source artifact identity is invalid.");
            if (!payload.SourceRevisions.Any(value => value.Revision.ArtifactIds.Contains(artifact.Id)))
                issues.Add("A source artifact is not reachable from a source revision.");
        }

        foreach (var value in payload.SourceRevisions)
        {
            var revision = value.Revision;
            if (!sources.ContainsKey(revision.SourceId) ||
                revision.ArtifactIds.IsDefault ||
                revision.ArtifactIds.Any(id => !artifacts.ContainsKey(id)) ||
                !adapters.TryGetValue(value.AdapterRevisionId, out var adapter) ||
                value.ArtifactFormats.IsDefaultOrEmpty ||
                !value.ArtifactFormats.Select(binding => binding.ArtifactId).SequenceEqual(
                    revision.ArtifactIds.OrderBy(id => id.Value, StringComparer.Ordinal)) ||
                value.ArtifactFormats.Any(binding => !adapter.Supports(value.SourceScope.GameId, binding.Format)) ||
                !SourceScopeMatchesManifestOrCrossSourceDependency(
                    value.SourceScope, revision.Id, manifest, payload) ||
                CatalogSourceRevisionId.DeriveV2(
                    revision.SourceId,
                    revision.NativeRevision,
                    revision.ArtifactIds,
                    value.AdapterRevisionId) != revision.Id)
                issues.Add("An adapter-bound source revision is invalid.");
        }

        if (manifest.GameScope.ArtifactFingerprints.Any(value => !artifacts.ContainsKey(value)))
            issues.Add("Game scope refers to an artifact outside the payload.");
        if (manifest.ModScope?.ArtifactFingerprints.Any(value => !artifacts.ContainsKey(value)) == true)
            issues.Add("Mod scope refers to an artifact outside the payload.");

        foreach (var record in payload.KnowledgeRecords)
        {
            if (!revisions.TryGetValue(record.SourceRevisionId, out var boundRevision) ||
                record.GameId != boundRevision.SourceScope.GameId ||
                record.GameVersion != boundRevision.SourceScope.ExactGameVersion ||
                record.ModVersion != boundRevision.SourceScope.ExactModVersion ||
                NativeRecordIdentityId.DeriveV1(record.GameId, record.NativeIdentity) != record.NativeRecordIdentityId ||
                KnowledgeRecordId.DeriveV1(
                    record.GameId,
                    record.GameVersion,
                    record.ModVersion,
                    record.SourceRevisionId,
                    record.Kind,
                    record.NativeRecordIdentityId) != record.Id)
                issues.Add("A canonical knowledge record is invalid for the package scope.");
        }

        foreach (var assertion in payload.TerminologyAssertions)
            if (!records.TryGetValue(assertion.KnowledgeRecordId, out var record) ||
                !AssertionSourceIsPermitted(
                    manifest, payload, record, assertion.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.Terminology,
                    EvidenceClaimContentId.DeriveV1(assertion)))
                issues.Add("A terminology assertion is not closed over its knowledge record.");

        foreach (var assertion in payload.RelationshipAssertions)
            if (!records.TryGetValue(assertion.SubjectKnowledgeRecordId, out var record) ||
                !AssertionSourceIsPermitted(
                    manifest, payload, record, assertion.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.Relationship,
                    EvidenceClaimContentId.DeriveV1(assertion)) ||
                assertion.ResolvedTargetKnowledgeRecordId is KnowledgeRecordId target && !records.ContainsKey(target))
                issues.Add("A relationship assertion is not closed over its knowledge records.");

        var receiptCoordinates = new Dictionary<EvidenceReceiptId, (CatalogSourceRevisionId RevisionId, string FieldPath)>();
        foreach (var evidence in payload.FileEvidenceReceipts)
        {
            var receipt = evidence.Receipt;
            if (!receiptCoordinates.TryAdd(evidence.Id, (receipt.SourceRevisionId, receipt.SourceFieldPath)) ||
                !FileEvidenceReceiptIdentityMatches(evidence) ||
                !revisions.TryGetValue(receipt.SourceRevisionId, out var revision) ||
                !revision.Revision.ArtifactIds.Contains(receipt.SourceArtifactId) ||
                !artifacts.TryGetValue(receipt.SourceArtifactId, out var artifact) ||
                artifact.Digest != receipt.ArtifactDigest)
                issues.Add("A FILE_VERIFIED receipt is invalid.");
        }

        foreach (var evidence in payload.ReferenceEvidenceReceipts)
        {
            var receipt = evidence.Receipt;
            if (!receiptCoordinates.TryAdd(evidence.Id, (receipt.SourceRevisionId, receipt.ResponseFieldPath)) ||
                EvidenceReceiptId.DeriveV1(receipt) != evidence.Id ||
                !sources.ContainsKey(receipt.ProviderCatalogSourceId) ||
                !revisions.TryGetValue(receipt.SourceRevisionId, out var revision) ||
                !revision.Revision.ArtifactIds.Contains(receipt.ResponseArtifactId) ||
                !artifacts.TryGetValue(receipt.ResponseArtifactId, out var artifact) ||
                artifact.Digest != receipt.ResponseContentDigest)
                issues.Add("A REFERENCE_VERIFIED receipt is invalid.");
        }

        foreach (var binding in payload.EvidenceBindings)
        {
            if (!receiptCoordinates.TryGetValue(binding.EvidenceReceiptId, out var coordinate) ||
                coordinate.RevisionId != binding.SourceRevisionId ||
                !string.Equals(coordinate.FieldPath, binding.ClaimLocator, StringComparison.Ordinal) ||
                !records.TryGetValue(binding.KnowledgeRecordId, out var record) ||
                !EvidenceBindingSourceIsPermitted(manifest, payload, record, binding) ||
                EvidenceBindingId.DeriveV2(
                    binding.EvidenceReceiptId,
                    binding.ClaimKind,
                    binding.KnowledgeRecordId,
                    binding.SourceRevisionId,
                    binding.ClaimLocator,
                    binding.ClaimContentId) != binding.Id ||
                !TargetsExactClaim(binding, payload))
                issues.Add("An evidence binding is invalid.");
        }

        foreach (var record in payload.KnowledgeRecords)
            if (!payload.EvidenceBindings.Any(value =>
                    value.KnowledgeRecordId == record.Id &&
                    value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity))
                issues.Add("A knowledge record lacks identity evidence.");
        foreach (var assertion in payload.TerminologyAssertions)
            if (!payload.EvidenceBindings.Any(value => Targets(value, assertion)))
                issues.Add("A terminology assertion lacks exact claim evidence.");
        foreach (var assertion in payload.RelationshipAssertions)
            if (!payload.EvidenceBindings.Any(value => Targets(value, assertion)))
                issues.Add("A relationship assertion lacks exact claim evidence.");

        foreach (var envelope in payload.CorrelationEnvelopes)
            if (CorrelationRecordId.DeriveV1(envelope.Record, envelope.SupportingEvidenceReceiptIds) != envelope.Id ||
                envelope.Record.MemberIds.Any(value => !records.ContainsKey(value)) ||
                envelope.SupportingEvidenceReceiptIds.Any(value => !allEvidenceIds.Contains(value)))
                issues.Add("A correlation envelope is invalid.");

        foreach (var unresolved in payload.UnresolvedSourceAssertions)
            if (!revisions.TryGetValue(unresolved.SourceRevisionId, out var revision) ||
                revision.AdapterRevisionId != unresolved.AdapterRevisionId ||
                unresolved.GameId != manifest.GameScope.GameId ||
                unresolved.SupportingEvidenceReceiptIds.Any(value => !allEvidenceIds.Contains(value)) ||
                UnresolvedSourceAssertionId.DeriveV1(
                    unresolved.GameId,
                    unresolved.SourceRevisionId,
                    unresolved.AdapterRevisionId,
                    unresolved.NativeIdentity,
                    unresolved.CandidateKind,
                    unresolved.ReasonCode,
                    unresolved.SupportingEvidenceReceiptIds) != unresolved.Id)
                issues.Add("An unresolved source assertion is invalid.");

        var consumedEvidenceIds = payload.EvidenceBindings.Select(value => value.EvidenceReceiptId)
            .Concat(payload.InstructionEvidenceBindings.Select(value => value.EvidenceReceiptId))
            .Concat(payload.UnresolvedCrossSourceEvidenceBindings.Select(value => value.EvidenceReceiptId))
            .Concat(payload.CorrelationEnvelopes.SelectMany(value => value.SupportingEvidenceReceiptIds))
            .Concat(payload.UnresolvedSourceAssertions.SelectMany(value => value.SupportingEvidenceReceiptIds))
            .Concat(payload.LocationCoverageReports
                .SelectMany(report => report.SourceFamilies)
                .SelectMany(family => family.EvidenceBackedExclusions)
                .SelectMany(exclusion => exclusion.SupportingEvidenceReceiptIds))
            .Concat(payload.CrossSourceAssertions.SelectMany(value => value.SupportingEvidenceReceiptIds))
            .ToHashSet();
        if (allEvidenceIds.Any(value => !consumedEvidenceIds.Contains(value)))
            issues.Add("An evidence receipt is orphaned from all legitimate payload constructs.");
    }

    private static void VerifyAcquisitionClosure(
        CatalogPackageManifest manifest,
        CanonicalCatalogPayload payload,
        Dictionary<SourceArtifactId, SourceArtifactRecord> artifacts,
        ImmutableArray<string>.Builder issues)
    {
        if (manifest.PackageSchemaVersion is not (CatalogPackageManifest.CurrentSchemaVersion or
            CatalogPackageManifest.LocationContractSchemaVersion or
            CatalogPackageManifest.ProjectionContractSchemaVersion or
            CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
            CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion))
            return;
        if (payload.AcquisitionReceipts.IsEmpty || payload.ArtifactAcquisitionBindings.IsEmpty)
        {
            issues.Add("Schema-v3 payload requires acquisition receipts and artifact bindings.");
            return;
        }

        var receipts = payload.AcquisitionReceipts.ToDictionary(value => value.Id);
        foreach (var receipt in payload.AcquisitionReceipts)
        {
            var expected = SourceAcquisitionReceiptId.DeriveV1(
                receipt.ReceiptSchemaVersion,
                receipt.GameId,
                receipt.DistributionApplicationIdentity,
                receipt.DistributionBuildVersion,
                receipt.ContainerCoordinate,
                receipt.ContainerByteLength,
                receipt.ContainerDigest,
                receipt.AcquisitionMethod,
                receipt.Members);
            if (expected != receipt.Id || receipt.GameId != manifest.GameScope.GameId)
                issues.Add("A source acquisition receipt is invalid for the package game scope.");
        }

        foreach (var binding in payload.ArtifactAcquisitionBindings)
        {
            if (!artifacts.TryGetValue(binding.ArtifactId, out var artifact) ||
                artifact.Digest != binding.MemberDigest ||
                SourceArtifactId.DeriveV1(binding.MemberDigest) != binding.ArtifactId ||
                !receipts.TryGetValue(binding.AcquisitionReceiptId, out var receipt) ||
                !receipt.Members.Any(member =>
                    member.ArtifactId == binding.ArtifactId &&
                    member.Digest == binding.MemberDigest &&
                    member.ByteLength == binding.MemberByteLength &&
                    member.MemberCoordinate == binding.MemberCoordinate))
                issues.Add("A source artifact acquisition binding is invalid.");
        }

        if (artifacts.Keys.Any(id => !payload.ArtifactAcquisitionBindings.Any(binding => binding.ArtifactId == id)))
            issues.Add("A source artifact lacks acquisition provenance.");
        if (payload.AcquisitionReceipts.Any(receipt =>
                !payload.ArtifactAcquisitionBindings.Any(binding => binding.AcquisitionReceiptId == receipt.Id)))
            issues.Add("A source acquisition receipt is orphaned.");
        if (payload.AcquisitionReceipts.Any(receipt => receipt.Members.Any(member =>
                !payload.ArtifactAcquisitionBindings.Any(binding =>
                    binding.AcquisitionReceiptId == receipt.Id &&
                    binding.ArtifactId == member.ArtifactId &&
                    binding.MemberCoordinate == member.MemberCoordinate))))
            issues.Add("A source acquisition member is orphaned.");
        if (payload.ArtifactAcquisitionBindings.Any(binding =>
                payload.ArtifactAcquisitionBindings.Count(value => value.ArtifactId == binding.ArtifactId) != 1))
            issues.Add("A source artifact has ambiguous acquisition provenance.");
    }

    private static bool SourceScopeMatchesManifest(
        KnowledgeSourceScope sourceScope,
        CatalogPackageManifest manifest)
    {
        if (sourceScope.GameId != manifest.GameScope.GameId ||
            sourceScope.ExactGameVersion != manifest.GameScope.ExactGameVersion)
            return false;

        return manifest.PackageKind switch
        {
            CatalogPackageKind.BaseGameCatalog =>
                sourceScope.ScopeKind == KnowledgeSourceScopeKind.BaseGame &&
                sourceScope.ExactModIdentity is null &&
                sourceScope.ExactModVersion is null,
            CatalogPackageKind.ModCatalogExtension =>
                manifest.ModScope is not null &&
                sourceScope.ScopeKind == KnowledgeSourceScopeKind.ModExtension &&
                sourceScope.ExactModIdentity == manifest.ModScope.ExactModIdentity &&
                sourceScope.ExactModVersion == manifest.ModScope.ExactModVersion,
            _ => false,
        };
    }

    private static bool SourceScopeMatchesManifestOrCrossSourceDependency(
        KnowledgeSourceScope sourceScope,
        CatalogSourceRevisionId sourceRevisionId,
        CatalogPackageManifest manifest,
        CanonicalCatalogPayload payload) =>
        SourceScopeMatchesManifest(sourceScope, manifest) ||
        manifest.PackageSchemaVersion is (CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
            CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion) &&
        manifest.PackageKind == CatalogPackageKind.ModCatalogExtension &&
        sourceScope.ScopeKind == KnowledgeSourceScopeKind.BaseGame &&
        sourceScope.ExactModIdentity is null &&
        sourceScope.ExactModVersion is null &&
        sourceScope.GameId == manifest.GameScope.GameId &&
        sourceScope.ExactGameVersion == manifest.GameScope.ExactGameVersion &&
        payload.CrossSourceAssertions.All(value =>
            value.AssertingSourceRevisionId != sourceRevisionId) &&
        payload.CrossSourceAssertions.Any(value =>
            value.TargetOriginSourceRevisionId == sourceRevisionId);

    private static bool LocationReportScopeIsPermitted(
        LocationCoverageReport report,
        CatalogPackageManifest manifest,
        CanonicalCatalogPayload payload) =>
        SourceScopeMatchesManifest(report.Manifest.SourceScope, manifest) ||
        manifest.PackageSchemaVersion is (CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
            CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion) &&
        manifest.PackageKind == CatalogPackageKind.ModCatalogExtension &&
        report.Manifest.SourceScope.ScopeKind == KnowledgeSourceScopeKind.BaseGame &&
        report.Manifest.SourceScope.ExactModIdentity is null &&
        report.Manifest.SourceScope.ExactModVersion is null &&
        report.Manifest.SourceScope.GameId == manifest.GameScope.GameId &&
        report.Manifest.SourceScope.ExactGameVersion == manifest.GameScope.ExactGameVersion &&
        report.Manifest.SourceFamilies
            .SelectMany(value => value.ExpectedSourceRevisionIds)
            .All(revisionId => payload.CrossSourceAssertions.Any(value =>
                value.TargetOriginSourceRevisionId == revisionId));

    private static void VerifyLocationContract(
        CatalogPackageManifest manifest,
        CanonicalCatalogPayload payload,
        ImmutableArray<string>.Builder issues)
    {
        var hasLocationContractContent =
            !payload.SourceNativeLocationTypeAssertions.IsEmpty ||
            !payload.LocationSemanticClassificationAssertions.IsEmpty ||
            !payload.RecordLifecycleAssertions.IsEmpty ||
            !payload.CorrelatedRelationshipEnvelopes.IsEmpty ||
            !payload.LocationCoverageReports.IsEmpty;

        if (manifest.PackageSchemaVersion is not (CatalogPackageManifest.LocationContractSchemaVersion or
            CatalogPackageManifest.ProjectionContractSchemaVersion or
            CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
            CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion))
        {
            if (hasLocationContractContent)
                issues.Add("Only schema-v4 or schema-v5 packages may carry frozen Location contract content.");
            return;
        }

        var records = payload.KnowledgeRecords.ToDictionary(value => value.Id);
        var locationRecords = payload.KnowledgeRecords
            .Where(value => value.Kind == KnowledgeKind.Location)
            .ToDictionary(value => value.Id);
        var revisions = payload.SourceRevisions.ToDictionary(value => value.Revision.Id);
        var evidenceIds = payload.FileEvidenceReceipts.Select(value => value.Id)
            .Concat(payload.ReferenceEvidenceReceipts.Select(value => value.Id))
            .ToHashSet();
        var evidenceCoordinates = new Dictionary<EvidenceReceiptId, (CatalogSourceRevisionId SourceRevisionId, string FieldPath)>();
        foreach (var value in payload.FileEvidenceReceipts)
            evidenceCoordinates.TryAdd(value.Id, (value.Receipt.SourceRevisionId, value.Receipt.SourceFieldPath));
        foreach (var value in payload.ReferenceEvidenceReceipts)
            evidenceCoordinates.TryAdd(value.Id, (value.Receipt.SourceRevisionId, value.Receipt.ResponseFieldPath));

        if (locationRecords.Count > 0 && payload.LocationCoverageReports.IsEmpty)
            issues.Add("A schema-v4 package containing Locations requires an exact Location coverage report.");

        if (payload.SourceNativeLocationTypeAssertions.Select(value => value.Id).Distinct().Count() !=
            payload.SourceNativeLocationTypeAssertions.Length)
            issues.Add("Location native-type assertion identities must be distinct.");
        foreach (var assertion in payload.SourceNativeLocationTypeAssertions)
        {
            if (!locationRecords.TryGetValue(assertion.KnowledgeRecordId, out var record) ||
                !AssertionSourceIsPermitted(
                    manifest, payload, record, assertion.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.LocationNativeType,
                    EvidenceClaimContentId.DeriveV1(assertion)) ||
                SourceNativeLocationTypeAssertionId.DeriveV1(
                    assertion.KnowledgeRecordId,
                    assertion.SourceRevisionId,
                    assertion.ExactNativeType,
                    assertion.SourceFieldPath) != assertion.Id ||
                !payload.EvidenceBindings.Any(binding => Targets(binding, assertion)))
                issues.Add("A Location native-type assertion is invalid or lacks exact claim evidence.");
        }

        if (payload.LocationSemanticClassificationAssertions.Select(value => value.Id).Distinct().Count() !=
            payload.LocationSemanticClassificationAssertions.Length)
            issues.Add("Location semantic-classification assertion identities must be distinct.");
        foreach (var assertion in payload.LocationSemanticClassificationAssertions)
        {
            var nativeType = assertion.SourceNativeTypeAssertionId is null
                ? null
                : payload.SourceNativeLocationTypeAssertions.FirstOrDefault(value =>
                    value.Id == assertion.SourceNativeTypeAssertionId.Value);
            if (!locationRecords.TryGetValue(assertion.KnowledgeRecordId, out var record) ||
                !AssertionSourceIsPermitted(
                    manifest, payload, record, assertion.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.LocationSemanticClassification,
                    EvidenceClaimContentId.DeriveV1(assertion)) ||
                nativeType is not null && (nativeType.KnowledgeRecordId != assertion.KnowledgeRecordId ||
                    nativeType.SourceRevisionId != assertion.SourceRevisionId) ||
                assertion.SourceNativeTypeAssertionId is not null && nativeType is null ||
                LocationSemanticClassificationAssertionId.DeriveV1(
                    assertion.KnowledgeRecordId,
                    assertion.SourceRevisionId,
                    assertion.SourceNativeTypeAssertionId,
                    assertion.RoleId,
                    assertion.VocabularyVersion,
                    assertion.ClassificationMethodId,
                    assertion.ClassificationMethodVersion,
                    assertion.SourceFieldPath) != assertion.Id ||
                !payload.EvidenceBindings.Any(binding => Targets(binding, assertion)))
                issues.Add("A Location semantic-classification assertion is invalid or lacks exact claim evidence.");
        }

        foreach (var assertion in payload.RecordLifecycleAssertions)
        {
            if (!records.TryGetValue(assertion.KnowledgeRecordId, out var record) ||
                !AssertionSourceIsPermitted(
                    manifest, payload, record, assertion.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.RecordLifecycle,
                    EvidenceClaimContentId.DeriveV1(assertion)) ||
                CanonicalRecordLifecycleAssertionId.DeriveV1(
                    assertion.KnowledgeRecordId,
                    assertion.SourceRevisionId,
                    assertion.State,
                    assertion.SourceNativeLifecycleType,
                    assertion.SourceFieldPath) != assertion.Id ||
                !payload.EvidenceBindings.Any(binding => Targets(binding, assertion)))
                issues.Add("A canonical record lifecycle assertion is invalid or lacks exact claim evidence.");
        }

        var relationshipClaimIds = payload.RelationshipAssertions
            .ToDictionary(EvidenceClaimContentId.DeriveV1);
        var correlationIds = payload.CorrelationEnvelopes.Select(value => value.Id).ToHashSet();
        foreach (var envelope in payload.CorrelatedRelationshipEnvelopes)
        {
            if (!locationRecords.ContainsKey(envelope.SubjectKnowledgeRecordId) ||
                envelope.CandidateTargetKnowledgeRecordIds.Any(value => !locationRecords.ContainsKey(value)) ||
                envelope.InputRelationshipClaimIds.Any(value => !relationshipClaimIds.ContainsKey(value)) ||
                envelope.CorrelationRecordIds.Any(value => !correlationIds.Contains(value)) ||
                CorrelatedRelationshipEnvelopeId.DeriveV1(
                    envelope.SubjectKnowledgeRecordId,
                    envelope.SemanticId,
                    envelope.CandidateTargetKnowledgeRecordIds,
                    envelope.InputRelationshipClaimIds,
                    envelope.CorrelationRecordIds,
                    envelope.MethodId,
                    envelope.MethodVersion,
                    envelope.Outcome) != envelope.Id)
                issues.Add("A correlated relationship envelope is invalid or not closed over its independent inputs.");
        }

        VerifyStrictLocationHierarchy(payload.RelationshipAssertions, issues);

        var coveredRecordIds = new List<KnowledgeRecordId>();
        foreach (var report in payload.LocationCoverageReports)
        {
            if (LocationCoverageReport.Create(
                    report.Manifest,
                    report.SourceFamilies,
                    report.SemanticCategories,
                    report.Terminology,
                    report.Hierarchy,
                    report.Relationships,
                    report.Unresolved).Id != report.Id ||
                !LocationReportScopeIsPermitted(report, manifest, payload))
                issues.Add("A Location coverage report identity or source scope is invalid.");

            foreach (var declaration in report.Manifest.SourceFamilies)
            {
                var descriptor = payload.AdapterDescriptors.FirstOrDefault(value =>
                    value.RevisionId == declaration.AdapterRevisionId);
                if (descriptor is null ||
                    !descriptor.Supports(report.Manifest.SourceScope.GameId, declaration.Format) ||
                    report.Manifest.IsClosed && declaration.ExpectedArtifactIds.Any(value =>
                        payload.Artifacts.All(artifact => artifact.Id != value)) ||
                    report.Manifest.IsClosed && declaration.ExpectedSourceRevisionIds.Any(value =>
                        !revisions.TryGetValue(value, out var revision) ||
                        revision.AdapterRevisionId != declaration.AdapterRevisionId ||
                        revision.SourceScope != report.Manifest.SourceScope ||
                        revision.ArtifactFormats.All(format => format.Format != declaration.Format)))
                    issues.Add("A Location source-family declaration is not closed over its adapter, format, artifacts, and revisions.");
            }

            foreach (var family in report.SourceFamilies)
            {
                var declaration = report.Manifest.SourceFamilies.FirstOrDefault(value =>
                    value.SourceFamilyId == family.SourceFamilyId);
                if (declaration is null ||
                    family.ArtifactIds.Any(value => payload.Artifacts.All(artifact => artifact.Id != value)) ||
                    family.SourceRevisionIds.Any(value => !revisions.ContainsKey(value)) ||
                    family.EmittedLocationRecordIds.Any(value => !locationRecords.ContainsKey(value)) ||
                    family.UnresolvedSourceAssertionIds.Any(value =>
                        payload.UnresolvedSourceAssertions.All(assertion => assertion.Id != value)) ||
                    family.EvidenceBackedExclusions.Any(exclusion =>
                        LocationCoverageExclusionId.DeriveV1(
                            exclusion.SourceRevisionId,
                            exclusion.NativeIdentity,
                            exclusion.ReasonCode,
                            exclusion.ExclusionRuleId,
                            exclusion.ExclusionRuleVersion,
                            exclusion.SourceFieldPath,
                            exclusion.SupportingEvidenceReceiptIds) != exclusion.Id ||
                        !family.SourceRevisionIds.Contains(exclusion.SourceRevisionId) ||
                        exclusion.SupportingEvidenceReceiptIds.Any(evidenceId =>
                            !evidenceIds.Contains(evidenceId) ||
                            !evidenceCoordinates.TryGetValue(evidenceId, out var coordinate) ||
                            coordinate.SourceRevisionId != exclusion.SourceRevisionId ||
                            !string.Equals(coordinate.FieldPath, exclusion.SourceFieldPath, StringComparison.Ordinal))))
                    issues.Add("A Location source-family coverage entry is not closed over the payload.");
                coveredRecordIds.AddRange(family.EmittedLocationRecordIds);
            }

            var reportRecords = report.SourceFamilies
                .SelectMany(value => value.EmittedLocationRecordIds)
                .ToHashSet();
            var reportRevisionIds = report.SourceFamilies
                .SelectMany(value => value.SourceRevisionIds)
                .ToHashSet();
            if (report.SemanticCategories.SelectMany(value => value.RecordIds)
                .Any(value => !reportRecords.Contains(value)))
                issues.Add("Location semantic-category coverage refers outside its source-family record set.");
            if (reportRecords.Any(recordId => report.SemanticCategories.All(category =>
                    !category.RecordIds.Contains(recordId))))
                issues.Add("Every covered Location requires an exact semantic or unclassified category state.");

            foreach (var category in report.SemanticCategories)
            {
                if (category.ExactNativeType is not null && category.RecordIds.Any(recordId =>
                        !payload.SourceNativeLocationTypeAssertions.Any(assertion =>
                            assertion.KnowledgeRecordId == recordId &&
                            reportRevisionIds.Contains(assertion.SourceRevisionId) &&
                            assertion.ExactNativeType == category.ExactNativeType)))
                    issues.Add("Location native-type coverage is not backed by exact type assertions.");
                if (category.SemanticRoleId is not null && category.RecordIds.Any(recordId =>
                        !payload.LocationSemanticClassificationAssertions.Any(assertion =>
                            assertion.KnowledgeRecordId == recordId &&
                            reportRevisionIds.Contains(assertion.SourceRevisionId) &&
                            assertion.RoleId == category.SemanticRoleId.Value)))
                    issues.Add("Location semantic-role coverage is not backed by exact classification assertions.");
                var expectedUnclassified = category.SemanticRoleId is null
                    ? category.RecordIds.Count(recordId => payload.LocationSemanticClassificationAssertions.All(assertion =>
                        assertion.KnowledgeRecordId != recordId ||
                        !reportRevisionIds.Contains(assertion.SourceRevisionId)))
                    : 0;
                if (category.UnclassifiedCandidateCount != expectedUnclassified)
                    issues.Add("Location unclassified coverage does not match the exact classification assertions.");
            }
            var declaredRoles = report.Manifest.SourceFamilies
                .SelectMany(value => value.ExpectedSemanticRoles)
                .ToHashSet();
            if (report.SemanticCategories
                .Where(value => value.SemanticRoleId is not null)
                .Any(value => !declaredRoles.Contains(value.SemanticRoleId!.Value)))
                issues.Add("Location semantic coverage includes a role absent from the exact source-family manifest.");

            var actualPrimaryNamed = reportRecords.Count(recordId => payload.TerminologyAssertions.Any(value =>
                value.KnowledgeRecordId == recordId &&
                reportRevisionIds.Contains(value.SourceRevisionId) &&
                value.Role == TerminologyAssertionRole.PrimaryName));
            var actualAliases = payload.TerminologyAssertions.Count(value =>
                reportRecords.Contains(value.KnowledgeRecordId) &&
                reportRevisionIds.Contains(value.SourceRevisionId) &&
                value.Role == TerminologyAssertionRole.Alias);
            if (report.Terminology.TotalRecordCount != reportRecords.Count ||
                report.Terminology.PrimaryNamedRecordCount != actualPrimaryNamed ||
                report.Terminology.IdentifierOnlyRecordCount != reportRecords.Count - actualPrimaryNamed ||
                report.Terminology.AliasAssertionCount != actualAliases ||
                report.Terminology.ConflictingRecordCount != reportRecords.Count(recordId =>
                    payload.TerminologyAssertions
                        .Where(value => value.KnowledgeRecordId == recordId &&
                            reportRevisionIds.Contains(value.SourceRevisionId) &&
                            value.Role == TerminologyAssertionRole.PrimaryName)
                        .Select(value => value.VerbatimValue)
                        .Distinct(StringComparer.Ordinal)
                        .Skip(1)
                        .Any()))
                issues.Add("Location terminology coverage does not match the exact assertions.");

            var reportRelationships = payload.RelationshipAssertions
                .Where(value => reportRecords.Contains(value.SubjectKnowledgeRecordId) &&
                    reportRevisionIds.Contains(value.SourceRevisionId))
                .ToImmutableArray();
            var strict = reportRelationships.Where(value =>
                LocationRelationshipSemantics.IsStrictHierarchy(value.SemanticId)).ToImmutableArray();
            var reportRelationshipClaimIds = reportRelationships.Select(EvidenceClaimContentId.DeriveV1).ToHashSet();
            bool CorrelationBelongsToReport(
                CorrelatedRelationshipEnvelope value,
                HashSet<EvidenceClaimContentId> ownedClaimIds)
            {
                if (!value.InputRelationshipClaimIds.IsEmpty)
                    return value.InputRelationshipClaimIds.All(ownedClaimIds.Contains);
                return !value.CorrelationRecordIds.IsEmpty && value.CorrelationRecordIds.All(id =>
                    payload.CorrelationEnvelopes.FirstOrDefault(item => item.Id == id) is { } correlation &&
                    !correlation.SupportingEvidenceReceiptIds.IsEmpty &&
                    correlation.SupportingEvidenceReceiptIds.All(receiptId =>
                        evidenceCoordinates.TryGetValue(receiptId, out var coordinate) &&
                        reportRevisionIds.Contains(coordinate.SourceRevisionId)));
            }
            var strictAmbiguities = payload.CorrelatedRelationshipEnvelopes.Count(value =>
                reportRecords.Contains(value.SubjectKnowledgeRecordId) &&
                CorrelationBelongsToReport(value, reportRelationshipClaimIds) &&
                LocationRelationshipSemantics.IsStrictHierarchy(value.SemanticId) &&
                value.Outcome == CorrelationOutcome.Ambiguous);
            if (report.Hierarchy.SourceProvidedEdgeCount != strict.Length ||
                report.Hierarchy.ResolvedTargetCount != strict.Count(value => value.Resolution == CanonicalResolutionState.Resolved) ||
                report.Hierarchy.UnresolvedTargetCount != strict.Count(value => value.Resolution == CanonicalResolutionState.Unresolved) ||
                report.Hierarchy.ConflictCount != strictAmbiguities ||
                report.Hierarchy.NotProvidedBySource != strict.IsEmpty)
                issues.Add("Location hierarchy coverage does not match the exact relationship assertions.");

            var actualSemantics = reportRelationships.Select(value => value.SemanticId).Distinct().ToHashSet();
            if (!actualSemantics.SetEquals(report.Relationships.Select(value => value.SemanticId)))
                issues.Add("Location relationship coverage semantics do not match the exact assertions.");
            foreach (var relationshipCoverage in report.Relationships)
            {
                var matching = reportRelationships.Where(value => value.SemanticId == relationshipCoverage.SemanticId).ToImmutableArray();
                var claimIds = matching.Select(EvidenceClaimContentId.DeriveV1).ToHashSet();
                var matchingBindings = payload.EvidenceBindings.Where(value =>
                    value.ClaimKind == EvidenceClaimKind.Relationship &&
                    value.ClaimContentId is { } claimId && claimIds.Contains(claimId)).ToImmutableArray();
                var fileCount = matching.Count(assertion => matchingBindings.Any(binding =>
                    binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion) &&
                    payload.FileEvidenceReceipts.Any(receipt => receipt.Id == binding.EvidenceReceiptId)));
                var referenceCount = matching.Count(assertion => matchingBindings.Any(binding =>
                    binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion) &&
                    payload.ReferenceEvidenceReceipts.Any(receipt => receipt.Id == binding.EvidenceReceiptId)));
                var ambiguityCount = payload.CorrelatedRelationshipEnvelopes.Count(value =>
                    reportRecords.Contains(value.SubjectKnowledgeRecordId) &&
                    CorrelationBelongsToReport(value, claimIds) &&
                    value.SemanticId == relationshipCoverage.SemanticId &&
                    value.Outcome == CorrelationOutcome.Ambiguous);
                if (relationshipCoverage.AssertionCount != matching.Length ||
                    relationshipCoverage.ResolvedTargetCount != matching.Count(value => value.Resolution == CanonicalResolutionState.Resolved) ||
                    relationshipCoverage.UnresolvedTargetCount != matching.Count(value => value.Resolution == CanonicalResolutionState.Unresolved) ||
                    relationshipCoverage.FileVerifiedCount != fileCount ||
                    relationshipCoverage.ReferenceVerifiedCount != referenceCount ||
                    relationshipCoverage.ConflictCount != ambiguityCount)
                    issues.Add("Location relationship coverage does not match the exact assertions and evidence classes.");
            }

            var expectedUnresolved = report.SourceFamilies
                .SelectMany(family => family.UnresolvedSourceAssertionIds.Select(id =>
                    (family.SourceFamilyId, Assertion: payload.UnresolvedSourceAssertions.Single(value => value.Id == id))))
                .GroupBy(value => new
                {
                    value.Assertion.ReasonCode,
                    value.Assertion.CandidateKind,
                    value.SourceFamilyId,
                    value.Assertion.SourceRevisionId,
                })
                .Select(value => new
                {
                    value.Key.ReasonCode,
                    value.Key.CandidateKind,
                    value.Key.SourceFamilyId,
                    value.Key.SourceRevisionId,
                    Count = value.Count(),
                })
                .ToImmutableArray();
            if (expectedUnresolved.Length != report.Unresolved.Length ||
                expectedUnresolved.Any(expected => !report.Unresolved.Any(actual =>
                    string.Equals(actual.ReasonCode, expected.ReasonCode, StringComparison.Ordinal) &&
                    actual.CandidateKind == expected.CandidateKind &&
                    actual.SourceFamilyId == expected.SourceFamilyId &&
                    actual.SourceRevisionId == expected.SourceRevisionId &&
                    actual.Count == expected.Count)))
                issues.Add("Location unresolved coverage does not match the exact unresolved assertions.");
        }

        if (coveredRecordIds.Count != locationRecords.Count ||
            coveredRecordIds.Distinct().Count() != locationRecords.Count ||
            coveredRecordIds.Any(value => !locationRecords.ContainsKey(value)))
            issues.Add("Every schema-v4 Location must be covered exactly once by a Location coverage report.");
    }

    private static void VerifyStrictLocationHierarchy(
        ImmutableArray<RelationshipAssertion> assertions,
        ImmutableArray<string>.Builder issues)
    {
        foreach (var group in assertions
                     .Where(value => LocationRelationshipSemantics.IsStrictHierarchy(value.SemanticId) &&
                         value.ResolvedTargetKnowledgeRecordId is not null)
                     .GroupBy(value => value.SourceRevisionId))
        {
            var graph = group.GroupBy(value => value.SubjectKnowledgeRecordId)
                .ToDictionary(
                    value => value.Key,
                    value => value.Select(item => item.ResolvedTargetKnowledgeRecordId!.Value).ToImmutableArray());
            var visiting = new HashSet<KnowledgeRecordId>();
            var visited = new HashSet<KnowledgeRecordId>();
            bool HasCycle(KnowledgeRecordId node)
            {
                if (visiting.Contains(node)) return true;
                if (!visited.Add(node)) return false;
                visiting.Add(node);
                if (graph.TryGetValue(node, out var targets) && targets.Any(HasCycle)) return true;
                visiting.Remove(node);
                return false;
            }
            if (graph.Keys.Any(HasCycle))
                issues.Add("Strict Location hierarchy edges contain a cycle within one source revision.");
        }
    }

    private static void VerifyProjectionContract(
        CatalogPackageManifest manifest,
        CanonicalCatalogPayload payload,
        ImmutableArray<string>.Builder issues)
    {
        if (payload.SemanticClassificationAssertions.IsDefault ||
            payload.RecordContributionAssertions.IsDefault ||
            payload.OrganizationalValueAssertions.IsDefault ||
            payload.InstructionAssertions.IsDefault ||
            payload.InstructionEvidenceBindings.IsDefault ||
            payload.InstructionConflictGroups.IsDefault)
        {
            issues.Add("Schema-v5 projection and Instructions collections must be initialized.");
            return;
        }
        var hasProjectionContent =
            !payload.SemanticClassificationAssertions.IsEmpty ||
            !payload.RecordContributionAssertions.IsEmpty ||
            !payload.OrganizationalValueAssertions.IsEmpty ||
            !payload.InstructionAssertions.IsEmpty ||
            !payload.InstructionEvidenceBindings.IsEmpty ||
            !payload.InstructionConflictGroups.IsEmpty;

        if (manifest.PackageSchemaVersion is not (CatalogPackageManifest.ProjectionContractSchemaVersion or
            CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
            CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion))
        {
            if (hasProjectionContent)
                issues.Add("Only schema-v5 packages may carry selector projection or Instructions assertions.");
            return;
        }

        var records = payload.KnowledgeRecords.ToDictionary(value => value.Id);
        var revisions = payload.SourceRevisions.ToDictionary(value => value.Revision.Id);
        var artifacts = payload.Artifacts.ToDictionary(value => value.Id);
        var fileEvidence = payload.FileEvidenceReceipts.ToDictionary(value => value.Id);
        var referenceEvidence = payload.ReferenceEvidenceReceipts.ToDictionary(value => value.Id);

        if (payload.SemanticClassificationAssertions.Select(value => value.Id).Distinct().Count() !=
            payload.SemanticClassificationAssertions.Length)
            issues.Add("Semantic classification assertion identities must be distinct.");
        foreach (var assertion in payload.SemanticClassificationAssertions)
        {
            if (!records.TryGetValue(assertion.KnowledgeRecordId, out var record) ||
                !AssertionSourceIsPermitted(
                    manifest, payload, record, assertion.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.SemanticClassification,
                    EvidenceClaimContentId.DeriveV1(assertion)) ||
                !KnownProjectionRoleMatchesKind(assertion.RoleId, record.Kind) ||
                CanonicalSemanticClassificationAssertionId.DeriveV1(
                    assertion.KnowledgeRecordId,
                    assertion.SourceRevisionId,
                    assertion.RoleId,
                    assertion.VocabularyId,
                    assertion.VocabularyVersion,
                    assertion.ClassificationMethodId,
                    assertion.ClassificationMethodVersion,
                    assertion.SourceFieldPath) != assertion.Id ||
                !payload.EvidenceBindings.Any(binding => Targets(binding, assertion)))
                issues.Add("A semantic classification assertion is invalid or lacks exact claim evidence.");
        }

        if (payload.RecordContributionAssertions.Select(value => value.Id).Distinct().Count() !=
            payload.RecordContributionAssertions.Length)
            issues.Add("Record contribution assertion identities must be distinct.");
        foreach (var assertion in payload.RecordContributionAssertions)
        {
            var sourceScopeIsValid = revisions.TryGetValue(assertion.SourceRevisionId, out var revision) &&
                (revision.SourceScope.ScopeKind == KnowledgeSourceScopeKind.BaseGame
                    ? assertion.ExactModIdentity is null && assertion.ExactModVersion is null
                    : assertion.ExactModIdentity == revision.SourceScope.ExactModIdentity &&
                      assertion.ExactModVersion == revision.SourceScope.ExactModVersion);
            var originIsValid = assertion.OriginKnowledgeRecordId is not KnowledgeRecordId origin ||
                records.TryGetValue(origin, out var originRecord) &&
                origin != assertion.KnowledgeRecordId &&
                records.TryGetValue(assertion.KnowledgeRecordId, out var contributedRecord) &&
                originRecord.GameId == contributedRecord.GameId && originRecord.Kind == contributedRecord.Kind;
            if (!records.TryGetValue(assertion.KnowledgeRecordId, out var record) ||
                !AssertionSourceIsPermitted(
                    manifest, payload, record, assertion.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.RecordContribution,
                    EvidenceClaimContentId.DeriveV1(assertion)) ||
                !sourceScopeIsValid ||
                !originIsValid ||
                CanonicalRecordContributionAssertionId.DeriveV1(
                    assertion.KnowledgeRecordId,
                    assertion.SourceRevisionId,
                    assertion.ContributionKind,
                    assertion.OriginKnowledgeRecordId,
                    assertion.ExactModIdentity,
                    assertion.ExactModVersion,
                    assertion.MethodId,
                    assertion.MethodVersion,
                    assertion.SourceFieldPath) != assertion.Id ||
                !payload.EvidenceBindings.Any(binding => Targets(binding, assertion)))
                issues.Add("A record contribution assertion is invalid or lacks exact claim evidence.");
        }

        if (payload.OrganizationalValueAssertions.Select(value => value.Id).Distinct().Count() !=
            payload.OrganizationalValueAssertions.Length)
            issues.Add("Organizational value assertion identities must be distinct.");
        foreach (var assertion in payload.OrganizationalValueAssertions)
        {
            if (!records.TryGetValue(assertion.KnowledgeRecordId, out var record) ||
                !AssertionSourceIsPermitted(
                    manifest, payload, record, assertion.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.OrganizationalValue,
                    EvidenceClaimContentId.DeriveV1(assertion)) ||
                !KnownOrganizationalDimensionMatchesKind(assertion.DimensionId, record.Kind) ||
                CanonicalOrganizationalValueAssertionId.DeriveV1(
                    assertion.KnowledgeRecordId,
                    assertion.SourceRevisionId,
                    assertion.DimensionId,
                    assertion.ExactValueIdentity,
                    assertion.VerbatimDisplayValue,
                    assertion.MethodId,
                    assertion.MethodVersion,
                    assertion.SourceFieldPath) != assertion.Id ||
                !payload.EvidenceBindings.Any(binding => Targets(binding, assertion)))
                issues.Add("An organizational value assertion is invalid or lacks exact claim evidence.");
        }

        if (payload.InstructionAssertions.Select(value => value.Id).Distinct().Count() !=
            payload.InstructionAssertions.Length)
            issues.Add("Instruction assertion identities must be distinct.");
        foreach (var assertion in payload.InstructionAssertions)
        {
            var sourceLocatorClosed = assertion.SourceLocator.ArtifactId is not SourceArtifactId artifactId ||
                artifacts.ContainsKey(artifactId) &&
                revisions.TryGetValue(assertion.SourceRevisionId, out var sourceRevision) &&
                sourceRevision.Revision.ArtifactIds.Contains(artifactId);
            if (!revisions.ContainsKey(assertion.SourceRevisionId) ||
                !SourceScopeMatchesManifest(assertion.SourceScope, manifest) ||
                !sourceLocatorClosed ||
                assertion.ApplicableRecordIds.Any(id => !records.ContainsKey(id)) ||
                manifest.PackageSchemaVersion is (CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
                    CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion) &&
                assertion.ApplicableRecordIds.Any(id =>
                    records[id].SourceRevisionId != assertion.SourceRevisionId &&
                    !HasMatchingCrossSourceEnvelope(
                        payload,
                        records[id],
                        assertion.SourceRevisionId,
                        CrossSourceCanonicalAssertionKind.InstructionApplicability,
                        EvidenceClaimContentId.DeriveV1(assertion))) ||
                InstructionAssertionId.DeriveV1(
                    assertion.SourceRevisionId,
                    assertion.SourceScope,
                    assertion.NativeIdentity,
                    assertion.CategoryId,
                    assertion.Content.RetentionMode,
                    assertion.Content.ContentDigest,
                    assertion.SourceLocator,
                    assertion.ExactLanguageTag,
                    assertion.CategoryMappingMethodId,
                    assertion.CategoryMappingMethodVersion,
                    assertion.ApplicableRecordIds,
                    assertion.ApplicablePackageIds) != assertion.Id)
                issues.Add("An Instruction assertion is invalid or is outside the package closure.");
        }

        if (payload.InstructionEvidenceBindings.Select(value => value.Id).Distinct().Count() !=
            payload.InstructionEvidenceBindings.Length)
            issues.Add("Instruction evidence binding identities must be distinct.");
        foreach (var binding in payload.InstructionEvidenceBindings)
        {
            var assertion = payload.InstructionAssertions.FirstOrDefault(value => value.Id == binding.InstructionAssertionId);
            if (assertion is null ||
                binding.SourceRevisionId != assertion.SourceRevisionId ||
                binding.ClaimContentId != EvidenceClaimContentId.DeriveV1(assertion) ||
                !string.Equals(binding.ExactLocator, assertion.SourceLocator.ExactFieldPathOrFragment, StringComparison.Ordinal) ||
                !InstructionEvidenceMatchesExactSource(
                    assertion,
                    binding,
                    revisions,
                    fileEvidence,
                    referenceEvidence) ||
                !InstructionEvidenceBindingIdentityMatches(binding))
                issues.Add("An Instruction evidence binding is invalid.");
        }
        foreach (var assertion in payload.InstructionAssertions)
            if (!payload.InstructionEvidenceBindings.Any(value => value.InstructionAssertionId == assertion.Id))
                issues.Add("An Instruction assertion lacks exact claim evidence.");

        if (payload.InstructionConflictGroups.Select(value => value.Id).Distinct().Count() !=
            payload.InstructionConflictGroups.Length)
            issues.Add("Instruction conflict-group identities must be distinct.");
        foreach (var conflict in payload.InstructionConflictGroups)
        {
            if (conflict.MemberIds.Any(id => payload.InstructionAssertions.All(value => value.Id != id)) ||
                InstructionConflictGroupId.DeriveV1(conflict.MemberIds, conflict.MethodId, conflict.MethodVersion) != conflict.Id)
                issues.Add("An Instruction conflict group is invalid or refers outside the payload.");
        }
    }

    private static void VerifyCrossSourceAssertions(
        CatalogPackageManifest manifest,
        CanonicalCatalogPayload payload,
        ImmutableArray<string>.Builder issues)
    {
        if (payload.CrossSourceAssertions.IsDefault)
        {
            issues.Add("Cross-source assertion collection must be initialized.");
            return;
        }
        if (payload.CrossSourceTargetLinkClaims.IsDefault)
        {
            issues.Add("Cross-source target-link claim collection must be initialized.");
            return;
        }
        if (payload.UnresolvedCrossSourceClaimContents.IsDefault ||
            payload.UnresolvedCrossSourceEvidenceBindings.IsDefault ||
            payload.UnresolvedCrossSourceAssertions.IsDefault)
        {
            issues.Add("Unresolved cross-source collections must be initialized.");
            return;
        }
        if (manifest.PackageSchemaVersion is not (CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
            CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion))
        {
            if (!payload.CrossSourceAssertions.IsEmpty || !payload.CrossSourceTargetLinkClaims.IsEmpty ||
                !payload.UnresolvedCrossSourceClaimContents.IsEmpty ||
                !payload.UnresolvedCrossSourceEvidenceBindings.IsEmpty ||
                !payload.UnresolvedCrossSourceAssertions.IsEmpty)
                issues.Add("Only schema-v6 packages may carry cross-source canonical assertion state.");
            return;
        }
        if (payload.CrossSourceAssertions.Select(value => value.Id).Distinct().Count() !=
            payload.CrossSourceAssertions.Length)
            issues.Add("Cross-source assertion identities must be distinct.");
        if (payload.CrossSourceTargetLinkClaims.Select(value => value.Id).Distinct().Count() !=
            payload.CrossSourceTargetLinkClaims.Length)
            issues.Add("Cross-source target-link claim identities must be distinct.");
        if (payload.UnresolvedCrossSourceClaimContents.Select(value => value.Id).Distinct().Count() !=
            payload.UnresolvedCrossSourceClaimContents.Length ||
            payload.UnresolvedCrossSourceEvidenceBindings.Select(value => value.Id).Distinct().Count() !=
            payload.UnresolvedCrossSourceEvidenceBindings.Length ||
            payload.UnresolvedCrossSourceAssertions.Select(value => value.Id).Distinct().Count() !=
            payload.UnresolvedCrossSourceAssertions.Length)
            issues.Add("Unresolved cross-source identities must be distinct.");

        var records = payload.KnowledgeRecords.ToDictionary(value => value.Id);
        var revisions = payload.SourceRevisions.ToDictionary(value => value.Revision.Id);
        var receipts = payload.FileEvidenceReceipts.Select(value => (
                value.Id, value.Receipt.SourceRevisionId, ArtifactId: value.Receipt.SourceArtifactId,
                FieldPath: value.Receipt.SourceFieldPath, value.Receipt.NativeObjectIdentity,
                Kind: EvidenceVerificationKind.FileVerified))
            .Concat(payload.ReferenceEvidenceReceipts.Select(value => (
                value.Id, value.Receipt.SourceRevisionId, ArtifactId: value.Receipt.ResponseArtifactId,
                FieldPath: value.Receipt.ResponseFieldPath, value.Receipt.NativeObjectIdentity,
                Kind: EvidenceVerificationKind.ReferenceVerified)))
            .ToDictionary(value => value.Id);
        var bindings = payload.EvidenceBindings.ToDictionary(value => value.Id);
        var instructionBindings = payload.InstructionEvidenceBindings.ToDictionary(value => value.Id);
        var correlations = payload.CorrelationEnvelopes.ToDictionary(value => value.Id);
        var targetLinkClaims = payload.CrossSourceTargetLinkClaims.ToDictionary(value => value.Id);

        foreach (var claim in payload.CrossSourceTargetLinkClaims)
        {
            var expected = CrossSourceTargetLinkClaimId.DeriveV1(
                claim.TargetKnowledgeRecordId,
                claim.TargetOriginSourceRevisionId,
                claim.TargetNativeRecordIdentityId,
                claim.ExactTargetCoordinate,
                claim.TargetCoordinateArtifactId,
                claim.TargetCoordinateFieldPath,
                claim.AssertingSourceRevisionId,
                claim.ExactAssertingCoordinate,
                claim.AssertingCoordinateArtifactId,
                claim.AssertingCoordinateFieldPath,
                claim.LinkKind,
                claim.MethodId,
                claim.MethodVersion);
            if (expected != claim.Id ||
                !records.TryGetValue(claim.TargetKnowledgeRecordId, out var target) ||
                target.SourceRevisionId != claim.TargetOriginSourceRevisionId ||
                target.NativeRecordIdentityId != claim.TargetNativeRecordIdentityId ||
                !revisions.TryGetValue(claim.TargetOriginSourceRevisionId, out var originRevision) ||
                !revisions.TryGetValue(claim.AssertingSourceRevisionId, out var assertingRevision) ||
                assertingRevision.SourceScope.GameId != target.GameId ||
                !originRevision.Revision.ArtifactIds.Contains(claim.TargetCoordinateArtifactId) ||
                !assertingRevision.Revision.ArtifactIds.Contains(claim.TargetCoordinateArtifactId) ||
                !assertingRevision.Revision.ArtifactIds.Contains(claim.AssertingCoordinateArtifactId) ||
                !TargetLinkCoordinatesAreExact(claim, target) ||
                !payload.CrossSourceAssertions.Any(value => value.TargetLinkClaimId == claim.Id))
                issues.Add("A cross-source target-link claim is invalid, unproven, or orphaned.");
        }

        foreach (var envelope in payload.CrossSourceAssertions)
        {
            var expectedId = CrossSourceCanonicalAssertionId.DeriveV1(
                envelope.TargetKnowledgeRecordId,
                envelope.TargetOriginSourceRevisionId,
                envelope.TargetNativeRecordIdentityId,
                envelope.AssertingSourceRevisionId,
                envelope.AssertionKind,
                envelope.UnderlyingClaimContentId,
                envelope.TargetLinkClaimId,
                envelope.TargetLinkKind,
                envelope.ExactLinkKey,
                envelope.TargetLinkMethodId,
                envelope.TargetLinkMethodVersion,
                envelope.SupportingEvidenceReceiptIds,
                envelope.SupportingEvidenceBindingIds,
                envelope.SupportingInstructionEvidenceBindingIds,
                envelope.CorrelationRecordIds);
            if (expectedId != envelope.Id ||
                !records.TryGetValue(envelope.TargetKnowledgeRecordId, out var target) ||
                target.SourceRevisionId != envelope.TargetOriginSourceRevisionId ||
                target.NativeRecordIdentityId != envelope.TargetNativeRecordIdentityId ||
                !revisions.TryGetValue(envelope.TargetOriginSourceRevisionId, out var originRevision) ||
                !revisions.TryGetValue(envelope.AssertingSourceRevisionId, out var assertingRevision) ||
                originRevision.SourceScope.GameId != target.GameId ||
                assertingRevision.SourceScope.GameId != target.GameId ||
                !SourceScopeMatchesManifest(assertingRevision.SourceScope, manifest) ||
                !targetLinkClaims.TryGetValue(envelope.TargetLinkClaimId, out var targetLinkClaim) ||
                targetLinkClaim.TargetKnowledgeRecordId != target.Id ||
                targetLinkClaim.TargetOriginSourceRevisionId != target.SourceRevisionId ||
                targetLinkClaim.TargetNativeRecordIdentityId != target.NativeRecordIdentityId ||
                targetLinkClaim.AssertingSourceRevisionId != envelope.AssertingSourceRevisionId ||
                targetLinkClaim.LinkKind != envelope.TargetLinkKind ||
                targetLinkClaim.ExactAssertingCoordinate != envelope.ExactLinkKey ||
                !string.Equals(targetLinkClaim.MethodId, envelope.TargetLinkMethodId, StringComparison.Ordinal) ||
                !string.Equals(targetLinkClaim.MethodVersion, envelope.TargetLinkMethodVersion, StringComparison.Ordinal) ||
                !UnderlyingCrossSourceClaimExists(payload, envelope) ||
                !CrossSourceLinkKeyMatchesTypedClaim(payload, envelope, targetLinkClaim))
            {
                issues.Add("A cross-source assertion identity, target, source revision, or typed claim is invalid.");
                continue;
            }

            var selectedBindings = envelope.SupportingEvidenceBindingIds
                .Where(bindings.ContainsKey).Select(id => bindings[id]).ToImmutableArray();
            var selectedInstructionBindings = envelope.SupportingInstructionEvidenceBindingIds
                .Where(instructionBindings.ContainsKey).Select(id => instructionBindings[id]).ToImmutableArray();
            var referencedReceiptIds = selectedBindings.Select(value => value.EvidenceReceiptId)
                .Concat(selectedInstructionBindings.Select(value => value.EvidenceReceiptId))
                .Distinct()
                .OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
            var declaredReceiptIds = envelope.SupportingEvidenceReceiptIds
                .OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
            var hasOriginIdentityEvidence = selectedBindings.Any(binding =>
                binding.KnowledgeRecordId == target.Id &&
                binding.SourceRevisionId == target.SourceRevisionId &&
                binding.ClaimKind == EvidenceClaimKind.KnowledgeIdentity &&
                binding.ClaimContentId is null);
            var hasAssertionEvidence = envelope.AssertionKind == CrossSourceCanonicalAssertionKind.InstructionApplicability
                ? selectedInstructionBindings.Any(binding =>
                    binding.SourceRevisionId == envelope.AssertingSourceRevisionId &&
                    binding.ClaimContentId == envelope.UnderlyingClaimContentId)
                : selectedBindings.Any(binding =>
                    binding.KnowledgeRecordId == target.Id &&
                    binding.SourceRevisionId == envelope.AssertingSourceRevisionId &&
                    binding.ClaimKind == envelope.ClaimKind &&
                    binding.ClaimContentId == envelope.UnderlyingClaimContentId);
            var compositeAssertionEvidenceClosed =
                envelope.AssertionKind != CrossSourceCanonicalAssertionKind.OrganizationalValue ||
                payload.OrganizationalValueAssertions
                    .Where(value => EvidenceClaimContentId.DeriveV1(value) == envelope.UnderlyingClaimContentId)
                    .Any(value => CompositeClaimLocatorSetClosed(
                        value.SourceFieldPath,
                        selectedBindings.Where(binding =>
                                binding.ClaimKind == EvidenceClaimKind.OrganizationalValue &&
                                binding.ClaimContentId == envelope.UnderlyingClaimContentId)
                            .Select(binding => binding.ClaimLocator)));
            var targetLinkClaimContentId = EvidenceClaimContentId.DeriveV1(targetLinkClaim);
            var hasTargetCoordinateEvidence = selectedBindings.Any(binding =>
                binding.KnowledgeRecordId == target.Id &&
                binding.SourceRevisionId == envelope.AssertingSourceRevisionId &&
                binding.ClaimKind == EvidenceClaimKind.CrossSourceTargetLink &&
                binding.ClaimContentId == targetLinkClaimContentId &&
                string.Equals(binding.ClaimLocator, targetLinkClaim.TargetCoordinateFieldPath, StringComparison.Ordinal) &&
                receipts.TryGetValue(binding.EvidenceReceiptId, out var receipt) &&
                receipt.SourceRevisionId == envelope.AssertingSourceRevisionId &&
                receipt.ArtifactId == targetLinkClaim.TargetCoordinateArtifactId &&
                receipt.NativeObjectIdentity == target.NativeIdentity &&
                string.Equals(receipt.FieldPath, targetLinkClaim.TargetCoordinateFieldPath, StringComparison.Ordinal));
            var hasAssertingCoordinateEvidence = selectedBindings.Any(binding =>
                binding.KnowledgeRecordId == target.Id &&
                binding.SourceRevisionId == envelope.AssertingSourceRevisionId &&
                binding.ClaimKind == EvidenceClaimKind.CrossSourceTargetLink &&
                binding.ClaimContentId == targetLinkClaimContentId &&
                string.Equals(binding.ClaimLocator, targetLinkClaim.AssertingCoordinateFieldPath, StringComparison.Ordinal) &&
                receipts.TryGetValue(binding.EvidenceReceiptId, out var receipt) &&
                receipt.SourceRevisionId == envelope.AssertingSourceRevisionId &&
                receipt.ArtifactId == targetLinkClaim.AssertingCoordinateArtifactId &&
                receipt.NativeObjectIdentity == targetLinkClaim.ExactAssertingCoordinate &&
                string.Equals(receipt.FieldPath, targetLinkClaim.AssertingCoordinateFieldPath, StringComparison.Ordinal));
            var regularBindingsValid = selectedBindings.Length == envelope.SupportingEvidenceBindingIds.Length &&
                selectedBindings.All(binding =>
                    binding.KnowledgeRecordId == target.Id &&
                    (binding.SourceRevisionId == target.SourceRevisionId
                        ? binding.ClaimKind == EvidenceClaimKind.KnowledgeIdentity && binding.ClaimContentId is null
                        : binding.SourceRevisionId == envelope.AssertingSourceRevisionId &&
                          (binding.ClaimKind == envelope.ClaimKind &&
                           binding.ClaimContentId == envelope.UnderlyingClaimContentId ||
                           binding.ClaimKind == EvidenceClaimKind.CrossSourceTargetLink &&
                           binding.ClaimContentId == targetLinkClaimContentId)));
            var instructionBindingsValid = selectedInstructionBindings.Length ==
                    envelope.SupportingInstructionEvidenceBindingIds.Length &&
                (envelope.AssertionKind == CrossSourceCanonicalAssertionKind.InstructionApplicability
                    ? !selectedInstructionBindings.IsEmpty && selectedInstructionBindings.All(binding =>
                        binding.SourceRevisionId == envelope.AssertingSourceRevisionId &&
                        binding.ClaimContentId == envelope.UnderlyingClaimContentId)
                    : selectedInstructionBindings.IsEmpty);
            var correlationsValid = envelope.CorrelationRecordIds.All(id =>
                correlations.TryGetValue(id, out var correlation) &&
                correlation.Record.Outcome == CorrelationOutcome.Correlated &&
                correlation.Record.MemberIds.Contains(target.Id) &&
                (envelope.TargetLinkKind != CrossSourceTargetLinkKind.VerifiedDeterministicCorrelation ||
                 string.Equals(correlation.Record.MethodId, envelope.TargetLinkMethodId, StringComparison.Ordinal) &&
                 string.Equals(correlation.Record.MethodVersion, envelope.TargetLinkMethodVersion, StringComparison.Ordinal) &&
                 correlation.SupportingEvidenceReceiptIds.All(envelope.SupportingEvidenceReceiptIds.Contains) &&
                 correlation.Record.MemberIds.Any(memberId =>
                     memberId != target.Id && records.TryGetValue(memberId, out var member) &&
                     member.SourceRevisionId == envelope.AssertingSourceRevisionId &&
                     member.NativeIdentity == targetLinkClaim.ExactAssertingCoordinate)));
            if (referencedReceiptIds.Length != declaredReceiptIds.Length ||
                !referencedReceiptIds.SequenceEqual(declaredReceiptIds) ||
                referencedReceiptIds.Any(id => !receipts.ContainsKey(id)) ||
                !hasOriginIdentityEvidence ||
                !hasAssertionEvidence ||
                !compositeAssertionEvidenceClosed ||
                !hasTargetCoordinateEvidence ||
                !hasAssertingCoordinateEvidence ||
                !regularBindingsValid ||
                !instructionBindingsValid ||
                !correlationsValid ||
                envelope.TargetLinkKind != CrossSourceTargetLinkKind.VerifiedDeterministicCorrelation &&
                    !envelope.CorrelationRecordIds.IsEmpty ||
                envelope.TargetLinkKind == CrossSourceTargetLinkKind.VerifiedDeterministicCorrelation &&
                    envelope.CorrelationRecordIds.IsEmpty)
                issues.Add("A cross-source assertion lacks exact target-link or claim evidence closure.");
        }

        var unresolvedClaims = payload.UnresolvedCrossSourceClaimContents.ToDictionary(value => value.Id);
        var unresolvedBindings = payload.UnresolvedCrossSourceEvidenceBindings.ToDictionary(value => value.Id);
        foreach (var unresolved in payload.UnresolvedCrossSourceAssertions)
        {
            if (!unresolvedClaims.TryGetValue(unresolved.ClaimContentId, out var claim) ||
                UnresolvedCrossSourceClaimContentId.DeriveV1(
                    claim.AssertingSourceRevisionId, claim.ExactAssertingSubject, claim.AssertionKind,
                    claim.Terminology, claim.Relationship, claim.LocationNativeType,
                    claim.LocationSemanticClassification, claim.RecordLifecycle,
                    claim.SemanticClassification, claim.RecordContribution,
                    claim.OrganizationalValue, claim.InstructionApplicability) != claim.Id ||
                claim.AssertingSourceRevisionId != unresolved.AssertingSourceRevisionId ||
                !revisions.TryGetValue(unresolved.AssertingSourceRevisionId, out var revision) ||
                revision.AdapterRevisionId != unresolved.AdapterRevisionId ||
                !SourceScopeMatchesManifest(revision.SourceScope, manifest) ||
                unresolved.CandidateTargetIds.Any(id => !records.TryGetValue(id, out var candidate) ||
                    candidate.GameId != revision.SourceScope.GameId) ||
                unresolved.Outcome == CorrelationOutcome.Correlated ||
                UnresolvedCrossSourceAssertionId.DeriveV1(
                    unresolved.ClaimContentId, unresolved.AssertingSourceRevisionId,
                    unresolved.AdapterRevisionId, unresolved.AttemptedLinkKind,
                    unresolved.AttemptedLinkMethodId, unresolved.AttemptedLinkMethodVersion,
                    unresolved.Outcome, unresolved.ReasonCode, unresolved.CandidateTargetIds,
                    unresolved.SupportingEvidenceReceiptIds, unresolved.SupportingEvidenceBindingIds,
                    unresolved.CorrelationRecordIds) != unresolved.Id ||
                claim.AssertionKind == CrossSourceCanonicalAssertionKind.InstructionApplicability &&
                    payload.InstructionAssertions.All(value =>
                        value.Id != claim.InstructionApplicability!.InstructionAssertionId ||
                        value.SourceRevisionId != unresolved.AssertingSourceRevisionId))
            {
                issues.Add("An unresolved cross-source assertion identity, source, candidate, or typed claim is invalid.");
                continue;
            }

            var selected = unresolved.SupportingEvidenceBindingIds
                .Where(unresolvedBindings.ContainsKey).Select(id => unresolvedBindings[id]).ToImmutableArray();
            var correlationReceipts = unresolved.CorrelationRecordIds
                .Where(correlations.ContainsKey).SelectMany(id => correlations[id].SupportingEvidenceReceiptIds);
            var referenced = selected.Select(value => value.EvidenceReceiptId).Concat(correlationReceipts)
                .Distinct().OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
            var declared = unresolved.SupportingEvidenceReceiptIds
                .OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
            var correlationsClosed = unresolved.CorrelationRecordIds.All(id =>
                correlations.TryGetValue(id, out var correlation) &&
                unresolved.AttemptedLinkKind == CrossSourceTargetLinkKind.VerifiedDeterministicCorrelation &&
                correlation.Record.Outcome == unresolved.Outcome &&
                string.Equals(correlation.Record.MethodId, unresolved.AttemptedLinkMethodId, StringComparison.Ordinal) &&
                string.Equals(correlation.Record.MethodVersion, unresolved.AttemptedLinkMethodVersion, StringComparison.Ordinal) &&
                correlation.Record.MemberIds.SequenceEqual(unresolved.CandidateTargetIds));
            var expectedAttemptId = UnresolvedCrossSourceTargetAttemptId.DeriveV1(
                unresolved.ClaimContentId, unresolved.AssertingSourceRevisionId,
                claim.ExactAssertingSubject, unresolved.AttemptedLinkKind,
                unresolved.AttemptedLinkMethodId, unresolved.AttemptedLinkMethodVersion,
                unresolved.Outcome, unresolved.CandidateTargetIds, unresolved.CorrelationRecordIds);
            var bindingsClosed = selected.Length == unresolved.SupportingEvidenceBindingIds.Length &&
                selected.All(binding =>
                    binding.AssertingSourceRevisionId == unresolved.AssertingSourceRevisionId &&
                    binding.ClaimContentId == unresolved.ClaimContentId &&
                    binding.AssertionKind == claim.AssertionKind &&
                    (unresolved.CorrelationRecordIds.IsEmpty
                        ? binding.TargetAttemptId is null
                        : binding.TargetAttemptId == expectedAttemptId) &&
                    (claim.AssertionKind == CrossSourceCanonicalAssertionKind.OrganizationalValue
                        ? IsExactOrStructuredChildLocator(binding.ExactLocator, claim.SourceFieldPath)
                        : string.Equals(binding.ExactLocator, claim.SourceFieldPath, StringComparison.Ordinal)) &&
                    UnresolvedCrossSourceEvidenceBindingId.DeriveV1(
                        binding.EvidenceReceiptId, binding.AssertingSourceRevisionId,
                        binding.ClaimContentId, binding.TargetAttemptId, binding.AssertionKind, binding.ExactLocator,
                        binding.Verification) == binding.Id &&
                    receipts.TryGetValue(binding.EvidenceReceiptId, out var receipt) &&
                    receipt.SourceRevisionId == unresolved.AssertingSourceRevisionId &&
                    receipt.Kind == binding.Verification &&
                    receipt.NativeObjectIdentity == claim.ExactAssertingSubject &&
                    string.Equals(receipt.FieldPath, binding.ExactLocator, StringComparison.Ordinal));
            var correlationEvidenceClosed = unresolved.CorrelationRecordIds.IsEmpty ||
                unresolved.CorrelationRecordIds.All(id =>
                    correlations.TryGetValue(id, out var correlation) &&
                    !correlation.SupportingEvidenceReceiptIds.IsEmpty &&
                    correlation.SupportingEvidenceReceiptIds.All(receiptId => selected.Any(binding =>
                        binding.EvidenceReceiptId == receiptId && binding.TargetAttemptId == expectedAttemptId)));
            var compositeUnresolvedEvidenceClosed =
                claim.AssertionKind != CrossSourceCanonicalAssertionKind.OrganizationalValue ||
                CompositeClaimLocatorSetClosed(
                    claim.SourceFieldPath,
                    selected.Select(binding => binding.ExactLocator));
            if (!bindingsClosed || !compositeUnresolvedEvidenceClosed ||
                !correlationsClosed || !correlationEvidenceClosed ||
                referenced.Length != declared.Length || !referenced.SequenceEqual(declared))
                issues.Add("An unresolved cross-source assertion lacks exact evidence closure.");
        }

        foreach (var claim in payload.UnresolvedCrossSourceClaimContents)
            if (!payload.UnresolvedCrossSourceAssertions.Any(value => value.ClaimContentId == claim.Id))
                issues.Add("An unresolved cross-source typed claim is orphaned.");
        foreach (var binding in payload.UnresolvedCrossSourceEvidenceBindings)
            if (!payload.UnresolvedCrossSourceAssertions.Any(value => value.SupportingEvidenceBindingIds.Contains(binding.Id)))
                issues.Add("An unresolved cross-source evidence binding is orphaned.");
    }

    private static bool AssertionSourceIsPermitted(
        CatalogPackageManifest manifest,
        CanonicalCatalogPayload payload,
        CanonicalKnowledgeRecord record,
        CatalogSourceRevisionId assertingSourceRevisionId,
        CrossSourceCanonicalAssertionKind kind,
        EvidenceClaimContentId claimContentId) =>
        record.SourceRevisionId == assertingSourceRevisionId ||
        manifest.PackageSchemaVersion is (CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
            CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion) &&
        HasMatchingCrossSourceEnvelope(payload, record, assertingSourceRevisionId, kind, claimContentId);

    private static bool HasMatchingCrossSourceEnvelope(
        CanonicalCatalogPayload payload,
        CanonicalKnowledgeRecord record,
        CatalogSourceRevisionId assertingSourceRevisionId,
        CrossSourceCanonicalAssertionKind kind,
        EvidenceClaimContentId claimContentId) =>
        payload.CrossSourceAssertions.Any(value =>
            value.TargetKnowledgeRecordId == record.Id &&
            value.TargetOriginSourceRevisionId == record.SourceRevisionId &&
            value.TargetNativeRecordIdentityId == record.NativeRecordIdentityId &&
            value.AssertingSourceRevisionId == assertingSourceRevisionId &&
            value.AssertionKind == kind &&
            value.UnderlyingClaimContentId == claimContentId);

    private static bool EvidenceBindingSourceIsPermitted(
        CatalogPackageManifest manifest,
        CanonicalCatalogPayload payload,
        CanonicalKnowledgeRecord record,
        EvidenceBinding binding)
    {
        if (record.SourceRevisionId == binding.SourceRevisionId) return true;
        if (binding.ClaimKind == EvidenceClaimKind.KnowledgeIdentity ||
            binding.ClaimContentId is not EvidenceClaimContentId claimContentId ||
            manifest.PackageSchemaVersion is not (CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
                CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion))
            return false;
        return binding.ClaimKind == EvidenceClaimKind.CrossSourceTargetLink
            ? payload.CrossSourceAssertions.Any(value =>
                value.TargetKnowledgeRecordId == record.Id &&
                value.AssertingSourceRevisionId == binding.SourceRevisionId &&
                value.SupportingEvidenceBindingIds.Contains(binding.Id) &&
                payload.CrossSourceTargetLinkClaims.Any(claim =>
                    claim.Id == value.TargetLinkClaimId &&
                    claim.TargetKnowledgeRecordId == record.Id &&
                    claim.AssertingSourceRevisionId == binding.SourceRevisionId &&
                    EvidenceClaimContentId.DeriveV1(claim) == claimContentId))
            : payload.CrossSourceAssertions.Any(value =>
            value.TargetKnowledgeRecordId == record.Id &&
            value.TargetOriginSourceRevisionId == record.SourceRevisionId &&
            value.TargetNativeRecordIdentityId == record.NativeRecordIdentityId &&
            value.AssertingSourceRevisionId == binding.SourceRevisionId &&
            value.ClaimKind == binding.ClaimKind &&
            value.UnderlyingClaimContentId == claimContentId &&
            value.SupportingEvidenceBindingIds.Contains(binding.Id));
    }

    private static bool UnderlyingCrossSourceClaimExists(
        CanonicalCatalogPayload payload,
        CrossSourceCanonicalAssertion envelope) => envelope.AssertionKind switch
    {
        CrossSourceCanonicalAssertionKind.Terminology => payload.TerminologyAssertions.Any(value =>
            value.KnowledgeRecordId == envelope.TargetKnowledgeRecordId &&
            value.SourceRevisionId == envelope.AssertingSourceRevisionId &&
            EvidenceClaimContentId.DeriveV1(value) == envelope.UnderlyingClaimContentId),
        CrossSourceCanonicalAssertionKind.Relationship => payload.RelationshipAssertions.Any(value =>
            value.SubjectKnowledgeRecordId == envelope.TargetKnowledgeRecordId &&
            value.SourceRevisionId == envelope.AssertingSourceRevisionId &&
            EvidenceClaimContentId.DeriveV1(value) == envelope.UnderlyingClaimContentId),
        CrossSourceCanonicalAssertionKind.LocationNativeType => payload.SourceNativeLocationTypeAssertions.Any(value =>
            value.KnowledgeRecordId == envelope.TargetKnowledgeRecordId && value.SourceRevisionId == envelope.AssertingSourceRevisionId &&
            EvidenceClaimContentId.DeriveV1(value) == envelope.UnderlyingClaimContentId),
        CrossSourceCanonicalAssertionKind.LocationSemanticClassification => payload.LocationSemanticClassificationAssertions.Any(value =>
            value.KnowledgeRecordId == envelope.TargetKnowledgeRecordId && value.SourceRevisionId == envelope.AssertingSourceRevisionId &&
            EvidenceClaimContentId.DeriveV1(value) == envelope.UnderlyingClaimContentId),
        CrossSourceCanonicalAssertionKind.RecordLifecycle => payload.RecordLifecycleAssertions.Any(value =>
            value.KnowledgeRecordId == envelope.TargetKnowledgeRecordId && value.SourceRevisionId == envelope.AssertingSourceRevisionId &&
            EvidenceClaimContentId.DeriveV1(value) == envelope.UnderlyingClaimContentId),
        CrossSourceCanonicalAssertionKind.SemanticClassification => payload.SemanticClassificationAssertions.Any(value =>
            value.KnowledgeRecordId == envelope.TargetKnowledgeRecordId && value.SourceRevisionId == envelope.AssertingSourceRevisionId &&
            EvidenceClaimContentId.DeriveV1(value) == envelope.UnderlyingClaimContentId),
        CrossSourceCanonicalAssertionKind.RecordContribution => payload.RecordContributionAssertions.Any(value =>
            value.KnowledgeRecordId == envelope.TargetKnowledgeRecordId && value.SourceRevisionId == envelope.AssertingSourceRevisionId &&
            EvidenceClaimContentId.DeriveV1(value) == envelope.UnderlyingClaimContentId),
        CrossSourceCanonicalAssertionKind.OrganizationalValue => payload.OrganizationalValueAssertions.Any(value =>
            value.KnowledgeRecordId == envelope.TargetKnowledgeRecordId && value.SourceRevisionId == envelope.AssertingSourceRevisionId &&
            EvidenceClaimContentId.DeriveV1(value) == envelope.UnderlyingClaimContentId),
        CrossSourceCanonicalAssertionKind.InstructionApplicability => payload.InstructionAssertions.Any(value =>
            value.SourceRevisionId == envelope.AssertingSourceRevisionId &&
            value.ApplicableRecordIds.Contains(envelope.TargetKnowledgeRecordId) &&
            EvidenceClaimContentId.DeriveV1(value) == envelope.UnderlyingClaimContentId),
        _ => false,
    };

    private static bool CrossSourceLinkKeyMatchesTypedClaim(
        CanonicalCatalogPayload payload,
        CrossSourceCanonicalAssertion envelope,
        CrossSourceTargetLinkClaim targetLinkClaim) => envelope.TargetLinkKind switch
    {
        CrossSourceTargetLinkKind.ExactSourceNativeIdentity or
        CrossSourceTargetLinkKind.ExactPluginRecordIdentity =>
            targetLinkClaim.ExactTargetCoordinate == targetLinkClaim.ExactAssertingCoordinate,
        CrossSourceTargetLinkKind.ExactLabelOrHashKey =>
            envelope.AssertionKind == CrossSourceCanonicalAssertionKind.Terminology &&
            payload.TerminologyAssertions.Any(value =>
                value.KnowledgeRecordId == targetLinkClaim.TargetKnowledgeRecordId &&
                value.SourceRevisionId == envelope.AssertingSourceRevisionId &&
                value.NativeStringIdentifier == targetLinkClaim.ExactAssertingCoordinate &&
                EvidenceClaimContentId.DeriveV1(value) == envelope.UnderlyingClaimContentId),
        CrossSourceTargetLinkKind.ExactProviderNativeIdentity or
        CrossSourceTargetLinkKind.VersionedExactMapping or
        CrossSourceTargetLinkKind.VerifiedDeterministicCorrelation => true,
        _ => false,
    };

    private static bool TargetLinkCoordinatesAreExact(
        CrossSourceTargetLinkClaim claim,
        CanonicalKnowledgeRecord target) => claim.LinkKind switch
    {
        CrossSourceTargetLinkKind.ExactSourceNativeIdentity or
        CrossSourceTargetLinkKind.ExactPluginRecordIdentity =>
            claim.ExactTargetCoordinate == target.NativeIdentity &&
            claim.ExactAssertingCoordinate == target.NativeIdentity,
        CrossSourceTargetLinkKind.ExactLabelOrHashKey =>
            claim.ExactTargetCoordinate == claim.ExactAssertingCoordinate,
        CrossSourceTargetLinkKind.ExactProviderNativeIdentity or
        CrossSourceTargetLinkKind.VersionedExactMapping or
        CrossSourceTargetLinkKind.VerifiedDeterministicCorrelation => true,
        _ => false,
    };

    private static bool InstructionEvidenceMatchesExactSource(
        InstructionAssertion assertion,
        InstructionEvidenceBinding binding,
        Dictionary<CatalogSourceRevisionId, AdapterBoundCatalogSourceRevisionRecord> revisions,
        Dictionary<EvidenceReceiptId, CatalogFileEvidenceReceipt> fileEvidence,
        Dictionary<EvidenceReceiptId, CatalogReferenceEvidenceReceipt> referenceEvidence)
    {
        if (!revisions.TryGetValue(assertion.SourceRevisionId, out var revision)) return false;
        if (fileEvidence.TryGetValue(binding.EvidenceReceiptId, out var file))
        {
            var receipt = file.Receipt;
            var versionContractMatches = binding.Id.AlgorithmVersion switch
            {
                InstructionEvidenceBindingId.LegacyAlgorithmVersion =>
                    binding.Verification is null &&
                    file.Id.AlgorithmVersion == EvidenceReceiptId.LegacyAlgorithmVersion &&
                    receipt.NativeObjectIdentity is null,
                InstructionEvidenceBindingId.EvidenceClassAlgorithmVersion =>
                    binding.Verification == EvidenceVerificationKind.FileVerified &&
                    file.Id.AlgorithmVersion == EvidenceReceiptId.LegacyAlgorithmVersion &&
                    receipt.NativeObjectIdentity is null,
                InstructionEvidenceBindingId.CurrentAlgorithmVersion =>
                    binding.Verification == EvidenceVerificationKind.FileVerified &&
                    file.Id.AlgorithmVersion == EvidenceReceiptId.CurrentAlgorithmVersion &&
                    receipt.NativeObjectIdentity == assertion.SourceLocator.NativeObjectIdentity,
                _ => false,
            };
            return versionContractMatches &&
                   assertion.SourceLocator.ArtifactId is SourceArtifactId artifactId &&
                   assertion.SourceLocator.ProviderObjectRevision is null &&
                   artifactId == receipt.SourceArtifactId &&
                   binding.SourceRevisionId == receipt.SourceRevisionId &&
                   assertion.SourceRevisionId == receipt.SourceRevisionId &&
                   string.Equals(
                       assertion.SourceLocator.NativeObjectIdentity.ExactRepresentation,
                       receipt.NativeRecordLocator,
                       StringComparison.Ordinal) &&
                   string.Equals(assertion.SourceLocator.ExactFieldPathOrFragment, receipt.SourceFieldPath, StringComparison.Ordinal) &&
                   string.Equals(binding.ExactLocator, receipt.SourceFieldPath, StringComparison.Ordinal);
        }

        if (referenceEvidence.TryGetValue(binding.EvidenceReceiptId, out var reference))
        {
            var receipt = reference.Receipt;
            var versionContractMatches = binding.Id.AlgorithmVersion switch
            {
                InstructionEvidenceBindingId.LegacyAlgorithmVersion => binding.Verification is null,
                InstructionEvidenceBindingId.EvidenceClassAlgorithmVersion or
                InstructionEvidenceBindingId.CurrentAlgorithmVersion =>
                    binding.Verification == EvidenceVerificationKind.ReferenceVerified,
                _ => false,
            };
            return versionContractMatches &&
                   reference.Id.AlgorithmVersion == EvidenceReceiptId.LegacyAlgorithmVersion &&
                   assertion.SourceLocator.ArtifactId is SourceArtifactId artifactId &&
                   artifactId == receipt.ResponseArtifactId &&
                   revision.Revision.SourceId == receipt.ProviderCatalogSourceId &&
                   binding.SourceRevisionId == receipt.SourceRevisionId &&
                   assertion.SourceRevisionId == receipt.SourceRevisionId &&
                   assertion.SourceLocator.NativeObjectIdentity == receipt.NativeObjectIdentity &&
                   assertion.SourceLocator.ProviderObjectRevision == receipt.NativeRevisionIdentity &&
                   string.Equals(assertion.SourceLocator.ExactFieldPathOrFragment, receipt.ResponseFieldPath, StringComparison.Ordinal) &&
                   string.Equals(binding.ExactLocator, receipt.ResponseFieldPath, StringComparison.Ordinal);
        }

        return false;
    }

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

    private static bool InstructionEvidenceBindingIdentityMatches(InstructionEvidenceBinding binding) =>
        binding.Id.AlgorithmVersion switch
        {
            InstructionEvidenceBindingId.LegacyAlgorithmVersion when binding.Verification is null =>
                InstructionEvidenceBindingId.DeriveV1(
                    binding.EvidenceReceiptId,
                    binding.InstructionAssertionId,
                    binding.SourceRevisionId,
                    binding.ExactLocator,
                    binding.ClaimContentId) == binding.Id,
            InstructionEvidenceBindingId.EvidenceClassAlgorithmVersion when binding.Verification is { } v2 =>
                InstructionEvidenceBindingId.DeriveV2(
                    binding.EvidenceReceiptId,
                    binding.InstructionAssertionId,
                    binding.SourceRevisionId,
                    binding.ExactLocator,
                    binding.ClaimContentId,
                    v2) == binding.Id,
            InstructionEvidenceBindingId.CurrentAlgorithmVersion when binding.Verification is { } v3 =>
                InstructionEvidenceBindingId.DeriveV3(
                    binding.EvidenceReceiptId,
                    binding.InstructionAssertionId,
                    binding.SourceRevisionId,
                    binding.ExactLocator,
                    binding.ClaimContentId,
                    v3) == binding.Id,
            _ => false,
        };

    private static bool KnownProjectionRoleMatchesKind(CanonicalSemanticRoleId roleId, KnowledgeKind kind)
    {
        if (roleId == CanonicalProjectionSemantics.MissionDlc ||
            roleId == CanonicalProjectionSemantics.MissionMod ||
            roleId == CanonicalProjectionSemantics.MissionOnline ||
            roleId == CanonicalProjectionSemantics.MissionStoryMode)
            return kind == KnowledgeKind.MissionQuest;
        if (roleId == CanonicalProjectionSemantics.ItemArmor ||
            roleId == CanonicalProjectionSemantics.ItemClothing ||
            roleId == CanonicalProjectionSemantics.ItemClutterProps ||
            roleId == CanonicalProjectionSemantics.ItemMagic ||
            roleId == CanonicalProjectionSemantics.ItemNature ||
            roleId == CanonicalProjectionSemantics.ItemWeapons)
            return kind == KnowledgeKind.Item;
        if (roleId == CanonicalProjectionSemantics.ActorNpc ||
            roleId == CanonicalProjectionSemantics.ActorPlayerCharacter)
            return kind == KnowledgeKind.Actor;
        return true;
    }

    private static bool KnownOrganizationalDimensionMatchesKind(
        CanonicalOrganizationalSemanticId dimensionId,
        KnowledgeKind kind) =>
        dimensionId == CanonicalProjectionSemantics.ItemSourceCategoryDimensionNode
            ? kind == KnowledgeKind.Item
            : dimensionId == CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode
                ? kind == KnowledgeKind.MissionQuest
            : dimensionId != CanonicalProjectionSemantics.ActorDlcDimensionNode &&
              dimensionId != CanonicalProjectionSemantics.ActorFactionDimensionNode ||
              kind == KnowledgeKind.Actor;

    private static bool TargetsExactClaim(EvidenceBinding binding, CanonicalCatalogPayload payload) =>
        binding.ClaimKind switch
        {
            EvidenceClaimKind.KnowledgeIdentity => binding.ClaimContentId is null,
            EvidenceClaimKind.Terminology => payload.TerminologyAssertions.Any(value => Targets(binding, value)),
            EvidenceClaimKind.Relationship => payload.RelationshipAssertions.Any(value => Targets(binding, value)),
            EvidenceClaimKind.LocationNativeType => payload.SourceNativeLocationTypeAssertions.Any(value => Targets(binding, value)),
            EvidenceClaimKind.LocationSemanticClassification => payload.LocationSemanticClassificationAssertions.Any(value => Targets(binding, value)),
            EvidenceClaimKind.RecordLifecycle => payload.RecordLifecycleAssertions.Any(value => Targets(binding, value)),
            EvidenceClaimKind.SemanticClassification => payload.SemanticClassificationAssertions.Any(value => Targets(binding, value)),
            EvidenceClaimKind.RecordContribution => payload.RecordContributionAssertions.Any(value => Targets(binding, value)),
            EvidenceClaimKind.OrganizationalValue => payload.OrganizationalValueAssertions.Any(value => Targets(binding, value)),
            EvidenceClaimKind.CrossSourceTargetLink => payload.CrossSourceTargetLinkClaims.Any(value => Targets(binding, value)),
            _ => false,
        };

    private static bool Targets(EvidenceBinding binding, TerminologyAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.Terminology &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool Targets(EvidenceBinding binding, RelationshipAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.Relationship &&
        binding.KnowledgeRecordId == assertion.SubjectKnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool Targets(EvidenceBinding binding, SourceNativeLocationTypeAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.LocationNativeType &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool Targets(EvidenceBinding binding, LocationSemanticClassificationAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.LocationSemanticClassification &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool Targets(EvidenceBinding binding, CanonicalRecordLifecycleAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.RecordLifecycle &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool Targets(EvidenceBinding binding, CanonicalSemanticClassificationAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.SemanticClassification &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool Targets(EvidenceBinding binding, CanonicalRecordContributionAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.RecordContribution &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        string.Equals(binding.ClaimLocator, assertion.SourceFieldPath, StringComparison.Ordinal) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool Targets(EvidenceBinding binding, CanonicalOrganizationalValueAssertion assertion) =>
        binding.ClaimKind == EvidenceClaimKind.OrganizationalValue &&
        binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
        binding.SourceRevisionId == assertion.SourceRevisionId &&
        IsExactOrStructuredChildLocator(binding.ClaimLocator, assertion.SourceFieldPath) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion);

    private static bool IsExactOrStructuredChildLocator(string locator, string claimLocator) =>
        string.Equals(locator, claimLocator, StringComparison.Ordinal) ||
        locator.StartsWith(claimLocator + "/", StringComparison.Ordinal);

    private static bool CompositeClaimLocatorSetClosed(
        string claimLocator,
        IEnumerable<string> evidenceLocators)
    {
        var locators = evidenceLocators.Distinct(StringComparer.Ordinal).ToImmutableArray();
        return locators.Any(value => string.Equals(value, claimLocator, StringComparison.Ordinal)) ||
               locators.Length >= 2 && locators.All(value =>
                   value.StartsWith(claimLocator + "/", StringComparison.Ordinal));
    }

    private static bool Targets(EvidenceBinding binding, CrossSourceTargetLinkClaim claim) =>
        binding.ClaimKind == EvidenceClaimKind.CrossSourceTargetLink &&
        binding.KnowledgeRecordId == claim.TargetKnowledgeRecordId &&
        binding.SourceRevisionId == claim.AssertingSourceRevisionId &&
        (string.Equals(binding.ClaimLocator, claim.TargetCoordinateFieldPath, StringComparison.Ordinal) ||
         string.Equals(binding.ClaimLocator, claim.AssertingCoordinateFieldPath, StringComparison.Ordinal)) &&
        binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(claim);

    private static bool SameIds(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Order(StringComparer.Ordinal).SequenceEqual(right.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static KnowledgeAdapterRevisionId RederiveAdapterRevision(
        KnowledgeAdapterRevisionCoordinate coordinate,
        KnowledgeAdapterSemanticContractDigest? independentlyDerivedSemanticContractDigest) =>
        coordinate.Id.AlgorithmVersion switch
        {
            KnowledgeAdapterRevisionId.LegacyAlgorithmVersion when coordinate.SemanticContractDigest is null =>
                KnowledgeAdapterRevisionId.DeriveV1(
                    coordinate.AdapterId,
                    coordinate.ExactAdapterVersion,
                    coordinate.AdapterArtifactDigest,
                    coordinate.AdapterContractVersion,
                    coordinate.MappingRulesVersion),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion when
                coordinate.SemanticContractDigest is { } declared &&
                independentlyDerivedSemanticContractDigest is { } derived &&
                declared == derived =>
                KnowledgeAdapterRevisionId.DeriveV2(
                    coordinate.AdapterId,
                    coordinate.ExactAdapterVersion,
                    coordinate.AdapterContractVersion,
                    coordinate.MappingRulesVersion,
                    declared),
            _ => default,
        };
}
