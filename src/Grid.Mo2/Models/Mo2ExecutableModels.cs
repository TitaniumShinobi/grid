using System.Collections.Immutable;
using System.Diagnostics;
using Grid.Core.Models;

namespace Grid.Mo2.Models;

public enum Mo2ExecutableConfigurationStatus
{
    Complete,
    Partial,
    Missing,
    Inaccessible,
    Malformed,
    ChangedDuringRead,
    Canceled,
}

public enum Mo2ExecutablePathAvailability
{
    Available,
    Missing,
    Inaccessible,
    OutsideExpectedRoots,
    Invalid,
    NotConfigured,
}

public enum Mo2ExecutableLocationTrust
{
    ExpectedRoot,
    ExplicitlyAuthorized,
    OutsideExpectedRoots,
    Unknown,
}

public enum Mo2QtValueSupport
{
    Supported,
    UnsupportedVariant,
    Malformed,
}

public sealed record Mo2ExecutableObservationLimits(
    int MaximumConfigurationBytes = 8 * 1024 * 1024,
    int MaximumLines = 65_536,
    int MaximumLineCharacters = 65_536,
    int MaximumEntries = 4_096,
    int MaximumFieldsPerEntry = 256)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumConfigurationBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLineCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFieldsPerEntry);
    }
}

public sealed record Mo2QSettingsField(
    int SourceLineIndex,
    int SourceOrder,
    int? ArrayIndex,
    string Key,
    string RawKey,
    string RawValue,
    string? LogicalValue,
    Mo2QtValueSupport Support)
{
    public override string ToString() => $"{RawKey}=<preserved {RawValue.Length} characters>";
}

public sealed record Mo2QSettingsArrayEntry(
    int ArrayIndex,
    int SourceOrder,
    ImmutableArray<Mo2QSettingsField> Fields,
    ImmutableArray<Mo2ParseWarning> Warnings)
{
    public Mo2QSettingsField? LastSupported(string key) => Fields
        .LastOrDefault(field =>
            string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase) &&
            field.Support == Mo2QtValueSupport.Supported);
}

public sealed record Mo2QSettingsArrayParseResult(
    string Section,
    int? DeclaredSize,
    ImmutableArray<Mo2QSettingsArrayEntry> Entries,
    ImmutableArray<Mo2QSettingsField> UnassignedFields,
    ImmutableArray<Mo2ParseWarning> Warnings,
    ProfileSourceParseStatus Status);

[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class Mo2OpaqueArguments : IEquatable<Mo2OpaqueArguments>
{
    public Mo2OpaqueArguments(string exactValue) => ExactValue = exactValue ?? throw new ArgumentNullException(nameof(exactValue));

    public string ExactValue { get; }

    public int Length => ExactValue.Length;

    public bool IsEmpty => ExactValue.Length == 0;

    public string MaskedDisplay => IsEmpty ? "(none)" : $"Masked MO2 arguments ({Length} characters)";

    public bool Equals(Mo2OpaqueArguments? other) => other is not null &&
        string.Equals(ExactValue, other.ExactValue, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Mo2OpaqueArguments other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ExactValue);

    public override string ToString() => MaskedDisplay;

    private string DebuggerDisplay => MaskedDisplay;
}

public sealed record Mo2ExecutablePathObservation(
    string Label,
    string? ConfiguredValue,
    string? CanonicalPath,
    Mo2ExecutablePathAvailability Availability,
    Mo2ExecutableLocationTrust LocationTrust,
    bool WasProbed,
    string? DiagnosticCode)
{
    public override string ToString() => $"{Label}: {Availability} ({LocationTrust})";
}

public sealed record Mo2ObservedExecutable(
    int SourceIndex,
    int SourceOrder,
    string Title,
    Mo2ExecutablePathObservation Binary,
    Mo2OpaqueArguments Arguments,
    Mo2ExecutablePathObservation WorkingDirectory,
    string? SteamAppId,
    bool? Toolbar,
    bool? OwnIcon,
    bool? Hide,
    bool? MinimizeToSystemTray,
    ImmutableArray<Mo2QSettingsField> RawFields,
    bool IsDuplicate,
    ImmutableArray<Mo2ParseWarning> Warnings,
    string Fingerprint)
{
    public Mo2ExecutablePathAvailability Availability => Binary.Availability;
}

public sealed record Mo2ExecutableSourceProvenance(
    string CanonicalSourcePath,
    DateTimeOffset ObservedAtUtc,
    string ParserVersion,
    string ContentFingerprint,
    Mo2FileStamp Before,
    Mo2FileStamp After,
    Mo2TextEncodingKind Encoding,
    ImmutableArray<Mo2LineTerminator> LineTerminators)
{
    public override string ToString() =>
        $"MO2 executable source {ContentFingerprint} observed {ObservedAtUtc:O}";
}

public sealed record Mo2ExecutableConfigurationSnapshot(
    InstallationReferenceId ReferenceId,
    Mo2ExecutableConfigurationStatus Status,
    ImmutableArray<Mo2ObservedExecutable> Entries,
    Mo2QSettingsArrayParseResult ParsedArray,
    Mo2RawTextDocument? RawDocument,
    Mo2ExecutableSourceProvenance? Provenance,
    ImmutableArray<Mo2ValidationIssue> Issues,
    string Revision)
{
    public override string ToString() =>
        $"MO2 executable snapshot {Revision}: {Status}, {Entries.Length} entries";
}

public sealed record Mo2ExecutableObservationRequest(
    Mo2InstallationReference Reference,
    Mo2InstallationValidation Validation,
    ImmutableArray<string> AdditionalExpectedRoots = default)
{
    public ImmutableArray<string> EffectiveAdditionalExpectedRoots =>
        AdditionalExpectedRoots.IsDefault ? [] : AdditionalExpectedRoots;
}

public sealed record Mo2ExecutablePathAuthorization(
    InstallationReferenceId ReferenceId,
    string CanonicalPath);

public enum Mo2IconObservationStatus
{
    Available,
    Missing,
    Inaccessible,
    Unsupported,
    Malformed,
    Oversized,
    ChangedDuringRead,
}

public sealed record Mo2IconObservation(
    Mo2IconObservationStatus Status,
    ImmutableArray<byte> IcoBytes,
    string? Fingerprint,
    ImmutableArray<Mo2ParseWarning> Warnings,
    Mo2FileStamp? Before = null,
    Mo2FileStamp? After = null);

public sealed record Mo2PeIconLimits(
    int MaximumExecutableBytes = 32 * 1024 * 1024,
    int MaximumResourceEntries = 4_096,
    int MaximumIconsPerGroup = 128,
    int MaximumIconBytes = 2 * 1024 * 1024)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumExecutableBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumResourceEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumIconsPerGroup);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumIconBytes);
    }
}
