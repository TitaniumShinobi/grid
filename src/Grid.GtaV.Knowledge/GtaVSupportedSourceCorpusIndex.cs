using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>
/// Immutable, exact-artifact-bound parse index for supported GTA source families.
/// The index is a preproduction optimization only: adapters retain responsibility for
/// interpreting source semantics and for validating their exact supported coordinates.
/// </summary>
public sealed class GtaVSupportedSourceCorpusIndex
{
    private const uint Gxt2Magic = 0x47585432;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ImmutableDictionary<ArtifactKey, FrozenSourceArtifact> _artifacts;
    private readonly ConcurrentDictionary<ParseKey, Lazy<object>> _parsed = new();
    private readonly ConcurrentDictionary<ParseKey, int> _parseCounts = new();

    public GtaVSupportedSourceCorpusIndex(IEnumerable<FrozenSourceArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var builder = ImmutableDictionary.CreateBuilder<ArtifactKey, FrozenSourceArtifact>();
        foreach (var artifact in artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            var key = ArtifactKey.Create(artifact);
            if (!builder.TryAdd(key, artifact))
                throw new InvalidDataException("The corpus contains a duplicate exact artifact coordinate and format binding.");
        }
        _artifacts = builder.ToImmutable();
    }

    public ImmutableArray<GtaVIndexedArtifact> Artifacts => _artifacts
        .OrderBy(value => value.Key.CanonicalValue, StringComparer.Ordinal)
        .Select(value => new GtaVIndexedArtifact(
            value.Value.Id,
            value.Value.Digest,
            value.Value.SourceCoordinate,
            value.Value.DeclaredFormat,
            value.Value.ExactBytes.Length))
        .ToImmutableArray();

    public string GetStrictUtf8(FrozenSourceArtifact artifact, long maximumBytes)
    {
        var stored = RequireExactArtifact(artifact, maximumBytes);
        return GetOrParse(stored, "strict-utf8", () =>
        {
            try
            {
                return StrictUtf8.GetString(stored.ExactBytes.AsSpan());
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("The indexed source is not strict UTF-8.", exception);
            }
        });
    }

