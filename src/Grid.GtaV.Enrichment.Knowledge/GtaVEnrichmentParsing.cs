using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Grid.Core.Models;

namespace Grid.GtaV.Enrichment.Knowledge;

internal static class GtaVEnrichmentParsing
{
    internal const string ResourceCoordinateNamespace = "rockstar.gta-v.enhanced.resource-coordinate";
    internal const string ResourceCoordinateComparison = "grid.gta-v.resource-coordinate.exact-utf8";
    internal const uint Gxt2Magic = 0x47585432;
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static ImmutableArray<PopulationZone> ParsePopulationZones(FrozenSourceArtifact artifact)
    {
        var text = DecodeStrictUtf8(artifact.ExactBytes.AsSpan(), GtaVPopulationZonesKnowledgeAdapter.MaximumArtifactBytes);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length < 3 || lines[0] != "zone")
            throw new InvalidDataException("A population-zone resource must start with the exact zone section header.");

        var result = ImmutableArray.CreateBuilder<PopulationZone>();
        var sawEnd = false;
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                if (!sawEnd) throw new InvalidDataException("Blank lines are not valid inside the zone section.");
                continue;
            }
            if (line == "end")
            {
                if (sawEnd) throw new InvalidDataException("The zone section has more than one end marker.");
                sawEnd = true;
                continue;
            }
            if (sawEnd) throw new InvalidDataException("No content is permitted after the zone section end marker.");

            var fields = line.Split(',');
            if (fields.Length != 9)
                throw new InvalidDataException("Every population-zone row must contain exactly nine comma-delimited fields.");
            for (var fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
                fields[fieldIndex] = fields[fieldIndex].Trim(' ', '\t');
            if (fields.Any(value => value.Length == 0))
                throw new InvalidDataException("Population-zone fields cannot be empty.");
            for (var numberIndex = 1; numberIndex <= 6; numberIndex++)
            {
                if (!double.TryParse(fields[numberIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                    !double.IsFinite(number))
                    throw new InvalidDataException("Population-zone bounds must be finite invariant numbers.");
            }
            if (!int.TryParse(fields[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException("The population-zone flags field must be an invariant integer.");
            ValidateExactText(fields[0], "population-zone row identity");
            ValidateExactText(fields[7], "population-zone NameLabel");
            var row = index;
            result.Add(new PopulationZone(
                fields[0],
                fields[7],
                Locator(artifact.SourceCoordinate.ExactRepresentation, $"zone/row[{row}]"),
                Locator(artifact.SourceCoordinate.ExactRepresentation, $"zone/row[{row}]/NameLabel")));
        }
        if (!sawEnd || result.Count == 0)
            throw new InvalidDataException("A population-zone resource requires one non-empty terminated zone section.");
        return result.ToImmutable();
    }

    internal static ImmutableDictionary<uint, Gxt2Entry> ParseGxt2(FrozenSourceArtifact artifact, long maximumBytes)
    {
        var bytes = artifact.ExactBytes.AsSpan();
        if (bytes.Length > maximumBytes || bytes.Length < 16 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Gxt2Magic)
            throw new InvalidDataException("The frozen language resource is not bounded exact GXT2 data.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var tableSize = checked(8L + count * 8L);
        if (tableSize > int.MaxValue || tableSize + 8 > bytes.Length)
            throw new InvalidDataException("The GXT2 entry table is truncated.");
        var tableEnd = (int)tableSize;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[tableEnd..]) != Gxt2Magic)
            throw new InvalidDataException("The GXT2 string block marker is invalid.");
        var endValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(tableEnd + 4)..]);
        if (endValue > int.MaxValue) throw new InvalidDataException("The GXT2 string block end is invalid.");
        var end = (int)endValue;
        if (end < tableEnd + 8 || end > bytes.Length)
            throw new InvalidDataException("The GXT2 string block end is out of range.");

        var result = ImmutableDictionary.CreateBuilder<uint, Gxt2Entry>();
        for (var index = 0; index < count; index++)
        {
            var entryOffset = checked(8 + index * 8);
            var hash = BinaryPrimitives.ReadUInt32LittleEndian(bytes[entryOffset..]);
            var offsetValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(entryOffset + 4)..]);
            if (offsetValue > int.MaxValue) throw new InvalidDataException("A GXT2 text offset is invalid.");
            var offset = (int)offsetValue;
            if (offset < tableEnd + 8 || offset >= end)
                throw new InvalidDataException("A GXT2 text offset is out of range.");
            var terminator = bytes[offset..end].IndexOf((byte)0);
            if (terminator < 0) throw new InvalidDataException("A GXT2 value is not NUL terminated.");
            string text;
            try { text = StrictUtf8.GetString(bytes.Slice(offset, terminator)); }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("A GXT2 value is not strict UTF-8.", exception);
            }
            var field = Locator(artifact.SourceCoordinate.ExactRepresentation, $"entries[0x{hash:X8}]/text");
            if (!result.TryAdd(hash, new Gxt2Entry(hash, text, offset, terminator, field)))
                throw new InvalidDataException("The GXT2 resource contains duplicate label hashes.");
        }
        return result.ToImmutable();
    }

    internal static uint ComputeJoaat32(string value)
    {
        if (value.Length == 0 || value.Any(character => character > 0x7f || char.IsControl(character)))
            throw new InvalidDataException("The v1 GTA label-key mapping accepts exact printable ASCII keys only.");
        uint hash = 0;
        foreach (var character in value)
        {
            var lowered = character is >= 'A' and <= 'Z' ? (byte)(character + 32) : (byte)character;
            hash += lowered;
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }

    internal static SourceNativeIdentifier GxtHashIdentity(uint hash)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, hash);
        return new SourceNativeIdentifier(
            "rockstar.gta-v.gxt2", "LabelHash", $"0x{hash:X8}", bytes.ToImmutableArray(),
            "rockstar.gta-v.joaat32-little-endian", 1);
    }

    internal static string DecodeStrictUtf8(ReadOnlySpan<byte> bytes, long maximumBytes)
    {
        if (bytes.IsEmpty || bytes.Length > maximumBytes)
            throw new InvalidDataException("The frozen source artifact has an invalid size.");
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The frozen source is not well-formed UTF-8.", exception);
        }
    }

    internal static void ValidateArtifact(
        FrozenSourceArtifact artifact,
        SourceNativeIdentifier expectedCoordinate,
        KnowledgeFormatCoordinate format)
    {
        if (!string.Equals(artifact.SourceCoordinate.Namespace, expectedCoordinate.Namespace, StringComparison.Ordinal) ||
            !string.Equals(artifact.SourceCoordinate.ObjectType, expectedCoordinate.ObjectType, StringComparison.Ordinal) ||
            !string.Equals(artifact.SourceCoordinate.ExactRepresentation, expectedCoordinate.ExactRepresentation, StringComparison.Ordinal) ||
            !artifact.SourceCoordinate.IdentityBytes.AsSpan().SequenceEqual(expectedCoordinate.IdentityBytes.AsSpan()) ||
            !string.Equals(artifact.SourceCoordinate.ComparisonMethodId, expectedCoordinate.ComparisonMethodId, StringComparison.Ordinal) ||
            artifact.SourceCoordinate.ComparisonMethodVersion != expectedCoordinate.ComparisonMethodVersion ||
            artifact.DeclaredFormat != format ||
            SourceArtifactId.DeriveV1(artifact.Digest) != artifact.Id ||
            ContentDigest.ComputeSha256(artifact.ExactBytes.AsSpan()) != artifact.Digest)
            throw new InvalidDataException("The frozen artifact has the wrong coordinate, format, or digest.");
    }

    internal static SourceNativeIdentifier ResourceCoordinate(string objectType, string exactCoordinate) =>
        SourceNativeIdentifier.FromExactUtf8(
            ResourceCoordinateNamespace, objectType, exactCoordinate, ResourceCoordinateComparison, 1);

    internal static string Locator(string coordinate, string field) =>
        $"rpf7-member:{coordinate}#{field}";

    internal static void ValidateExactText(string value, string description)
    {
        if (value.Length == 0) throw new InvalidDataException($"The {description} cannot be empty.");
        try { _ = StrictUtf8.GetBytes(value); }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException($"The {description} is malformed UTF-16.", exception);
        }
    }

    internal sealed record PopulationZone(
        string RowIdentity,
        string NameLabel,
        string RowLocator,
        string LabelFieldLocator);

    internal sealed record Gxt2Entry(
        uint Hash,
        string Text,
        int TextOffset,
        int TextLength,
        string FieldLocator);
}
