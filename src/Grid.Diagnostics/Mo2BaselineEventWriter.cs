using System.Text.Json;
using System.Text.Json.Serialization;
using Grid.Mo2.Models;

namespace Grid.Diagnostics;

internal sealed class Mo2BaselineEventWriter : IAsyncDisposable
{
    private const int FlushIntervalRecords = 4096;
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Stream stream;
    private readonly bool ndjson;
    private readonly Utf8JsonWriter? json;
    private long sequence;
    private int recordsSinceFlush;
    private bool completed;

    public Mo2BaselineEventWriter(Stream stream, string format)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        ndjson = format.Equals("ndjson", StringComparison.OrdinalIgnoreCase);
        if (!ndjson)
        {
            json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 1);
            json.WriteString("stream", "grid.mo2.baseline.v1");
            json.WriteStartArray("records");
            json.Flush();
        }
    }

    public async ValueTask WriteAsync(Mo2BaselineEvent item, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(completed, this);
        var envelope = new EventEnvelope(1, ++sequence, item.RecordType, item.Payload);
        if (ndjson)
        {
            await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            recordsSinceFlush++;
            if (recordsSinceFlush >= FlushIntervalRecords)
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                recordsSinceFlush = 0;
            }
            return;
        }

        JsonSerializer.Serialize(json!, envelope, JsonOptions);
        recordsSinceFlush++;
        if (recordsSinceFlush >= FlushIntervalRecords)
        {
            json!.Flush();
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            recordsSinceFlush = 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (completed)
        {
            return;
        }

        completed = true;
        if (json is not null)
        {
            json.WriteEndArray();
            json.WriteEndObject();
            json.Flush();
        }
        await stream.FlushAsync().ConfigureAwait(false);
        json?.Dispose();
    }

    private sealed record EventEnvelope(int SchemaVersion, long Sequence, string RecordType, object Payload);
}