    public XDocument GetXmlDocument(FrozenSourceArtifact artifact, long maximumBytes)
    {
        var stored = RequireExactArtifact(artifact, maximumBytes);
        var document = GetOrParse(stored, "xml-v1", () =>
        {
            try
            {
                var text = GetStrictUtf8(stored, maximumBytes);
                using var textReader = new StringReader(text);
                using var reader = XmlReader.Create(textReader, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = maximumBytes,
                    IgnoreComments = false,
                    IgnoreProcessingInstructions = false,
                    IgnoreWhitespace = false,
                });
                return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            }
            catch (Exception exception) when (exception is XmlException or InvalidOperationException)
            {
                throw new InvalidDataException("The indexed source is not supported well-formed XML.", exception);
            }
        });
        return new XDocument(document);
    }

    public ImmutableDictionary<uint, GtaVIndexedGxt2Entry> GetGxt2(
        FrozenSourceArtifact artifact,
        long maximumBytes)
    {
        var stored = RequireExactArtifact(artifact, maximumBytes);
        return GetOrParse(stored, "gxt2-v1", () => ParseGxt2(stored));
    }

    public int GetPhysicalParseCount(FrozenSourceArtifact artifact, string representation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(representation);
        var stored = RequireExactArtifact(artifact, long.MaxValue);
        return _parseCounts.TryGetValue(new ParseKey(ArtifactKey.Create(stored), representation), out var count)
            ? count
            : 0;
    }

    private T GetOrParse<T>(FrozenSourceArtifact artifact, string representation, Func<T> parser)
        where T : notnull
    {
        var key = new ParseKey(ArtifactKey.Create(artifact), representation);
        var lazy = _parsed.GetOrAdd(key, _ => new Lazy<object>(() =>
        {
            _parseCounts.AddOrUpdate(key, 1, (_, count) => checked(count + 1));
            return parser();
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        return (T)lazy.Value;
    }

    private FrozenSourceArtifact RequireExactArtifact(FrozenSourceArtifact requested, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (maximumBytes <= 0 || requested.ExactBytes.IsDefaultOrEmpty || requested.ExactBytes.Length > maximumBytes)
            throw new InvalidDataException("The requested indexed artifact exceeds its exact parser boundary.");
        if (!_artifacts.TryGetValue(ArtifactKey.Create(requested), out var stored) ||
            stored.ObservedAtUtc != requested.ObservedAtUtc ||
            !stored.ExactBytes.AsSpan().SequenceEqual(requested.ExactBytes.AsSpan()) ||
            !EquivalentIdentity(stored.SourceCoordinate, requested.SourceCoordinate))
            throw new InvalidDataException("The requested artifact is not the exact immutable artifact indexed for this corpus.");
        return stored;
    }

    private static bool EquivalentIdentity(SourceNativeIdentifier left, SourceNativeIdentifier right) =>
        string.Equals(left.Namespace, right.Namespace, StringComparison.Ordinal) &&
        string.Equals(left.ObjectType, right.ObjectType, StringComparison.Ordinal) &&
        string.Equals(left.ExactRepresentation, right.ExactRepresentation, StringComparison.Ordinal) &&
        left.IdentityBytes.AsSpan().SequenceEqual(right.IdentityBytes.AsSpan()) &&
        string.Equals(left.ComparisonMethodId, right.ComparisonMethodId, StringComparison.Ordinal) &&
        left.ComparisonMethodVersion == right.ComparisonMethodVersion;

    private static ImmutableDictionary<uint, GtaVIndexedGxt2Entry> ParseGxt2(FrozenSourceArtifact artifact)
    {
        var bytes = artifact.ExactBytes.AsSpan();
        if (bytes.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Gxt2Magic)
            throw new InvalidDataException("The indexed language resource is not exact GXT2 data.");
        var countValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (countValue > int.MaxValue) throw new InvalidDataException("The GXT2 entry count is invalid.");
        var count = (int)countValue;
        var tableEnd = checked(8 + count * 8);
        if (tableEnd + 8 > bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes[tableEnd..]) != Gxt2Magic)
            throw new InvalidDataException("The GXT2 entry table or string-block marker is invalid.");
        var endValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(tableEnd + 4)..]);
        if (endValue > int.MaxValue || (int)endValue < tableEnd + 8 || (int)endValue > bytes.Length)
            throw new InvalidDataException("The GXT2 string block end is out of range.");

        var result = ImmutableDictionary.CreateBuilder<uint, GtaVIndexedGxt2Entry>();
        for (var index = 0; index < count; index++)
        {
            var offset = 8 + index * 8;
            var hash = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            var textOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            if (textOffsetValue > int.MaxValue) throw new InvalidDataException("A GXT2 text offset is invalid.");
            var textOffset = (int)textOffsetValue;
            if (textOffset < tableEnd + 8 || textOffset >= (int)endValue)
                throw new InvalidDataException("A GXT2 text offset is out of range.");
            var tail = bytes[textOffset..(int)endValue];
            var terminator = tail.IndexOf((byte)0);
            if (terminator < 0) throw new InvalidDataException("A GXT2 value is not NUL terminated.");
            string text;
            try { text = StrictUtf8.GetString(tail[..terminator]); }
            catch (DecoderFallbackException exception)
            { throw new InvalidDataException("A GXT2 value is not strict UTF-8.", exception); }
            var fieldLocator = $"rpf7-member:{artifact.SourceCoordinate.ExactRepresentation}#entries[0x{hash:X8}]/text";
            if (!result.TryAdd(hash, new GtaVIndexedGxt2Entry(hash, text, textOffset, terminator, fieldLocator)))
                throw new InvalidDataException("The GXT2 resource contains duplicate label hashes.");
        }
        return result.ToImmutable();
    }

    private readonly record struct ArtifactKey(
        string Id,
        string Digest,
        string Namespace,
        string ObjectType,
        string ExactRepresentation,
        string IdentityBytes,
        string ComparisonMethod,
        int ComparisonVersion,
        string FormatId,
        string FormatVersion)
    {
        public string CanonicalValue => string.Join('\n', Id, Digest, Namespace, ObjectType,
            ExactRepresentation, IdentityBytes, ComparisonMethod, ComparisonVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FormatId, FormatVersion);

        public static ArtifactKey Create(FrozenSourceArtifact artifact) => new(
            artifact.Id.Value,
            artifact.Digest.ToString(),
            artifact.SourceCoordinate.Namespace,
            artifact.SourceCoordinate.ObjectType,
            artifact.SourceCoordinate.ExactRepresentation,
            Convert.ToHexString(artifact.SourceCoordinate.IdentityBytes.AsSpan()),
            artifact.SourceCoordinate.ComparisonMethodId,
            artifact.SourceCoordinate.ComparisonMethodVersion,
            artifact.DeclaredFormat.FormatId,
            artifact.DeclaredFormat.ExactFormatVersion);
    }

    private readonly record struct ParseKey(ArtifactKey Artifact, string Representation);
}

public sealed record GtaVIndexedArtifact(
    SourceArtifactId Id,
    ContentDigest Digest,
    SourceNativeIdentifier SourceCoordinate,
    KnowledgeFormatCoordinate Format,
    int ByteLength);

public sealed record GtaVIndexedGxt2Entry(
    uint Hash,
    string Text,
    int TextOffset,
    int TextLength,
    string FieldLocator);
