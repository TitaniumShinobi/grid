using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Mo2.Models;

public enum Mo2RecognizedToolFamily
{
    Unknown,
    Skse,
    SkyrimLauncher,
    SseEdit,
    ZEdit,
    Synthesis,
    Pandora,
    Nemesis,
    Fnis,
    Loot,
    BodySlide,
    TexGen,
    DynDoLod,
    XLodGen,
    WryeBash,
    CreationKit,
}

public enum Mo2RecognitionConfidence
{
    Unknown,
    Candidate,
    Corroborated,
}

public enum Mo2GeneratedOutputKind
{
    Overwrite,
    UserDesignatedMod,
    BodySlide,
    BehaviorGeneration,
    Synthesis,
    WryeBash,
    TexGen,
    DynDoLod,
    XLodGen,
    Unknown,
}

public enum Mo2OutputAvailability
{
    Available,
    Missing,
    Inaccessible,
    Ambiguous,
    Inconsistent,
    AuthorizationRequired,
}

public enum Mo2OutputAssociationConfidence
{
    Unknown,
    Ambiguous,
    Corroborated,
    ConfirmedByMo2,
}

public enum Mo2OutputFingerprintStrength
{
    Unavailable,
    Structural,
    CompleteContent,
    Partial,
}

public enum Mo2OutputComparisonState
{
    FirstObservation,
    NoChange,
    NoChangeObserved,
    Added,
    Removed,
    Modified,
    Reclassified,
    AvailabilityChanged,
    AssociationChanged,
    Indeterminate,
}

public enum Mo2ToolOutputObservationStatus
{
    Complete,
    Partial,
    Unavailable,
    Inconsistent,
}

public sealed record Mo2ToolRecognitionEvidence(
    ObservedExecutableId ExecutableId,
    string Title,
    string? BinaryFileName,
    string? ProductName,
    ImmutableArray<string> ArtifactSignals);

public sealed record Mo2ToolRecognitionResult(
    ObservedExecutableId ExecutableId,
    string ConfiguredTitle,
    Mo2RecognizedToolFamily Family,
    Mo2RecognitionConfidence Confidence,
    ImmutableArray<string> Evidence);

public sealed record Mo2CustomOverwriteMapping(
    string ExecutableTitle,
    string OutputModName,
    int SourceIndex,
    int SourceLineIndex);

public sealed record Mo2GeneratedOutputFingerprint(
    Mo2OutputFingerprintStrength Strength,
    string? StructuralFingerprint,
    string? ContentFingerprint,
    long FileCount,
    long DirectoryCount,
    long TotalBytes,
    ImmutableArray<Mo2ValidationIssue> Issues);

public sealed record Mo2GeneratedOutputObservation(
    GeneratedOutputId Id,
    ProfileId ProfileId,
    string DisplayName,
    Mo2GeneratedOutputKind Kind,
    Mo2OutputAvailability Availability,
    bool? IsEnabled,
    ModId? ModId,
    ObservedExecutableId? AssociatedExecutableId,
    Mo2RecognizedToolFamily AssociatedToolFamily,
    Mo2OutputAssociationConfidence AssociationConfidence,
    Mo2GeneratedOutputFingerprint Fingerprint,
    Mo2OutputComparisonState Comparison,
    ImmutableArray<string> Evidence,
    ImmutableArray<Mo2ValidationIssue> Issues,
    string? CanonicalRoot);

public sealed record Mo2ToolOutputObservationLimits(
    Mo2ContentObservationLimits Content,
    long MaximumContentHashBytes = 256L * 1024 * 1024,
    int MaximumConcurrentReaders = 4,
    int BufferSize = 64 * 1024)
{
    public static Mo2ToolOutputObservationLimits Default { get; } = new(new Mo2ContentObservationLimits());

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Content);
        Content.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumContentHashBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumConcurrentReaders);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BufferSize);
    }
}

public sealed record Mo2ToolOutputObservationRequest(
    Mo2InstallationReference Reference,
    Mo2InstallationValidation Validation,
    Mo2ObservedProfile Profile,
    ImmutableArray<Mo2ToolRecognitionResult> RecognizedTools,
    ImmutableArray<Mo2CustomOverwriteMapping> CustomOverwriteMappings);

public sealed record Mo2ToolOutputSnapshot(
    ExternalObservationSnapshotId Id,
    InstallationReferenceId ReferenceId,
    ProfileId ProfileId,
    DateTimeOffset ObservedAtUtc,
    Mo2ToolOutputObservationStatus Status,
    ImmutableArray<Mo2GeneratedOutputObservation> Outputs,
    string Fingerprint,
    ImmutableArray<Mo2ValidationIssue> Issues);

public sealed record Mo2ToolOutputSnapshotComparison(
    Mo2ToolOutputSnapshot? Previous,
    Mo2ToolOutputSnapshot Current,
    ImmutableArray<Mo2GeneratedOutputObservation> RemovedOutputs);
