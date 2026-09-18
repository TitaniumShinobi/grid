using System.Collections.Immutable;

namespace Grid.Mo2.Models;

public enum Mo2SpidDistributionStatus
{
    Complete,
    Partial,
    LimitExceeded,
}

public enum Mo2SpidPluginReferenceStatus
{
    Enabled,
    Disabled,
    Missing,
}

public sealed record Mo2SpidDistributionLimits(
    int MaximumDocuments = 10_000,
    int MaximumLines = 2_000_000,
    int MaximumLineCharacters = 65_536,
    int MaximumReferences = 2_000_000)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDocuments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLineCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumReferences);
    }
}

public sealed record Mo2SpidDistributionDocument(
    string VirtualPath,
    string ProviderName,
    string Sha256,
    Mo2RawTextDocument Text);

public sealed record Mo2SpidSourcePluginReference(
    string VirtualPath,
    string ProviderName,
    int Line,
    string DistributionType,
    string SourceToken,
    string PluginName,
    string FormToken,
    Mo2SpidPluginReferenceStatus Status);

public sealed record Mo2SpidDistributionIssue(
    string Code,
    string Detail,
    string VirtualPath,
    string ProviderName,
    int? Line = null,
    string? PluginName = null);

public sealed record Mo2SpidDistributionInspection(
    Mo2SpidDistributionStatus Status,
    int DocumentsScanned,
    long LinesScanned,
    long RulesScanned,
    ImmutableArray<Mo2SpidSourcePluginReference> SourcePluginReferences,
    ImmutableArray<Mo2SpidDistributionIssue> Issues,
    string SemanticFingerprint);

