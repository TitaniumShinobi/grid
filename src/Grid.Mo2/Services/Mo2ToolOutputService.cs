using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2ToolOutputService
{
    public const string ObserverVersion = "grid.mo2.tool-output.v1";

    private readonly IMo2ContentTreeObserver trees;
    private readonly IMo2RandomAccessFileFactory files;
    private readonly IMo2PathCanonicalizer paths;
    private readonly IMo2SessionContentPathAuthorization contentAuthorization;
    private readonly Mo2ToolOutputObservationLimits limits;
    private readonly Func<DateTimeOffset> utcNow;

    public Mo2ToolOutputService(
        IMo2ContentTreeObserver trees,
        IMo2RandomAccessFileFactory files,
        IMo2PathCanonicalizer paths,
        IMo2SessionContentPathAuthorization contentAuthorization,
        Mo2ToolOutputObservationLimits? limits = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.trees = trees ?? throw new ArgumentNullException(nameof(trees));
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.contentAuthorization = contentAuthorization ?? throw new ArgumentNullException(nameof(contentAuthorization));
        this.limits = limits ?? Mo2ToolOutputObservationLimits.Default;
        this.limits.Validate();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<Mo2ToolOutputSnapshot> ObserveAsync(
        Mo2ToolOutputObservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var observedAt = utcNow().ToUniversalTime();
        var issues = ImmutableArray.CreateBuilder<Mo2ValidationIssue>();
        if (!IsCurrentReference(request))
        {
            issues.Add(new(
                "mo2.outputs.validation_required",
                Mo2IssueSeverity.Error,
                "A current validation bound to this connected MO2 reference is required."));
            return Empty(request, observedAt, issues);
        }

        var candidates = BuildCandidates(request, issues, cancellationToken);
        var outputs = ImmutableArray.CreateBuilder<Mo2GeneratedOutputObservation>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outputs.Add(await ObserveCandidateAsync(request, candidate, cancellationToken).ConfigureAwait(false));
        }

        var ordered = outputs
            .OrderBy(output => output.Kind == Mo2GeneratedOutputKind.Overwrite ? 0 : 1)
            .ThenBy(output => output.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(output => output.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var allIssues = issues.Concat(ordered.SelectMany(output => output.Issues)).ToImmutableArray();
        var status = Status(ordered, allIssues);
        var fingerprint = Fingerprint(ordered);
        return new(
            SnapshotId(request.Reference.Id, request.Profile.Id, fingerprint),
            request.Reference.Id,
            request.Profile.Id,
            observedAt,
            status,
            ordered,
            fingerprint,
            allIssues);
    }

    private bool IsCurrentReference(Mo2ToolOutputObservationRequest request) =>
        request.Validation.InstanceDirectory is not null &&
        request.Validation.Status == Mo2ValidationStatus.Valid &&
        paths.Equals(request.Validation.InstanceDirectory, request.Reference.InstanceDirectory);

    private ImmutableArray<OutputCandidate> BuildCandidates(
        Mo2ToolOutputObservationRequest request,
        ImmutableArray<Mo2ValidationIssue>.Builder issues,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<OutputCandidate>();
        if (!string.IsNullOrWhiteSpace(request.Validation.OverwriteDirectory))
        {
            result.Add(new(
                "MO2 Overwrite",
                request.Validation.OverwriteDirectory!,
                Mo2GeneratedOutputKind.Overwrite,
                null,
                true,
                null,
                Mo2RecognizedToolFamily.Unknown,
                Mo2OutputAssociationConfidence.ConfirmedByMo2,
                ["MO2 configured overwrite directory"]));
        }

        var inventory = request.Profile.Inventory?.Entries ?? [];
        var toolsByTitle = (request.RecognizedTools.IsDefault ? [] : request.RecognizedTools)
            .GroupBy(tool => tool.ConfiguredTitle, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), StringComparer.OrdinalIgnoreCase);
        var coveredMods = new HashSet<ModId>();
        foreach (var mapping in request.CustomOverwriteMappings.IsDefault ? [] : request.CustomOverwriteMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = inventory.Where(row =>
                    string.Equals(row.Name, mapping.OutputModName, StringComparison.OrdinalIgnoreCase) &&
                    row.Reconciliation == Mo2ModReconciliationState.Matched &&
                    row.Directory is not null)
                .ToArray();
            if (rows.Length != 1)
            {
                issues.Add(new(
                    "mo2.outputs.custom_mapping_ambiguous",
                    Mo2IssueSeverity.Warning,
                    "An MO2 custom output mapping did not resolve to exactly one authoritative mod directory."));
                continue;
            }

            var row = rows[0];
            if (!coveredMods.Add(row.Id))
            {
                continue;
            }

            var matchingTools = toolsByTitle.TryGetValue(mapping.ExecutableTitle, out var values) ? values : [];
            var corroborated = matchingTools.Where(tool => tool.Confidence == Mo2RecognitionConfidence.Corroborated).ToArray();
            var tool = corroborated.Length == 1 ? corroborated[0] : matchingTools.Length == 1 ? matchingTools[0] : null;
            var family = tool?.Family ?? Mo2RecognizedToolFamily.Unknown;
            result.Add(new(
                row.Name,
                row.Directory!.CanonicalPath,
                family == Mo2RecognizedToolFamily.Unknown
                    ? Mo2GeneratedOutputKind.UserDesignatedMod
                    : Mo2ToolRecognition.OutputKind(family),
                row.Id,
                row.IsEnabled,
                tool?.ExecutableId,
                family,
                Mo2OutputAssociationConfidence.ConfirmedByMo2,
                ["MO2 profile custom_overwrites mapping", $"source index {mapping.SourceIndex}"]));
        }

        var corroboratedTools = (request.RecognizedTools.IsDefault ? [] : request.RecognizedTools)
            .Where(tool => tool.Confidence == Mo2RecognitionConfidence.Corroborated &&
                Mo2ToolRecognition.OutputKind(tool.Family) != Mo2GeneratedOutputKind.UserDesignatedMod)
            .ToArray();
        foreach (var row in inventory.Where(row =>
            row.Reconciliation == Mo2ModReconciliationState.Matched && row.Directory is not null && !coveredMods.Contains(row.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var associations = corroboratedTools
                .Where(tool => NameMatchesFamily(row.Name, tool.Family))
                .ToArray();
            if (associations.Length != 1)
            {
                continue;
            }

            var tool = associations[0];
            coveredMods.Add(row.Id);
            result.Add(new(
                row.Name,
                row.Directory!.CanonicalPath,
                Mo2ToolRecognition.OutputKind(tool.Family),
                row.Id,
                row.IsEnabled,
                tool.ExecutableId,
                tool.Family,
                Mo2OutputAssociationConfidence.Corroborated,
                ["corroborated executable identity", "matching output-mod name evidence"]));
        }

        return result.ToImmutable();
    }

    private async Task<Mo2GeneratedOutputObservation> ObserveCandidateAsync(
        Mo2ToolOutputObservationRequest request,
        OutputCandidate candidate,
        CancellationToken cancellationToken)
    {
        var issues = ImmutableArray.CreateBuilder<Mo2ValidationIssue>();
        if (candidate.Kind == Mo2GeneratedOutputKind.Overwrite && !IsOverwriteAuthorized(request, candidate.Root))
        {
            issues.Add(new(
                "mo2.outputs.overwrite_authorization_required",
                Mo2IssueSeverity.Warning,
                "Exact session authorization is required before Grid observes the configured external overwrite directory."));
            return Unavailable(request.Reference.Id, request.Profile.Id, candidate, Mo2OutputAvailability.AuthorizationRequired, issues);
        }

        Mo2ContentTreeObservation tree;
        try
        {
            tree = await trees.ObserveAsync(new(
                request.Reference.Id,
                candidate.Kind == Mo2GeneratedOutputKind.Overwrite ? Mo2ContentRootKind.Overwrite : Mo2ContentRootKind.Mod,
                candidate.Root,
                limits.Content), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        issues.AddRange(tree.Issues);
        var availability = tree.State switch
        {
            Mo2PathState.Present when tree.IsPartial => Mo2OutputAvailability.Inconsistent,
            Mo2PathState.Present => Mo2OutputAvailability.Available,
            Mo2PathState.Missing => Mo2OutputAvailability.Missing,
            Mo2PathState.Inaccessible => Mo2OutputAvailability.Inaccessible,
            Mo2PathState.AuthorizationRequired => Mo2OutputAvailability.AuthorizationRequired,
            _ => Mo2OutputAvailability.Inconsistent,
        };
        var fingerprint = tree.State == Mo2PathState.Present
            ? await FingerprintAsync(tree, cancellationToken).ConfigureAwait(false)
            : UnavailableFingerprint(tree.Issues);
        issues.AddRange(fingerprint.Issues);
        return new(
            OutputId(request.Reference.Id, request.Profile.Id, candidate.Kind, candidate.ModId, candidate.DisplayName),
            request.Profile.Id,
            SafeDisplay(candidate.DisplayName),
            candidate.Kind,
            availability,
            candidate.IsEnabled,
            candidate.ModId,
            candidate.ExecutableId,
            candidate.Family,
            candidate.Confidence,
            fingerprint,
            Mo2OutputComparisonState.FirstObservation,
            candidate.Evidence,
            issues.ToImmutable(),
            tree.CanonicalRoot);
    }

    private bool IsOverwriteAuthorized(Mo2ToolOutputObservationRequest request, string root)
    {
        var executableRoot = Path.GetDirectoryName(request.Reference.ExecutablePath);
        return paths.IsWithinRoot(root, request.Reference.InstanceDirectory) ||
            executableRoot is not null && paths.IsWithinRoot(root, executableRoot) ||
            contentAuthorization.IsRootAuthorized(request.Reference.Id, Mo2ContentRootKind.Overwrite, root);
    }

    private async Task<Mo2GeneratedOutputFingerprint> FingerprintAsync(
        Mo2ContentTreeObservation tree,
        CancellationToken cancellationToken)
    {
        var filesToHash = tree.Entries
            .Where(entry => entry.Kind == Mo2ContentEntryKind.File)
            .OrderBy(entry => entry.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.VirtualPath, StringComparer.Ordinal)
            .ToImmutableArray();
        var directoryCount = tree.Entries.LongCount(entry => entry.Kind == Mo2ContentEntryKind.Directory);
        long totalBytes;
        try
        {
            totalBytes = filesToHash.Aggregate(0L, (total, entry) => checked(total + (entry.Length ?? 0)));
        }
        catch (OverflowException)
        {
            return Structural(tree, filesToHash.Length, directoryCount, long.MaxValue,
                "The output byte count exceeded the supported range.");
        }

        if (tree.IsPartial || totalBytes > limits.MaximumContentHashBytes)
        {
            return Structural(
                tree,
                filesToHash.Length,
                directoryCount,
                totalBytes,
                tree.IsPartial
                    ? "A complete content fingerprint is unavailable because the directory observation is partial."
                    : "The output exceeds the content-fingerprint byte budget; only structural evidence was recorded.");
        }

        var hashes = new byte[filesToHash.Length][];
        var failures = new bool[filesToHash.Length];
        var channel = Channel.CreateBounded<(int Index, Mo2ContentTreeEntry Entry)>(new BoundedChannelOptions(
            Math.Max(1, limits.MaximumConcurrentReaders * 2))
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var workers = Enumerable.Range(0, limits.MaximumConcurrentReaders)
            .Select(_ => HashWorkerAsync(channel.Reader, hashes, failures, cancellationToken))
            .ToArray();
        for (var index = 0; index < filesToHash.Length; index++)
        {
            await channel.Writer.WriteAsync((index, filesToHash[index]), cancellationToken).ConfigureAwait(false);
        }

        channel.Writer.Complete();
        await Task.WhenAll(workers).ConfigureAwait(false);
        if (failures.Any(value => value))
        {
            return Structural(tree, filesToHash.Length, directoryCount, totalBytes,
                "At least one file could not be read consistently; only structural evidence was recorded.", partial: true);
        }

        using var combined = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var index = 0; index < filesToHash.Length; index++)
        {
            Append(combined, filesToHash[index].VirtualPath);
            combined.AppendData(hashes[index]);
        }

        return new(
            Mo2OutputFingerprintStrength.CompleteContent,
            tree.MembershipFingerprint,
            Convert.ToHexString(combined.GetHashAndReset()).ToLowerInvariant(),
            filesToHash.Length,
            directoryCount,
            totalBytes,
            []);
    }

    private async Task HashWorkerAsync(
        ChannelReader<(int Index, Mo2ContentTreeEntry Entry)> reader,
        byte[][] hashes,
        bool[] failures,
        CancellationToken cancellationToken)
    {
        await foreach (var work in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await using var file = await files.OpenReadAsync(work.Entry.CanonicalPath, cancellationToken).ConfigureAwait(false);
                if (file.Length != work.Entry.Length)
                {
                    failures[work.Index] = true;
                    continue;
                }

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[limits.BufferSize];
                long offset = 0;
                while (offset < file.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(buffer.Length, file.Length - offset);
                    var read = await file.ReadAsync(offset, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        failures[work.Index] = true;
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    offset = checked(offset + read);
                }

                var final = await file.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false);
                if (failures[work.Index] || final != file.InitialStamp)
                {
                    failures[work.Index] = true;
                    continue;
                }

                hashes[work.Index] = hash.GetHashAndReset();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                failures[work.Index] = true;
            }
        }
    }

    private static Mo2GeneratedOutputFingerprint Structural(
        Mo2ContentTreeObservation tree,
        long fileCount,
        long directoryCount,
        long totalBytes,
        string message,
        bool partial = false) =>
        new(
            partial || tree.IsPartial ? Mo2OutputFingerprintStrength.Partial : Mo2OutputFingerprintStrength.Structural,
            tree.MembershipFingerprint,
            null,
            fileCount,
            directoryCount,
            totalBytes,
            [new("mo2.outputs.content_fingerprint_incomplete", Mo2IssueSeverity.Warning, message)]);

    private static Mo2GeneratedOutputFingerprint UnavailableFingerprint(
        ImmutableArray<Mo2ValidationIssue> issues) =>
        new(Mo2OutputFingerprintStrength.Unavailable, null, null, 0, 0, 0, issues);

    private static Mo2GeneratedOutputObservation Unavailable(
        InstallationReferenceId referenceId,
        ProfileId profileId,
        OutputCandidate candidate,
        Mo2OutputAvailability availability,
        ImmutableArray<Mo2ValidationIssue>.Builder issues) =>
        new(
            OutputId(referenceId, profileId, candidate.Kind, candidate.ModId, candidate.DisplayName),
            profileId,
            SafeDisplay(candidate.DisplayName),
            candidate.Kind,
            availability,
            candidate.IsEnabled,
            candidate.ModId,
            candidate.ExecutableId,
            candidate.Family,
            candidate.Confidence,
            UnavailableFingerprint(issues.ToImmutable()),
            Mo2OutputComparisonState.FirstObservation,
            candidate.Evidence,
            issues.ToImmutable(),
            null);

    private static Mo2ToolOutputSnapshot Empty(
        Mo2ToolOutputObservationRequest request,
        DateTimeOffset observedAt,
        ImmutableArray<Mo2ValidationIssue>.Builder issues)
    {
        const string fingerprint = "unavailable";
        return new(
            SnapshotId(request.Reference.Id, request.Profile.Id, fingerprint),
            request.Reference.Id,
            request.Profile.Id,
            observedAt,
            Mo2ToolOutputObservationStatus.Unavailable,
            [],
            fingerprint,
            issues.ToImmutable());
    }

    private static Mo2ToolOutputObservationStatus Status(
        ImmutableArray<Mo2GeneratedOutputObservation> outputs,
        ImmutableArray<Mo2ValidationIssue> issues)
    {
        if (outputs.Length == 0) return Mo2ToolOutputObservationStatus.Unavailable;
        if (outputs.Any(output => output.Availability == Mo2OutputAvailability.Inconsistent))
            return Mo2ToolOutputObservationStatus.Inconsistent;
        if (outputs.Any(output => output.Availability != Mo2OutputAvailability.Available) ||
            outputs.Any(output => output.Fingerprint.Strength != Mo2OutputFingerprintStrength.CompleteContent) ||
            issues.Any(issue => issue.Severity >= Mo2IssueSeverity.Warning))
            return Mo2ToolOutputObservationStatus.Partial;
        return Mo2ToolOutputObservationStatus.Complete;
    }

    private static bool NameMatchesFamily(string name, Mo2RecognizedToolFamily family)
    {
        var value = name.ToLowerInvariant();
        return family switch
        {
            Mo2RecognizedToolFamily.BodySlide => value.Contains("bodyslide") || value.Contains("body slide"),
            Mo2RecognizedToolFamily.Pandora => value.Contains("pandora"),
            Mo2RecognizedToolFamily.Nemesis => value.Contains("nemesis"),
            Mo2RecognizedToolFamily.Fnis => value.Contains("fnis"),
            Mo2RecognizedToolFamily.Synthesis => value.Contains("synthesis"),
            Mo2RecognizedToolFamily.WryeBash => value.Contains("bashed patch") || value.Contains("wrye bash"),
            Mo2RecognizedToolFamily.TexGen => value.Contains("texgen"),
            Mo2RecognizedToolFamily.DynDoLod => value.Contains("dyndolod"),
            Mo2RecognizedToolFamily.XLodGen => value.Contains("xlodgen") || value.Contains("lodgen"),
            _ => false,
        };
    }

    private static string SafeDisplay(string value)
    {
        var filtered = new string(value.Where(character => !char.IsControl(character)).Take(160).ToArray()).Trim();
        return filtered.Length == 0 ? "Unnamed output" : filtered;
    }

    private static string Fingerprint(ImmutableArray<Mo2GeneratedOutputObservation> outputs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, ObserverVersion);
        foreach (var output in outputs)
        {
            Append(hash, $"{output.Id}:{output.Kind}:{output.Availability}:{output.AssociationConfidence}:" +
                $"{output.Fingerprint.Strength}:{output.Fingerprint.StructuralFingerprint}:{output.Fingerprint.ContentFingerprint}");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static GeneratedOutputId OutputId(
        InstallationReferenceId referenceId,
        ProfileId profileId,
        Mo2GeneratedOutputKind kind,
        ModId? modId,
        string name) =>
        new($"output.{Hash($"{referenceId}|{profileId}|{kind}|{modId}|{name}")[..24]}");

    private static ExternalObservationSnapshotId SnapshotId(
        InstallationReferenceId referenceId,
        ProfileId profileId,
        string fingerprint) =>
        new($"tool-output.{Hash($"{referenceId}|{profileId}|{fingerprint}")[..24]}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));

    private sealed record OutputCandidate(
        string DisplayName,
        string Root,
        Mo2GeneratedOutputKind Kind,
        ModId? ModId,
        bool? IsEnabled,
        ObservedExecutableId? ExecutableId,
        Mo2RecognizedToolFamily Family,
        Mo2OutputAssociationConfidence Confidence,
        ImmutableArray<string> Evidence);
}

public sealed class Mo2WorkspaceToolOutputQueryService : IWorkspaceToolOutputQueryService
{
    private readonly IMo2InstallationReferenceStore references;
    private readonly Func<Mo2InstallationReference, IMo2InstallationValidator> validatorFactory;
    private readonly IMo2ProfileSnapshotService profiles;
    private readonly IMo2ModInventoryService inventory;
    private readonly Mo2ExecutableConfigurationService executableService;
    private readonly Mo2ToolOutputSnapshotCache outputCache;
    private readonly ConcurrentDictionary<WorkspaceToolOutputContext, ToolOutputObservationSnapshot> latest = new();
    private readonly ConcurrentDictionary<ExternalObservationSnapshotId, Mo2ExecutableConfigurationSnapshot> rawConfigurations = new();
    private readonly ConcurrentDictionary<ObservedExecutableId, Mo2ObservedExecutable> rawExecutables = new();
    private readonly ConcurrentDictionary<ObservedExecutableId, Mo2ExecutableSourceProvenance?> rawExecutableProvenance = new();
    private readonly ConcurrentDictionary<GeneratedOutputId, Mo2GeneratedOutputObservation> rawOutputs = new();

    public Mo2WorkspaceToolOutputQueryService(
        IMo2InstallationReferenceStore references,
        Func<Mo2InstallationReference, IMo2InstallationValidator> validatorFactory,
        IMo2ProfileSnapshotService profiles,
        IMo2ModInventoryService inventory,
        Mo2ExecutableConfigurationService executableService,
        Mo2ToolOutputSnapshotCache outputCache)
    {
        this.references = references ?? throw new ArgumentNullException(nameof(references));
        this.validatorFactory = validatorFactory ?? throw new ArgumentNullException(nameof(validatorFactory));
        this.profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        this.inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        this.executableService = executableService ?? throw new ArgumentNullException(nameof(executableService));
        this.outputCache = outputCache ?? throw new ArgumentNullException(nameof(outputCache));
    }

    public async Task<ToolOutputObservationRefreshResult> RefreshAsync(
        WorkspaceToolOutputContext context,
        bool forceRefresh,
        IProgress<ExternalObservationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && latest.TryGetValue(context, out var cached))
        {
            return new(
                ExternalObservationRefreshStatus.Completed,
                cached with { IsFromSessionCache = true },
                "Complete read-only MO2 tool/output evidence restored from the session cache.",
                []);
        }

        try
        {
            progress?.Report(new(ExternalObservationStage.Validating, 0, null, "Revalidating the connected MO2 reference."));
            var observed = await ObserveContextAsync(context, cancellationToken).ConfigureAwait(false);
            if (observed is null)
            {
                return new(
                    ExternalObservationRefreshStatus.Unavailable,
                    LatestOrNull(context),
                    "The selected connected MO2 profile is unavailable.",
                    []);
            }

            var (reference, validation, profile) = observed.Value;
            progress?.Report(new(ExternalObservationStage.ReadingConfiguration, 1, 5, "Reading MO2 executable configuration without modifying it."));
            var executableSnapshot = await executableService.ObserveAsync(
                new(reference, validation),
                cancellationToken).ConfigureAwait(false);
            var recognition = executableSnapshot.Entries.Select(entry =>
            {
                var id = ExecutableId(reference.Id, entry);
                return Mo2ToolRecognition.Recognize(new(
                    id,
                    entry.Title,
                    entry.Binary.CanonicalPath is null ? null : Path.GetFileName(entry.Binary.CanonicalPath),
                    null,
                    []));
            }).ToImmutableArray();

            progress?.Report(new(ExternalObservationStage.ObservingOutputs, 2, 5, "Observing Overwrite and corroborated output mods."));
            var mappings = (profile.Settings?.CustomOverwrites.IsDefaultOrEmpty == false
                    ? profile.Settings.CustomOverwrites
                    : [])
                .Select((mapping, index) => new Mo2CustomOverwriteMapping(
                    mapping.ExecutableTitle,
                    mapping.OutputModName,
                    index,
                    mapping.SourceLineIndex))
                .ToImmutableArray();
            var compared = await outputCache.RefreshAsync(
                new(reference, validation, profile, recognition, mappings),
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new(ExternalObservationStage.ComparingSnapshots, 4, 5, "Comparing immutable session observations."));
            latest.TryGetValue(context, out var previousPublic);
            var executableSummaries = executableSnapshot.Entries.Select(entry =>
            {
                var id = ExecutableId(reference.Id, entry);
                var recognized = recognition.Single(result => result.ExecutableId == id);
                rawExecutables[id] = entry;
                rawExecutableProvenance[id] = executableSnapshot.Provenance;
                var provisional = ToSummary(id, entry, recognized, ExternalObservationChange.FirstObservation);
                var previous = previousPublic?.Executables.FirstOrDefault(value => value.Id == id);
                return provisional with { Change = Compare(previous, provisional) };
            }).ToImmutableArray();
            var outputSummaries = compared.Current.Outputs.Select(output =>
            {
                rawOutputs[output.Id] = output;
                return ToSummary(output);
            }).Concat(compared.RemovedOutputs.Select(ToSummary)).ToImmutableArray();
            var warnings = executableSnapshot.Issues.Select(issue => issue.Message)
                .Concat(executableSnapshot.Entries.SelectMany(entry => entry.Warnings.Select(warning => warning.Message)))
                .Concat(compared.Current.Issues.Select(issue => issue.Message))
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray();
            var publicId = PublicSnapshotId(context, executableSnapshot.Revision, compared.Current.Fingerprint);
            var status = PublicStatus(executableSnapshot, compared.Current);
            var summary = new ToolOutputObservationSummary(
                status,
                compared.Current.ObservedAtUtc,
                PublicFingerprint(executableSnapshot.Revision, compared.Current.Fingerprint),
                executableSummaries.Length,
                outputSummaries.Length,
                warnings.Length,
                previousPublic is not null || compared.Previous is not null);
            var publicSnapshot = new ToolOutputObservationSnapshot(
                publicId,
                context,
                summary,
                executableSummaries,
                outputSummaries,
                warnings,
                false);
            rawConfigurations[publicId] = executableSnapshot;
            var refreshStatus = status == ExternalObservationStatus.Complete
                ? ExternalObservationRefreshStatus.Completed
                : status == ExternalObservationStatus.AuthorizationRequired
                    ? ExternalObservationRefreshStatus.AuthorizationRequired
                    : ExternalObservationRefreshStatus.Partial;
            if (refreshStatus == ExternalObservationRefreshStatus.Completed)
            {
                latest[context] = publicSnapshot;
            }

            progress?.Report(new(ExternalObservationStage.Publishing, 5, 5, "Published read-only MO2 tool/output evidence."));
            var authorizations = compared.Current.Outputs
                .Where(output => output.Availability == Mo2OutputAvailability.AuthorizationRequired)
                .Select(output => output.Kind == Mo2GeneratedOutputKind.Overwrite ? "External overwrite" : "External output")
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray();
            return new(
                refreshStatus,
                publicSnapshot,
                refreshStatus == ExternalObservationRefreshStatus.Completed
                    ? "Read-only MO2 executable and output observation completed. No external files were changed."
                    : "The read-only observation is partial; review warnings and required authorizations.",
                authorizations);
        }
        catch (OperationCanceledException)
        {
            return new(
                ExternalObservationRefreshStatus.Canceled,
                LatestOrNull(context),
                "The read-only MO2 tool/output observation was canceled. The previous complete snapshot was retained.",
                []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(
                ExternalObservationRefreshStatus.Failed,
                LatestOrNull(context),
                "The read-only MO2 tool/output observation failed without changing external state.",
                []);
        }
    }

    public bool TryGetExecutable(
        ObservedExecutableId id,
        out Mo2ObservedExecutable executable,
        out Mo2ExecutableSourceProvenance? provenance)
    {
        if (rawExecutables.TryGetValue(id, out executable!))
        {
            rawExecutableProvenance.TryGetValue(id, out provenance);
            return true;
        }

        executable = null!;
        provenance = null;
        return false;
    }

    public bool TryGetOutput(GeneratedOutputId id, out Mo2GeneratedOutputObservation output) =>
        rawOutputs.TryGetValue(id, out output!);

    public bool TryGetConfiguration(
        ExternalObservationSnapshotId id,
        out Mo2ExecutableConfigurationSnapshot? configuration) =>
        rawConfigurations.TryGetValue(id, out configuration);

    public bool TryGetLatest(
        WorkspaceToolOutputContext context,
        out ToolOutputObservationSnapshot? snapshot) =>
        latest.TryGetValue(context, out snapshot);

    public ToolOutputObservationSnapshot? FindLatest(
        InstallationId installationId,
        ProfileId profileId) =>
        latest
            .Where(pair => pair.Key.InstallationId == installationId && pair.Key.ProfileId == profileId)
            .OrderByDescending(pair => pair.Value.Summary.ObservedAtUtc)
            .Select(pair => pair.Value)
            .FirstOrDefault();

    private async Task<(Mo2InstallationReference Reference, Mo2InstallationValidation Validation, Mo2ObservedProfile Profile)?> ObserveContextAsync(
        WorkspaceToolOutputContext context,
        CancellationToken cancellationToken)
    {
        var loaded = await references.LoadAsync(cancellationToken).ConfigureAwait(false);
        var reference = loaded.References.FirstOrDefault(value =>
            value.InstallationId == context.InstallationId &&
            value.Id == context.ReferenceId &&
            value.GameId == context.GameId);
        if (reference is null)
        {
            return null;
        }

        var validation = await validatorFactory(reference).ValidateAsync(
            new(Path.GetDirectoryName(reference.ExecutablePath), reference.InstanceDirectory, reference.GameId),
            cancellationToken).ConfigureAwait(false);
        if (validation.Status is Mo2ValidationStatus.Invalid or Mo2ValidationStatus.Inaccessible)
        {
            return null;
        }

        var profileSnapshot = await profiles.ObserveAsync(new(reference, validation), cancellationToken).ConfigureAwait(false);
        var profile = profileSnapshot.Profiles.FirstOrDefault(value => value.Id == context.ProfileId);
        if (profile is null)
        {
            return null;
        }

        var inventorySnapshot = await inventory.ObserveAsync(
            new(reference, validation, profileSnapshot),
            cancellationToken).ConfigureAwait(false);
        var profileInventory = inventorySnapshot.Profiles.FirstOrDefault(value => value.ProfileId == profile.Id);
        if (profileInventory is not null)
        {
            profile = profile with { Inventory = profileInventory };
        }

        return (reference, validation, profile);
    }

    private ToolOutputObservationSnapshot? LatestOrNull(WorkspaceToolOutputContext context) =>
        latest.GetValueOrDefault(context);

    private static ObservedExecutableSummary ToSummary(
        ObservedExecutableId id,
        Mo2ObservedExecutable entry,
        Mo2ToolRecognitionResult recognition,
        ExternalObservationChange change) =>
        new(
            id,
            SafeDisplay(entry.Title, "Unnamed MO2 executable"),
            entry.IsDuplicate ? ObservedExecutableAvailability.Duplicate : entry.Availability switch
            {
                Mo2ExecutablePathAvailability.Available => ObservedExecutableAvailability.Available,
                Mo2ExecutablePathAvailability.Missing => ObservedExecutableAvailability.Missing,
                Mo2ExecutablePathAvailability.Inaccessible => ObservedExecutableAvailability.Inaccessible,
                Mo2ExecutablePathAvailability.OutsideExpectedRoots => ObservedExecutableAvailability.OutsideExpectedRoots,
                Mo2ExecutablePathAvailability.Invalid or Mo2ExecutablePathAvailability.NotConfigured => ObservedExecutableAvailability.Unsupported,
                _ => ObservedExecutableAvailability.Unsupported,
            },
            entry.Binary.LocationTrust switch
            {
                Mo2ExecutableLocationTrust.ExpectedRoot => ObservedLocationTrust.ExpectedRoot,
                Mo2ExecutableLocationTrust.ExplicitlyAuthorized => ObservedLocationTrust.SessionAuthorized,
                Mo2ExecutableLocationTrust.OutsideExpectedRoots => ObservedLocationTrust.OutsideExpectedRoots,
                _ => ObservedLocationTrust.Unknown,
            },
            recognition.Family switch
            {
                Mo2RecognizedToolFamily.Skse => ObservedToolFamily.Skse,
                Mo2RecognizedToolFamily.SkyrimLauncher => ObservedToolFamily.SkyrimLauncher,
                Mo2RecognizedToolFamily.SseEdit => ObservedToolFamily.SseEdit,
                Mo2RecognizedToolFamily.ZEdit => ObservedToolFamily.ZEdit,
                Mo2RecognizedToolFamily.Synthesis => ObservedToolFamily.Synthesis,
                Mo2RecognizedToolFamily.Pandora => ObservedToolFamily.Pandora,
                Mo2RecognizedToolFamily.Loot => ObservedToolFamily.Loot,
                Mo2RecognizedToolFamily.BodySlide => ObservedToolFamily.BodySlide,
                Mo2RecognizedToolFamily.TexGen => ObservedToolFamily.TexGen,
                Mo2RecognizedToolFamily.DynDoLod => ObservedToolFamily.DynDoLod,
                Mo2RecognizedToolFamily.XLodGen => ObservedToolFamily.XLodGen,
                Mo2RecognizedToolFamily.WryeBash => ObservedToolFamily.WryeBash,
                Mo2RecognizedToolFamily.CreationKit => ObservedToolFamily.CreationKit,
                _ => ObservedToolFamily.Unknown,
            },
            recognition.Confidence switch
            {
                Mo2RecognitionConfidence.Candidate => EvidenceConfidence.Candidate,
                Mo2RecognitionConfidence.Corroborated => EvidenceConfidence.Corroborated,
                _ => EvidenceConfidence.None,
            },
            entry.OwnIcon == true ? ObservedIconAvailability.Unsupported : ObservedIconAvailability.NotConfigured,
            entry.SourceOrder,
            entry.Warnings.Length,
            entry.Fingerprint,
            change);

    private static GeneratedOutputSummary ToSummary(Mo2GeneratedOutputObservation output) =>
        new(
            output.Id,
            output.DisplayName,
            output.Kind switch
            {
                Mo2GeneratedOutputKind.Overwrite => GeneratedOutputKind.Overwrite,
                Mo2GeneratedOutputKind.BodySlide => GeneratedOutputKind.BodySlide,
                Mo2GeneratedOutputKind.BehaviorGeneration => GeneratedOutputKind.BehaviorGeneration,
                Mo2GeneratedOutputKind.Synthesis => GeneratedOutputKind.Synthesis,
                Mo2GeneratedOutputKind.WryeBash => GeneratedOutputKind.WryeBash,
                Mo2GeneratedOutputKind.TexGen => GeneratedOutputKind.TexGen,
                Mo2GeneratedOutputKind.DynDoLod => GeneratedOutputKind.DynDoLod,
                Mo2GeneratedOutputKind.XLodGen => GeneratedOutputKind.XLodGen,
                Mo2GeneratedOutputKind.UserDesignatedMod => GeneratedOutputKind.UserDesignated,
                _ => GeneratedOutputKind.Unknown,
            },
            output.Kind == Mo2GeneratedOutputKind.Overwrite
                ? GeneratedOutputLocationKind.Overwrite
                : GeneratedOutputLocationKind.InventoryMod,
            output.Availability switch
            {
                Mo2OutputAvailability.Available => GeneratedOutputAvailability.Available,
                Mo2OutputAvailability.Missing => GeneratedOutputAvailability.Missing,
                Mo2OutputAvailability.Inaccessible => GeneratedOutputAvailability.Inaccessible,
                Mo2OutputAvailability.Ambiguous => GeneratedOutputAvailability.Ambiguous,
                Mo2OutputAvailability.AuthorizationRequired => GeneratedOutputAvailability.AuthorizationRequired,
                _ => GeneratedOutputAvailability.Inconsistent,
            },
            output.IsEnabled switch
            {
                true => GeneratedOutputEnabledState.Enabled,
                false => GeneratedOutputEnabledState.Disabled,
                null when output.Kind == Mo2GeneratedOutputKind.Overwrite => GeneratedOutputEnabledState.NotApplicable,
                _ => GeneratedOutputEnabledState.Unknown,
            },
            output.AssociationConfidence switch
            {
                Mo2OutputAssociationConfidence.ConfirmedByMo2 => EvidenceConfidence.Confirmed,
                Mo2OutputAssociationConfidence.Corroborated => EvidenceConfidence.Corroborated,
                Mo2OutputAssociationConfidence.Ambiguous => EvidenceConfidence.Ambiguous,
                _ => EvidenceConfidence.None,
            },
            output.Fingerprint.Strength switch
            {
                Mo2OutputFingerprintStrength.CompleteContent => OutputFingerprintStrength.ContentComplete,
                Mo2OutputFingerprintStrength.Structural => OutputFingerprintStrength.Structural,
                Mo2OutputFingerprintStrength.Partial => OutputFingerprintStrength.Partial,
                _ => OutputFingerprintStrength.Indeterminate,
            },
            output.Fingerprint.FileCount,
            output.Fingerprint.DirectoryCount,
            output.Fingerprint.TotalBytes,
            output.Issues.Length,
            output.Fingerprint.ContentFingerprint ?? output.Fingerprint.StructuralFingerprint ?? "unavailable",
            output.Comparison switch
            {
                Mo2OutputComparisonState.FirstObservation => ExternalObservationChange.FirstObservation,
                Mo2OutputComparisonState.NoChange => ExternalObservationChange.Unchanged,
                Mo2OutputComparisonState.NoChangeObserved => ExternalObservationChange.NoChangeObserved,
                Mo2OutputComparisonState.Added => ExternalObservationChange.Added,
                Mo2OutputComparisonState.Removed => ExternalObservationChange.Removed,
                Mo2OutputComparisonState.Modified => ExternalObservationChange.Modified,
                Mo2OutputComparisonState.Reclassified => ExternalObservationChange.Reclassified,
                Mo2OutputComparisonState.AvailabilityChanged => ExternalObservationChange.AvailabilityChanged,
                Mo2OutputComparisonState.AssociationChanged => ExternalObservationChange.AssociationChanged,
                _ => ExternalObservationChange.Indeterminate,
            },
            output.AssociatedExecutableId,
            output.ModId);

    private static ExternalObservationChange Compare(
        ObservedExecutableSummary? previous,
        ObservedExecutableSummary current)
    {
        if (previous is null) return ExternalObservationChange.FirstObservation;
        if (previous.Availability != current.Availability) return ExternalObservationChange.AvailabilityChanged;
        if (previous.RecognizedFamily != current.RecognizedFamily) return ExternalObservationChange.Reclassified;
        if (previous.RecognitionConfidence != current.RecognitionConfidence) return ExternalObservationChange.Reclassified;
        return string.Equals(previous.Fingerprint, current.Fingerprint, StringComparison.Ordinal)
            ? ExternalObservationChange.Unchanged
            : ExternalObservationChange.Modified;
    }

    private static ExternalObservationStatus PublicStatus(
        Mo2ExecutableConfigurationSnapshot executable,
        Mo2ToolOutputSnapshot outputs)
    {
        if (outputs.Outputs.Any(output => output.Availability == Mo2OutputAvailability.AuthorizationRequired))
            return ExternalObservationStatus.AuthorizationRequired;
        if (executable.Status is Mo2ExecutableConfigurationStatus.Missing or Mo2ExecutableConfigurationStatus.Inaccessible ||
            outputs.Status == Mo2ToolOutputObservationStatus.Unavailable)
            return ExternalObservationStatus.Unavailable;
        if (executable.Status == Mo2ExecutableConfigurationStatus.ChangedDuringRead ||
            outputs.Status == Mo2ToolOutputObservationStatus.Inconsistent)
            return ExternalObservationStatus.Stale;
        if (executable.Status != Mo2ExecutableConfigurationStatus.Complete ||
            outputs.Status != Mo2ToolOutputObservationStatus.Complete)
            return ExternalObservationStatus.Partial;
        return ExternalObservationStatus.Complete;
    }

    private static ObservedExecutableId ExecutableId(
        InstallationReferenceId referenceId,
        Mo2ObservedExecutable entry) =>
        new($"observed-executable.{Sha256($"{referenceId}|{entry.SourceIndex}|{entry.Fingerprint}")[..24]}");

    private static ExternalObservationSnapshotId PublicSnapshotId(
        WorkspaceToolOutputContext context,
        string executableRevision,
        string outputRevision) =>
        new($"tool-output-public.{Sha256($"{context.ReferenceId}|{context.ProfileId}|{executableRevision}|{outputRevision}")[..24]}");

    private static string PublicFingerprint(string executableRevision, string outputRevision) =>
        Sha256($"{executableRevision}|{outputRevision}");

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string SafeDisplay(string value, string fallback)
    {
        var filtered = new string(value.Where(character => !char.IsControl(character)).Take(160).ToArray()).Trim();
        return filtered.Length == 0 ? fallback : filtered;
    }
}
