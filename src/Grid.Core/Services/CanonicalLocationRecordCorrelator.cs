using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Deterministic correlation from source-owned native identity plus primary terminology to one canonical Location record.
/// </summary>
public static class CanonicalLocationRecordCorrelator
{
    public static KnowledgeRecordId ResolveUniqueLocation(
        CanonicalCatalogPayload payload,
        LocationRecordCorrelationKey key)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(key);
        var records = payload.KnowledgeRecords.Where(record =>
            record.Kind == KnowledgeKind.Location &&
            record.NativeIdentity.Namespace == key.NativeNamespace &&
            record.NativeIdentity.ObjectType == key.NativeObjectType &&
            string.Equals(record.NativeIdentity.ExactRepresentation, key.NativeExactRepresentation, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (records.Length == 0)
            throw new InvalidDataException("Location correlation found no native identity match: " + key.PrimaryName);
        if (records.Length > 1)
            throw new InvalidDataException("Location correlation is ambiguous for native identity: " + key.PrimaryName);

        var record = records[0];
        var labels = payload.TerminologyAssertions
            .Where(value => value.KnowledgeRecordId == record.Id &&
                value.Role == TerminologyAssertionRole.PrimaryName &&
                string.Equals(value.LanguageTag, key.LanguageTag, StringComparison.Ordinal))
            .Select(value => value.VerbatimValue)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (labels.Length != 1 || !string.Equals(labels[0], key.PrimaryName, StringComparison.Ordinal))
            throw new InvalidDataException("Location correlation primary terminology does not match exactly: " + key.PrimaryName);
        return record.Id;
    }
}
