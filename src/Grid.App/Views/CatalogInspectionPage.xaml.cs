using System.Collections.Immutable;
using Grid.App.Services;
using Grid.Core.Models;
using Grid.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class CatalogInspectionPage : Page
{
    private readonly CanonicalCatalogInspector inspector = new();
    private ImmutableArray<CatalogInspectionPackageOption> packages = [];
    private CanonicalCatalogInspectionReport? report;

    public CatalogInspectionPage()
    {
        InitializeComponent();
        KindFilter.ItemsSource = new[] { "All kinds", "Location", "MissionQuest", "Item", "Actor" };
        ScopeFilter.ItemsSource = new[] { "All scopes", "Base game", "Mod extension" };
        ResolutionFilter.ItemsSource = new[] { "All resolution states", "Resolved terminology", "Missing terminology" };
        EvidenceFilter.ItemsSource = new[] { "All evidence", "FILE_VERIFIED", "REFERENCE_VERIFIED", "Mixed evidence" };
        KindFilter.SelectedIndex = ScopeFilter.SelectedIndex = ResolutionFilter.SelectedIndex = EvidenceFilter.SelectedIndex = 0;
        BindPackages(DeveloperCatalogInspectionFixture.CreateOptions());
    }

    public void BindPackages(IEnumerable<CatalogInspectionPackageOption> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        packages = values.ToImmutableArray();
        if (packages.Any(value => value is null || value.Package is null))
            throw new ArgumentException("Inspection package options must be non-null.", nameof(values));
        PackageSelector.ItemsSource = packages;
        PackageSelector.SelectedIndex = packages.IsEmpty ? -1 : 0;
    }

    private void OnPackageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PackageSelector.SelectedItem is not CatalogInspectionPackageOption option) return;
        try
        {
            LoadPackage(option.Package);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        {
            ShowFailClosedPackage(option.Package, exception);
        }
    }

    private void LoadPackage(CanonicalCatalogPackage package)
    {
        report = inspector.Inspect(package);
        PopulateSummary();
        PopulateRelationships();
        PopulateEvidence();
        PopulateUnresolved();
        PopulateConflicts();
        PopulateSources();
        PopulatePackage();
        PopulateSourceFilter();
        ApplyRecordFilter();

        VerificationBanner.Title = report.Summary.IsStructurallyValid ? "Structurally valid" : "Structurally invalid";
        VerificationBanner.Message = $"QCS status: {report.Summary.QcsStatus}. Structural verification is not approval or publication authority.";
        VerificationBanner.Severity = report.Summary.IsStructurallyValid ? InfoBarSeverity.Success : InfoBarSeverity.Error;
    }

    private void ShowFailClosedPackage(CanonicalCatalogPackage package, Exception exception)
    {
        report = null;
        SummaryItems.ItemsSource = RecordList.ItemsSource = RelationshipList.ItemsSource = EvidenceList.ItemsSource =
            UnresolvedList.ItemsSource = ConflictList.ItemsSource = SourceList.ItemsSource = null;
        RecordIdText.Text = NativeIdentityText.Text = RecordRevisionText.Text = EvidenceDetailText.Text = string.Empty;
        RecordTerminologyItems.ItemsSource = RecordRelationshipItems.ItemsSource = RecordEvidenceItems.ItemsSource = null;
        var verification = CanonicalCatalogPackageKernel.Verify(package);
        PackageItems.ItemsSource = new[]
        {
            Row("Package ID", package.Id.Value),
            Row("Catalog revision", package.Manifest.CatalogRevisionId.Value),
            Row("Payload digest", package.Manifest.PayloadDigest.Value),
            Row("Structural verification", "Structurally invalid"),
            Row("Structural issues", verification.Issues.IsEmpty
                ? $"Inspection failed closed ({exception.GetType().Name})."
                : string.Join(Environment.NewLine, verification.Issues)),
            Row("QCS status", package.Manifest.ValidationStatus.ToString()),
            Row("Publication semantic", "None. Inspection does not approve or publish packages."),
        };
        VerificationBanner.Title = "Structurally invalid";
        VerificationBanner.Message =
            $"QCS status: {package.Manifest.ValidationStatus}. Inspection failed closed ({exception.GetType().Name}); no package data was repaired.";
        VerificationBanner.Severity = InfoBarSeverity.Error;
    }

    private void PopulateSummary()
    {
        if (report is null) return;
        var value = report.Summary;
        SummaryItems.ItemsSource = new[]
        {
            Row("GameId", value.GameId.Value),
            Row("Scope", value.PackageKind.ToString()),
            Row("Exact game version", Exact(value.ExactGameVersion)),
            Row("Exact mod identity", Exact(value.ExactModIdentity)),
            Row("Exact mod version", Exact(value.ExactModVersion)),
            Row("Adapter ID / exact version / revision", string.Join(Environment.NewLine, value.AdapterRevisions.Select(item =>
                $"{item.AdapterId.Value} / {item.ExactAdapterVersion} / {item.Id.Value}"))),
            Row("Coverage", value.CoverageState.ToString()),
            Row("Location records", value.RecordCounts[KnowledgeKind.Location].ToString()),
            Row("MissionQuest records", value.RecordCounts[KnowledgeKind.MissionQuest].ToString()),
            Row("Item records", value.RecordCounts[KnowledgeKind.Item].ToString()),
            Row("Actor records", value.RecordCounts[KnowledgeKind.Actor].ToString()),
            Row("Terminology assertions", value.TerminologyCount.ToString()),
            Row("Relationships / hierarchy", value.RelationshipCount.ToString()),
            Row("Correlations", value.CorrelationCount.ToString()),
            Row("Unresolved", value.UnresolvedCount.ToString()),
            Row("Conflicts", value.ConflictCount.ToString()),
            Row("FILE_VERIFIED receipts", value.FileVerifiedCount.ToString()),
            Row("REFERENCE_VERIFIED receipts", value.ReferenceVerifiedCount.ToString()),
            Row("Sources / artifacts", $"{value.SourceCount} / {value.ArtifactCount}"),
            Row("Catalog revision", value.CatalogRevisionId.Value),
            Row("Package ID", value.PackageId.Value),
            Row("Structural verification", value.IsStructurallyValid ? "Structurally valid" : "Structurally invalid"),
            Row("QCS status", value.QcsStatus.ToString()),
        };
    }

    private void PopulateSourceFilter()
    {
        if (report is null) return;
        var values = new[] { "All sources" }.Concat(report.Sources.Select(value => value.Source.Id.Value)).ToArray();
        SourceFilter.ItemsSource = values;
        SourceFilter.SelectedIndex = 0;
    }

    private void OnRecordFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyRecordFilter();

    private void ApplyRecordFilter()
    {
        if (report is null || KindFilter is null || SourceFilter is null) return;
        IEnumerable<CatalogInspectionRecord> values = report.Records;
        if (KindFilter.SelectedIndex > 0 && Enum.TryParse<KnowledgeKind>(KindFilter.SelectedItem?.ToString(), out var kind))
            values = values.Where(value => value.Record.Kind == kind);
        if (SourceFilter.SelectedIndex > 0 && SourceFilter.SelectedItem is string sourceId)
        {
            var revisionIds = report.Sources.Where(value => value.Source.Id.Value == sourceId)
                .SelectMany(value => value.Revisions).Select(value => value.Revision.Id).ToHashSet();
            values = values.Where(value => revisionIds.Contains(value.Record.SourceRevisionId));
        }
        if (ScopeFilter.SelectedIndex == 1)
            values = values.Where(value => value.Record.ModVersion is null);
        else if (ScopeFilter.SelectedIndex == 2)
            values = values.Where(value => value.Record.ModVersion is not null);
        if (ResolutionFilter.SelectedIndex == 1)
            values = values.Where(value => !value.IsTerminologyUnresolved);
        else if (ResolutionFilter.SelectedIndex == 2)
            values = values.Where(value => value.IsTerminologyUnresolved);
        if (EvidenceFilter.SelectedIndex == 1)
            values = values.Where(value => value.EvidenceStates.Contains(EvidenceVerificationKind.FileVerified));
        else if (EvidenceFilter.SelectedIndex == 2)
            values = values.Where(value => value.EvidenceStates.Contains(EvidenceVerificationKind.ReferenceVerified));
        else if (EvidenceFilter.SelectedIndex == 3)
            values = values.Where(value => value.EvidenceStates.Length > 1);

        RecordList.ItemsSource = values.Select(value => new RecordRow(value)).ToArray();
        RecordList.SelectedIndex = RecordList.Items.Count > 0 ? 0 : -1;
    }

    private void OnRecordSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordList.SelectedItem is not RecordRow row)
        {
            RecordIdText.Text = NativeIdentityText.Text = RecordRevisionText.Text = string.Empty;
            RecordTerminologyItems.ItemsSource = RecordRelationshipItems.ItemsSource = RecordEvidenceItems.ItemsSource = null;
            return;
        }

        var value = row.Value;
        RecordIdText.Text = value.Record.Id.Value;
        NativeIdentityText.Text = NativeCoordinate(value.Record.NativeIdentity);
        RecordRevisionText.Text = value.Record.SourceRevisionId.Value;
        RecordTerminologyItems.ItemsSource = value.TerminologyAssertions.Select(item => new TerminologyRow(
            $"{item.Role} · {item.LanguageTag ?? "(no language tag)"} · {item.SourceFieldPath}", item.VerbatimValue)).ToArray();
        RecordRelationshipItems.ItemsSource = value.RelationshipAssertions.Select(item =>
            $"{item.SemanticId.Value} · native type {item.SourceNativeRelationshipType} · target {item.SourceNativeTarget.ExactRepresentation} · {item.Resolution} · {item.SourceFieldPath}").ToArray();
        RecordEvidenceItems.ItemsSource = value.EvidenceBindings.Select(item =>
                $"{item.ClaimKind} · {item.Id.Value} · {item.EvidenceReceiptId.Value} · {item.ClaimLocator}")
            .Concat(value.CorrelationIds.Select(item => $"Correlation · {item.Value}"))
            .ToArray();
    }

    private void PopulateRelationships()
    {
        if (report is null) return;
        RelationshipList.ItemsSource = report.Relationships.Select(value => new RelationshipRow(
            $"{value.Assertion.SemanticId.Value} · native type {value.Assertion.SourceNativeRelationshipType}",
            $"subject {value.Assertion.SubjectKnowledgeRecordId.Value}",
            value.Assertion.ResolvedTargetKnowledgeRecordId is KnowledgeRecordId target
                ? $"resolved target {target.Value} · native {value.Assertion.SourceNativeTarget.ExactRepresentation}"
                : $"UNRESOLVED native target {value.Assertion.SourceNativeTarget.ExactRepresentation}",
            $"claim {value.ClaimContentId.Value} · bindings {string.Join(", ", value.EvidenceBindingIds.Select(item => item.Value))} · field {value.Assertion.SourceFieldPath}"))
            .ToArray();
    }

    private void PopulateEvidence()
    {
        if (report is null) return;
        EvidenceList.ItemsSource = report.Evidence.Select(value => new EvidenceRow(value)).ToArray();
        EvidenceList.SelectedIndex = EvidenceList.Items.Count > 0 ? 0 : -1;
    }

    private void OnEvidenceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EvidenceList.SelectedItem is not EvidenceRow row)
        {
            EvidenceDetailText.Text = string.Empty;
            return;
        }
        var value = row.Value;
        var lines = new List<string>
        {
            $"EvidenceReceiptId: {value.EvidenceReceiptId.Value}",
            $"Verification: {value.Verification}",
            $"Exact claim bindings: {value.EvidenceBindings.Length}",
            $"Source revision: {value.SourceRevision.Revision.Id.Value}",
            $"Adapter ID: {value.AdapterDescriptor.AdapterId.Value}",
            $"Adapter exact version: {value.AdapterDescriptor.ExactAdapterVersion}",
            $"Adapter revision: {value.AdapterDescriptor.RevisionId.Value}",
            $"Mapping rules version: {value.AdapterDescriptor.MappingRulesVersion}",
            $"Format(s): {string.Join(", ", value.SourceRevision.ArtifactFormats.Select(item => $"{item.Format.FormatId}@{item.Format.ExactFormatVersion}"))}",
        };
        foreach (var binding in value.EvidenceBindings)
        {
            lines.Add($"Binding: {binding.Id.Value}");
            lines.Add($"  KnowledgeRecordId: {binding.KnowledgeRecordId.Value}");
            lines.Add($"  Claim kind/content: {binding.ClaimKind} / {binding.ClaimContentId?.Value ?? "(identity claim)"}");
            lines.Add($"  Exact field locator: {binding.ClaimLocator}");
        }
        foreach (var correlationId in value.CorrelationIds) lines.Add($"Correlation consumer: {correlationId.Value}");
        foreach (var unresolvedId in value.UnresolvedAssertionIds) lines.Add($"Unresolved assertion consumer: {unresolvedId.Value}");
        if (value.FileEvidence is not null)
        {
            var receipt = value.FileEvidence.Receipt;
            lines.Add($"Artifact: {receipt.SourceArtifactId.Value}");
            lines.Add($"Artifact digest: {receipt.ArtifactDigest.Algorithm}:{receipt.ArtifactDigest.HexValue}");
            lines.Add($"Parser: {receipt.ParserId}@{receipt.ParserVersion}");
            lines.Add($"Native record/member locator: {receipt.NativeRecordLocator}");
            lines.Add($"Source field path: {receipt.SourceFieldPath}");
            lines.Add($"Byte range: {(receipt.ByteOffset is null ? "(not supplied)" : $"{receipt.ByteOffset}+{receipt.ByteLength}")}");
        }
        else if (value.ReferenceEvidence is not null)
        {
            var receipt = value.ReferenceEvidence.Receipt;
            lines.Add($"Response artifact: {receipt.ResponseArtifactId.Value}");
            lines.Add($"Response digest: {receipt.ResponseContentDigest.Algorithm}:{receipt.ResponseContentDigest.HexValue}");
            lines.Add($"Provider source: {receipt.ProviderCatalogSourceId.Value}");
            lines.Add($"Native object: {Exact(receipt.NativeObjectIdentity)}");
            lines.Add($"Native revision: {Exact(receipt.NativeRevisionIdentity)}");
            lines.Add($"Response field path: {receipt.ResponseFieldPath}");
        }
        EvidenceDetailText.Text = string.Join(Environment.NewLine, lines);
    }

    private void PopulateUnresolved()
    {
        if (report is null) return;
        UnresolvedList.ItemsSource = report.Unresolved.Select(value => new UnresolvedRow(
            value.Kind.ToString(), value.CanonicalCoordinate, value.ExactDetail)).ToArray();
    }

    private void PopulateConflicts()
    {
        if (report is null) return;
        ConflictList.ItemsSource = report.Conflicts.Select(value => new ConflictRow(
            value.Kind.ToString(), value.CanonicalCoordinate,
            string.Join(Environment.NewLine + "────────" + Environment.NewLine, value.ExactAssertions))).ToArray();
    }

    private void PopulateSources()
    {
        if (report is null) return;
        SourceList.ItemsSource = report.Sources.Select(value => new SourceRow(
            value.Source.Kind.ToString(),
            NativeCoordinate(value.Source.NativeIdentity),
            string.Join(Environment.NewLine, value.Revisions.Select(revision =>
                $"Source revision: {revision.Revision.Id.Value}{Environment.NewLine}" +
                $"Native revision: {Exact(revision.Revision.NativeRevision)}{Environment.NewLine}" +
                $"Scope: {revision.SourceScope.ScopeKind} · game {revision.SourceScope.GameId.Value}{Environment.NewLine}" +
                $"Adapter revision: {revision.AdapterRevisionId.Value}{Environment.NewLine}" +
                $"Formats: {string.Join(", ", revision.ArtifactFormats.Select(item => $"{item.Format.FormatId}@{item.Format.ExactFormatVersion}"))}{Environment.NewLine}" +
                $"Artifacts: {string.Join(", ", value.Artifacts.Select(item => $"{item.Id.Value} ({item.Digest.Algorithm}:{item.Digest.HexValue})"))}"))))
            .ToArray();
    }

    private void PopulatePackage()
    {
        if (report is null) return;
        var value = report.PackageValidation;
        PackageItems.ItemsSource = new[]
        {
            Row("Package schema", value.PackageSchemaVersion.ToString()),
            Row("Package ID", value.PackageId.Value),
            Row("Catalog revision", value.CatalogRevisionId.Value),
            Row("Payload digest", value.PayloadDigest.Value),
            Row("Structural verification", value.IsStructurallyValid ? "Structurally valid" : "Structurally invalid"),
            Row("Structural issues", value.StructuralIssues.IsEmpty ? "(none)" : string.Join(Environment.NewLine, value.StructuralIssues)),
            Row("QCS status", value.QcsStatus.ToString()),
            Row("Validation policy", $"{value.ValidationPolicyId}@{value.ValidationPolicyVersion}"),
            Row("Validation result digest", $"{value.ValidationResultDigest.Algorithm}:{value.ValidationResultDigest.HexValue}"),
            Row("Publication semantic", "None. Inspection does not approve or publish packages."),
        };
    }

    private static FieldRow Row(string label, string value) => new(label, value);
    private static string Exact(SourceNativeIdentifier? value) => value?.ExactRepresentation ?? "(absent)";
    private static string Exact(SourceNativeVersion? value) => value?.ExactRepresentation ?? "(absent)";
    private static string NativeCoordinate(SourceNativeIdentifier value) =>
        $"namespace={value.Namespace} · type={value.ObjectType} · exact={value.ExactRepresentation} · " +
        $"identity-bytes={Convert.ToHexString(value.IdentityBytes.AsSpan())} · comparison={value.ComparisonMethodId}@{value.ComparisonMethodVersion}";

    public sealed record FieldRow(string Label, string Value);
    public sealed record RecordRow(CatalogInspectionRecord Value)
    {
        public string Kind => Value.Record.Kind.ToString();
        public string NativeIdentifier => $"IDENTIFIER · {Value.Record.NativeIdentity.ExactRepresentation}";
        public string TerminologyState => Value.IsTerminologyUnresolved
            ? "UNRESOLVED · no source-native terminology assertion"
            : $"{Value.TerminologyAssertions.Length} source-native terminology assertion(s)";
    }
    public sealed record TerminologyRow(string Coordinate, string VerbatimValue);
    public sealed record RelationshipRow(string Semantic, string Subject, string Target, string Evidence);
    public sealed record EvidenceRow(CatalogInspectionEvidenceTrace Value)
    {
        public string Verification => Value.Verification == EvidenceVerificationKind.FileVerified
            ? "FILE_VERIFIED"
            : "REFERENCE_VERIFIED";
        public string Claim => Value.EvidenceBindings.IsEmpty
            ? $"receipt · {Value.EvidenceReceiptId.Value}"
            : $"{string.Join(" + ", Value.EvidenceBindings.Select(item => item.ClaimKind))} · {Value.EvidenceReceiptId.Value}";
        public string Locator => Value.FileEvidence?.Receipt.SourceFieldPath ??
                                 Value.ReferenceEvidence?.Receipt.ResponseFieldPath ?? string.Empty;
    }
    public sealed record UnresolvedRow(string Kind, string Coordinate, string ExactDetail);
    public sealed record ConflictRow(string Kind, string Coordinate, string ExactAssertions);
    public sealed record SourceRow(string SourceKind, string NativeIdentity, string ExactCoordinates);
}
