using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public readonly record struct SourceAcquisitionReceiptId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "source-acquisition-receipt";

    [JsonConstructor]
    public SourceAcquisitionReceiptId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static SourceAcquisitionReceiptId DeriveV1(
        int receiptSchemaVersion,
        GameId gameId,
        SourceNativeIdentifier? distributionApplicationIdentity,
        SourceNativeVersion? distributionBuildVersion,
        SourceNativeIdentifier containerCoordinate,
        long containerByteLength,
        ContentDigest containerDigest,
        AcquisitionMethodCoordinate acquisitionMethod,
        ImmutableArray<SourceAcquisitionMember> members)
    {
        if (receiptSchemaVersion != SourceAcquisitionReceipt.CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(receiptSchemaVersion));
        CanonicalKnowledgeContract.RequireIdentifier(gameId.Value, nameof(gameId));
        if ((distributionApplicationIdentity is null) != (distributionBuildVersion is null))
            throw new ArgumentException("Distribution application and build coordinates must either both be present or both be absent.");
        ArgumentNullException.ThrowIfNull(containerCoordinate);
        ArgumentNullException.ThrowIfNull(acquisitionMethod);
        if (containerByteLength <= 0) throw new ArgumentOutOfRangeException(nameof(containerByteLength));
        if (members.IsDefaultOrEmpty || members.Any(value => value is null))
            throw new ArgumentException("An acquisition receipt requires at least one member.", nameof(members));

        var ordered = SourceAcquisitionReceipt.NormalizeMembers(members, nameof(members));
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddInt32("receipt-schema-version", receiptSchemaVersion);
        writer.AddString("game-id", gameId.Value);
        writer.AddOptionalExactNativeIdentifier("distribution-application", distributionApplicationIdentity);
        CanonicalKnowledgePackageEncoding.AddOptionalExactNativeVersion(
            writer,
            "distribution-build",
            distributionBuildVersion);
        writer.AddExactNativeIdentifier("container-coordinate", containerCoordinate);
        writer.AddInt64("container-byte-length", containerByteLength);
        writer.AddContentDigest("container-digest", containerDigest);
        acquisitionMethod.AddTo(writer, "acquisition-method");
        writer.AddInt32("members.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
            ordered[index].AddTo(writer, $"members.{index}");
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public sealed record AcquisitionMethodCoordinate
{
    [JsonConstructor]
    public AcquisitionMethodCoordinate(
        string methodId,
        string exactMethodVersion,
        string toolId,
        string exactToolVersion,
        ContentDigest toolArtifactDigest)
    {
        MethodId = SupportedKnowledgeFormat.RequireStrictText(methodId, nameof(methodId));
        ExactMethodVersion = SupportedKnowledgeFormat.RequireStrictText(exactMethodVersion, nameof(exactMethodVersion));
        ToolId = SupportedKnowledgeFormat.RequireStrictText(toolId, nameof(toolId));
        ExactToolVersion = SupportedKnowledgeFormat.RequireStrictText(exactToolVersion, nameof(exactToolVersion));
        _ = toolArtifactDigest.HexValue ?? throw new ArgumentException("Tool artifact digest must be initialized.", nameof(toolArtifactDigest));
        ToolArtifactDigest = toolArtifactDigest;
    }

    public string MethodId { get; }
    public string ExactMethodVersion { get; }
    public string ToolId { get; }
    public string ExactToolVersion { get; }
    public ContentDigest ToolArtifactDigest { get; }

    internal void AddTo(CanonicalIdentityWriter writer, string prefix)
    {
        writer.AddString($"{prefix}.id", MethodId);
        writer.AddString($"{prefix}.version", ExactMethodVersion);
        writer.AddString($"{prefix}.tool-id", ToolId);
        writer.AddString($"{prefix}.tool-version", ExactToolVersion);
        writer.AddContentDigest($"{prefix}.tool-artifact-digest", ToolArtifactDigest);
    }
}

public sealed record SourceAcquisitionMember
{
    [JsonConstructor]
    public SourceAcquisitionMember(
        SourceNativeIdentifier memberCoordinate,
        long byteLength,
        ContentDigest digest,
        SourceArtifactId artifactId)
    {
        ArgumentNullException.ThrowIfNull(memberCoordinate);
        if (byteLength <= 0) throw new ArgumentOutOfRangeException(nameof(byteLength));
        if (SourceArtifactId.DeriveV1(digest) != artifactId)
            throw new ArgumentException("Acquired member artifact identity does not match its digest.", nameof(artifactId));
        MemberCoordinate = memberCoordinate;
        ByteLength = byteLength;
        Digest = digest;
        ArtifactId = artifactId;
    }

    public SourceNativeIdentifier MemberCoordinate { get; }
    public long ByteLength { get; }
    public ContentDigest Digest { get; }
    public SourceArtifactId ArtifactId { get; }

    internal string SortKey => string.Join(
        "\u001f",
        MemberCoordinate.Namespace,
        MemberCoordinate.ObjectType,
        MemberCoordinate.ExactRepresentation,
        Convert.ToHexString(MemberCoordinate.IdentityBytes.AsSpan()),
        MemberCoordinate.ComparisonMethodId,
        MemberCoordinate.ComparisonMethodVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal void AddTo(CanonicalIdentityWriter writer, string prefix)
    {
        writer.AddExactNativeIdentifier($"{prefix}.coordinate", MemberCoordinate);
        writer.AddInt64($"{prefix}.byte-length", ByteLength);
        writer.AddContentDigest($"{prefix}.digest", Digest);
        writer.AddString($"{prefix}.artifact-id", ArtifactId.Value);
    }
}

public sealed record SourceAcquisitionReceipt
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public SourceAcquisitionReceipt(
        SourceAcquisitionReceiptId id,
        int receiptSchemaVersion,
        GameId gameId,
        SourceNativeIdentifier? distributionApplicationIdentity,
        SourceNativeVersion? distributionBuildVersion,
        SourceNativeIdentifier containerCoordinate,
        long containerByteLength,
        ContentDigest containerDigest,
        AcquisitionMethodCoordinate acquisitionMethod,
        ImmutableArray<SourceAcquisitionMember> members)
    {
        ArgumentNullException.ThrowIfNull(containerCoordinate);
        ArgumentNullException.ThrowIfNull(acquisitionMethod);
        var ordered = NormalizeMembers(members, nameof(members));
        var expected = SourceAcquisitionReceiptId.DeriveV1(
            receiptSchemaVersion,
            gameId,
            distributionApplicationIdentity,
            distributionBuildVersion,
            containerCoordinate,
            containerByteLength,
            containerDigest,
            acquisitionMethod,
            ordered);
        if (expected != id)
            throw new ArgumentException("Acquisition receipt identity does not match its deterministic content.", nameof(id));

        Id = id;
        ReceiptSchemaVersion = receiptSchemaVersion;
        GameId = gameId;
        DistributionApplicationIdentity = distributionApplicationIdentity;
        DistributionBuildVersion = distributionBuildVersion;
        ContainerCoordinate = containerCoordinate;
        ContainerByteLength = containerByteLength;
        ContainerDigest = containerDigest;
        AcquisitionMethod = acquisitionMethod;
        Members = ordered;
    }

    public SourceAcquisitionReceiptId Id { get; }
    public int ReceiptSchemaVersion { get; }
    public GameId GameId { get; }
    public SourceNativeIdentifier? DistributionApplicationIdentity { get; }
    public SourceNativeVersion? DistributionBuildVersion { get; }
    public SourceNativeIdentifier ContainerCoordinate { get; }
    public long ContainerByteLength { get; }
    public ContentDigest ContainerDigest { get; }
    public AcquisitionMethodCoordinate AcquisitionMethod { get; }
    public ImmutableArray<SourceAcquisitionMember> Members { get; }

    internal static ImmutableArray<SourceAcquisitionMember> NormalizeMembers(
        ImmutableArray<SourceAcquisitionMember> members,
        string parameterName)
    {
        if (members.IsDefaultOrEmpty || members.Any(value => value is null))
            throw new ArgumentException("An acquisition receipt requires at least one member.", parameterName);
        var ordered = members.OrderBy(value => value.SortKey, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Select(value => value.SortKey).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Acquisition member coordinates must be distinct.", parameterName);
        if (ordered.Select(value => value.ArtifactId).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Each acquired artifact must have exactly one member coordinate per receipt.", parameterName);
        return ordered;
    }
}

public sealed record SourceArtifactAcquisitionBinding
{
    [JsonConstructor]
    public SourceArtifactAcquisitionBinding(
        SourceArtifactId artifactId,
        SourceAcquisitionReceiptId acquisitionReceiptId,
        SourceNativeIdentifier memberCoordinate,
        long memberByteLength,
        ContentDigest memberDigest)
    {
        ArgumentNullException.ThrowIfNull(memberCoordinate);
        if (memberByteLength <= 0) throw new ArgumentOutOfRangeException(nameof(memberByteLength));
        if (SourceArtifactId.DeriveV1(memberDigest) != artifactId)
            throw new ArgumentException("Acquisition binding artifact identity does not match its member digest.", nameof(artifactId));
        CanonicalKnowledgeContract.RequireIdentifier(acquisitionReceiptId.Value, nameof(acquisitionReceiptId));
        ArtifactId = artifactId;
        AcquisitionReceiptId = acquisitionReceiptId;
        MemberCoordinate = memberCoordinate;
        MemberByteLength = memberByteLength;
        MemberDigest = memberDigest;
    }

    public SourceArtifactId ArtifactId { get; }
    public SourceAcquisitionReceiptId AcquisitionReceiptId { get; }
    public SourceNativeIdentifier MemberCoordinate { get; }
    public long MemberByteLength { get; }
    public ContentDigest MemberDigest { get; }
}

public sealed record CatalogCommittedBuildInput
{
    [JsonConstructor]
    public CatalogCommittedBuildInput(string repositoryRelativePath, string gitBlobObjectId)
    {
        RepositoryRelativePath = SupportedKnowledgeFormat.RequireStrictText(
            repositoryRelativePath,
            nameof(repositoryRelativePath));
        GitBlobObjectId = SupportedKnowledgeFormat.RequireStrictText(gitBlobObjectId, nameof(gitBlobObjectId));
        if (RepositoryRelativePath.Contains('\\') || RepositoryRelativePath.StartsWith("/", StringComparison.Ordinal) ||
            RepositoryRelativePath.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Build input paths must be normalized repository-relative paths.", nameof(repositoryRelativePath));
        if ((GitBlobObjectId.Length is not (40 or 64)) ||
            GitBlobObjectId.Any(value => !Uri.IsHexDigit(value) || char.IsUpper(value)))
            throw new ArgumentException("Git blob object identity must be 40 or 64 lowercase hexadecimal characters.", nameof(gitBlobObjectId));
    }

    public string RepositoryRelativePath { get; }
    public string GitBlobObjectId { get; }
}

public enum CatalogDevelopmentBuildInputState
{
    HeadTrackedClean = 0,
    TrackedIndexModified = 1,
    TrackedWorktreeModified = 2,
    TrackedIndexAndWorktreeModified = 3,
    Untracked = 4,
}

/// <summary>
/// Exact worktree bytes and Git state for one input to a non-publishable development build.
/// The digest is always over the bytes actually consumed by the build, never a Git blob or a
/// caller-provided description of those bytes.
/// </summary>
public sealed record CatalogDevelopmentBuildInput
{
    [JsonConstructor]
    public CatalogDevelopmentBuildInput(
        string repositoryRelativePath,
        ContentDigest exactContentDigest,
        CatalogDevelopmentBuildInputState state)
    {
        RepositoryRelativePath = SupportedKnowledgeFormat.RequireStrictText(
            repositoryRelativePath,
            nameof(repositoryRelativePath));
        if (RepositoryRelativePath.Contains('\\') || RepositoryRelativePath.StartsWith("/", StringComparison.Ordinal) ||
            RepositoryRelativePath.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Build input paths must be normalized repository-relative paths.", nameof(repositoryRelativePath));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        _ = exactContentDigest.HexValue ?? throw new ArgumentException(
            "Development build input digest must be initialized.", nameof(exactContentDigest));
        ExactContentDigest = exactContentDigest;
        State = state;
    }

    public string RepositoryRelativePath { get; }
    public ContentDigest ExactContentDigest { get; }
    public CatalogDevelopmentBuildInputState State { get; }
}
