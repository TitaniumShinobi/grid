using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2ConnectionService(
    IMo2InstallationValidator validator,
    IMo2InstallationReferenceStore referenceStore,
    IMo2PathCanonicalizer paths) : IMo2ConnectionService
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<Mo2ConnectionResult> ConnectAsync(
        Mo2ConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ConnectCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Mo2DisconnectResult> DisconnectAsync(
        Mo2DisconnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await referenceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.Issues.Any(issue => issue.Severity == Mo2IssueSeverity.Error))
            {
                return new(false, false, loaded.Issues);
            }

            var matches = loaded.References.Where(reference =>
                reference.Id == request.ReferenceId &&
                reference.InstallationId == request.InstallationId).ToArray();
            if (matches.Length == 0)
            {
                return new(false, false,
                    [new("mo2.connection.disconnect_missing", Mo2IssueSeverity.Warning,
                        "The Grid connection reference was already absent. No external files were changed.")]);
            }

            try
            {
                await referenceStore.SaveAsync(
                    loaded.References.Where(reference => !matches.Contains(reference)).ToImmutableArray(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new(false, true,
                    [new("mo2.connection.disconnect_persistence_failed", Mo2IssueSeverity.Error,
                        "Grid could not remove its local connection reference. The external MO2 installation was not changed.")]);
            }

            return new(true, true,
                [new("mo2.connection.disconnected", Mo2IssueSeverity.Information,
                    "Grid removed only its local connection reference. The external MO2 installation was not changed.")]);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Mo2ConnectionResult> ConnectCoreAsync(
        Mo2ConnectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string displayName;
        try
        {
            displayName = Mo2InstallationReferenceStore.ValidateDisplayName(request.DisplayName);
        }
        catch (ArgumentException exception)
        {
            var invalid = new Mo2InstallationValidation(
                Status: Mo2ValidationStatus.Invalid,
                SelectionKind: Mo2SelectionKind.Invalid,
                CanConnect: false,
                InstanceKind: null,
                ApplicationDirectory: null,
                ExecutablePath: null,
                InstanceDirectory: null,
                IniPath: null,
                BaseDirectory: null,
                ModsDirectory: null,
                ProfilesDirectory: null,
                DownloadsDirectory: null,
                OverwriteDirectory: null,
                GameDirectory: null,
                GameName: null,
                ConnectionKey: null,
                Paths: [],
                Issues: []);
            return new(false, null, invalid, [new("mo2.name.invalid", Mo2IssueSeverity.Error, exception.Message)]);
        }

        var validation = await validator.ValidateAsync(request.Validation, cancellationToken).ConfigureAwait(false);
        try
        {
            Mo2InstallationReferenceStore.EnforceSupportedIdentity(request.Validation.ExpectedGameId, request.AdapterId);
        }
        catch (ArgumentException exception)
        {
            return new(false, null, validation, [new("mo2.identity.unsupported", Mo2IssueSeverity.Error, exception.Message)]);
        }
        if (!validation.CanConnect ||
            validation.InstanceKind is not Mo2InstanceKind instanceKind ||
            validation.ApplicationDirectory is null ||
            validation.ExecutablePath is null ||
            validation.InstanceDirectory is null ||
            validation.ConnectionKey is null)
        {
            return new(false, null, validation, validation.Issues);
        }

        var loaded = await referenceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.Issues.Any(issue => issue.Severity == Mo2IssueSeverity.Error))
        {
            return new(false, null, validation, loaded.Issues);
        }

        var existing = loaded.References.FirstOrDefault(reference =>
            string.Equals(
                DeriveConnectionKey(reference),
                validation.ConnectionKey,
                StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (request.ReuseExisting &&
                existing.GameId == request.Validation.ExpectedGameId &&
                existing.AdapterId == request.AdapterId &&
                existing.InstanceKind == validation.InstanceKind &&
                paths.Equals(existing.ExecutablePath, validation.ExecutablePath) &&
                paths.Equals(existing.InstanceDirectory, validation.InstanceDirectory))
            {
                return new(
                    true,
                    existing,
                    validation,
                    [new("mo2.connection.reused", Mo2IssueSeverity.Information,
                        "Grid reused the matching validated MO2 connection reference; no MO2 files were changed.")]);
            }

            return new(
                false,
                null,
                validation,
                [new("mo2.connection.duplicate", Mo2IssueSeverity.Error, "This MO2 instance is already connected to Grid.")]);
        }

        var opaqueSuffix = validation.ConnectionKey[..24];
        var reference = new Mo2InstallationReference(
            Mo2InstallationReference.CurrentSchemaVersion,
            new InstallationReferenceId($"reference.mo2.{opaqueSuffix}"),
            new InstallationId($"installation.mo2.{opaqueSuffix}"),
            request.Validation.ExpectedGameId,
            request.AdapterId,
            displayName,
            instanceKind,
            validation.ExecutablePath,
            validation.InstanceDirectory);

        try
        {
            await referenceStore.SaveAsync(loaded.References.Add(reference), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return new(
                false,
                null,
                validation,
                [new(
                    "mo2.connection.persistence_failed",
                    Mo2IssueSeverity.Error,
                    exception is UnauthorizedAccessException
                        ? "Grid could not save the connection reference because access was denied."
                        : "Grid could not safely save the connection reference; no MO2 files were changed.")]);
        }

        return new(true, reference, validation, [new("mo2.connection.saved", Mo2IssueSeverity.Information, "Grid saved a read-only link; no MO2 files were changed.")]);
    }

    private string DeriveConnectionKey(Mo2InstallationReference reference)
    {
        var applicationDirectory = Path.GetDirectoryName(reference.ExecutablePath)
            ?? throw new InvalidDataException("Connected MO2 executable has no application directory.");
        return paths.GetIdentityKey(applicationDirectory, reference.InstanceDirectory);
    }
}
