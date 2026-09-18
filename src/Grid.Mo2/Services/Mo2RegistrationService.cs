using System.Collections.Immutable;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

/// <summary>
/// Registers one explicitly selected MO2 instance after bounded, two-pass validation.
/// This service reads MO2 configuration and writes only Grid's installation-reference store.
/// </summary>
public sealed class Mo2RegistrationService(
    IMo2InstallationValidator validator,
    IMo2ConnectionService connections,
    IMo2InstallationReferenceRecoveryStore references,
    IMo2ReadOnlyFileSystem fileSystem,
    IMo2TextDecoder textDecoder,
    IMo2PathCanonicalizer paths,
    Func<string, DriveType>? driveType = null)
{
    private readonly Func<string, DriveType> getDriveType = driveType ?? (root => new DriveInfo(root).DriveType);

    public async Task<Mo2RegistrationResult> RegisterAsync(
        Mo2RegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var initialRequest = new Mo2ValidationRequest(
            request.ApplicationDirectory,
            request.InstanceDirectory,
            request.ExpectedGameId);
        var first = await validator.ValidateAsync(initialRequest, cancellationToken).ConfigureAwait(false);
        var derived = first.Paths
            .Where(item => item.State == Mo2PathState.AuthorizationRequired && item.CanonicalPath is not null)
            .Select(item => item.CanonicalPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var validationRequest = initialRequest with { AuthorizedConfiguredPaths = derived };
        var validation = derived.IsEmpty
            ? first
            : await validator.ValidateAsync(validationRequest, cancellationToken).ConfigureAwait(false);

        var contextIssues = ImmutableArray.CreateBuilder<Mo2ValidationIssue>();
        if (!validation.CanConnect)
        {
            contextIssues.AddRange(validation.Issues);
            return Unresolved(validation, derived, contextIssues);
        }

        ValidateVolumes(validation, contextIssues);
        var profile = await ResolveSelectedProfileAsync(
            validation,
            request.ExpectedProfileName,
            contextIssues,
            cancellationToken).ConfigureAwait(false);
        if (contextIssues.Any(issue => issue.Severity == Mo2IssueSeverity.Error) || profile is null)
        {
            return Unresolved(validation, derived, contextIssues);
        }

        Mo2ReferenceStoreSnapshot storeSnapshot;
        Mo2ReferenceLoadResult before;
        try
        {
            storeSnapshot = await references.CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
            before = await references.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            contextIssues.Add(new("mo2.registration.snapshot_failed", Mo2IssueSeverity.Error,
                "Grid could not capture its exact connection-reference state before registration."));
            return PersistenceFailure(validation, derived, contextIssues);
        }
        if (before.Issues.Any(issue => issue.Severity == Mo2IssueSeverity.Error))
        {
            contextIssues.AddRange(before.Issues);
            return PersistenceFailure(validation, derived, contextIssues);
        }

        var connection = await connections.ConnectAsync(
            new(validationRequest, request.AdapterId, request.DisplayName, ReuseExisting: true),
            cancellationToken).ConfigureAwait(false);
        if (!connection.Succeeded || connection.Reference is null)
        {
            contextIssues.AddRange(connection.Issues);
            return connection.Issues.Any(issue => issue.Code.Contains("persistence", StringComparison.Ordinal))
                ? PersistenceFailure(validation, derived, contextIssues)
                : Unresolved(validation, derived, contextIssues);
        }

        var reused = connection.Issues.Any(issue => issue.Code == "mo2.connection.reused");
        Mo2ReferenceLoadResult readback;
        try
        {
            readback = await references.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!reused)
        {
            await references.RestoreSnapshotAsync(storeSnapshot, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        var persisted = readback.Issues.All(issue => issue.Severity != Mo2IssueSeverity.Error) &&
            readback.References.Count(candidate => candidate.Id == connection.Reference.Id) == 1 &&
            readback.References.Any(candidate => candidate == connection.Reference);
        if (!persisted)
        {
            contextIssues.AddRange(readback.Issues);
            contextIssues.Add(new(
                "mo2.registration.readback_failed",
                Mo2IssueSeverity.Error,
                "Grid could not verify the persisted MO2 reference and restored the preceding reference set."));
            if (!reused)
            {
                try
                {
                    await references.RestoreSnapshotAsync(storeSnapshot, CancellationToken.None).ConfigureAwait(false);
                    var restored = await references.CaptureSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                    if (restored != storeSnapshot)
                    {
                        throw new InvalidDataException("The exact reference-store snapshot was not restored.");
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                {
                    contextIssues.Add(new(
                        "mo2.registration.rollback_failed",
                        Mo2IssueSeverity.Error,
                        "Grid could not verify restoration of its connection-reference store."));
                }
            }
            return PersistenceFailure(validation, derived, contextIssues);
        }

        var profileId = Mo2ProfileParsers.StableProfileId(connection.Reference.Id, profile.Value.Path);
        contextIssues.AddRange(connection.Issues);
        return new(
            reused ? Mo2RegistrationStatus.Reused : Mo2RegistrationStatus.Registered,
            Mo2RegistrationRecoveryDisposition.None,
            true,
            true,
            connection.Reference,
            profile.Value.Name,
            profileId,
            validation,
            derived,
            contextIssues.ToImmutable());
    }

    private async Task<(string Name, string Path)?> ResolveSelectedProfileAsync(
        Mo2InstallationValidation validation,
        string expectedProfileName,
        ImmutableArray<Mo2ValidationIssue>.Builder issues,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedProfileName) ||
            expectedProfileName.Trim().Length > 255 ||
            expectedProfileName.Any(character => char.IsControl(character) || Path.GetInvalidFileNameChars().Contains(character)))
        {
            issues.Add(new("mo2.profile.expected_invalid", Mo2IssueSeverity.Error,
                "The expected selected-profile name is invalid."));
            return null;
        }
        if (validation.IniPath is null || validation.ProfilesDirectory is null)
        {
            issues.Add(new("mo2.profile.context_missing", Mo2IssueSeverity.Error,
                "Validated instance configuration and profiles roots are required."));
            return null;
        }

        Mo2FileReadResult source;
        try
        {
            source = await fileSystem.ReadBytesWithMetadataAsync(
                validation.IniPath,
                1024 * 1024,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            issues.Add(new("mo2.profile.selection_unreadable", Mo2IssueSeverity.Error,
                "The selected profile could not be read from the validated MO2 configuration."));
            return null;
        }

        if (source.Before != source.After)
        {
            issues.Add(new("mo2.profile.selection_changed", Mo2IssueSeverity.Error,
                "The selected-profile configuration changed while it was being read."));
            return null;
        }

        string? selected;
        try
        {
            var document = textDecoder.Decode(source.Bytes, Mo2TextDecodingPolicy.IniBomUtf8SystemFallback);
            selected = Mo2ProfileParsers.ParseSelectedProfile(document)?.Trim();
        }
        catch (InvalidDataException)
        {
            issues.Add(new("mo2.profile.selection_unreadable", Mo2IssueSeverity.Error,
                "The selected profile could not be decoded from the validated MO2 configuration."));
            return null;
        }
        if (string.IsNullOrWhiteSpace(selected))
        {
            issues.Add(new("mo2.profile.selection_missing", Mo2IssueSeverity.Error,
                "The validated MO2 instance does not identify one selected profile."));
            return null;
        }
        if (!selected.Equals(expectedProfileName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("mo2.profile.selection_mismatch", Mo2IssueSeverity.Error,
                "The explicitly expected profile does not match MO2's selected profile."));
            return null;
        }

        var lexicalProfilePath = Path.Combine(validation.ProfilesDirectory, selected);
        if (!paths.TryCanonicalize(lexicalProfilePath, out var profilePath, out _) ||
            !paths.IsImmediateChildOf(profilePath, validation.ProfilesDirectory) ||
            fileSystem.ProbeDirectory(profilePath) != Mo2PathState.Present)
        {
            issues.Add(new("mo2.profile.selected_unavailable", Mo2IssueSeverity.Error,
                "The selected profile is missing, inaccessible, or outside the validated profiles root."));
            return null;
        }
        return (selected, profilePath);
    }

    private void ValidateVolumes(
        Mo2InstallationValidation validation,
        ImmutableArray<Mo2ValidationIssue>.Builder issues)
    {
        var roots = validation.Paths
            .Where(item => item.State == Mo2PathState.Present && item.CanonicalPath is not null)
            .Select(item => Path.GetPathRoot(item.CanonicalPath!))
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            DriveType type;
            try
            {
                type = getDriveType(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                issues.Add(new("mo2.volume.unavailable", Mo2IssueSeverity.Error,
                    "A validated installation root's volume type could not be established."));
                continue;
            }
            if (type is not (DriveType.Fixed or DriveType.Ram))
            {
                issues.Add(new("mo2.volume.unsupported", Mo2IssueSeverity.Error,
                    $"A validated installation root uses unsupported volume type '{type}'."));
            }
        }
    }

    private static Mo2RegistrationResult Unresolved(
        Mo2InstallationValidation validation,
        ImmutableArray<string> derived,
        ImmutableArray<Mo2ValidationIssue>.Builder issues) =>
        new(Mo2RegistrationStatus.InstallationContextUnresolved,
            Mo2RegistrationRecoveryDisposition.ConfigurationRequired, false, false, null, null, null,
            validation, derived, issues.ToImmutable());

    private static Mo2RegistrationResult PersistenceFailure(
        Mo2InstallationValidation validation,
        ImmutableArray<string> derived,
        ImmutableArray<Mo2ValidationIssue>.Builder issues) =>
        new(Mo2RegistrationStatus.PersistenceFailed,
            Mo2RegistrationRecoveryDisposition.ConfigurationRequired, false, false, null, null, null,
            validation, derived, issues.ToImmutable());
}
