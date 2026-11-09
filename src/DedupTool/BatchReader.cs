using System.Text.Json;

namespace DedupTool;

/// <summary>
/// Streams the documents of a batch file one at a time, so peak memory is one read
/// buffer plus the document being parsed instead of the whole file (SPEC §6.2: ~2M docs).
///
/// Accepts the same shape as <see cref="Batch"/>: <c>{ "batchId": ..., "documents": [ ... ] }</c>,
/// properties in any order and matched case-insensitively; unknown properties are skipped.
/// <see cref="BatchId"/> is available once the enumeration has finished.
/// </summary>
public sealed class BatchReader
{
    private enum Stage { Start, Root, Documents, Done }

    private readonly JsonSerializerOptions _options;
    private readonly int _initialBufferSize;
    private Stage _stage;

    public BatchReader(JsonSerializerOptions? options = null, int initialBufferSize = 64 * 1024)
    {
        if (initialBufferSize < 1) throw new ArgumentOutOfRangeException(nameof(initialBufferSize));
        _options = options ?? new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        _initialBufferSize = initialBufferSize;
    }

    public string BatchId { get; private set; } = "";

    public IEnumerable<Document> ReadDocuments(Stream stream)
    {
        _stage = Stage.Start;
        BatchId = "";

        var buffer = new byte[_initialBufferSize];
        var length = 0;
        var isFinal = false;
        var state = new JsonReaderState();
        var parsed = new List<Document>();

        while (_stage != Stage.Done)
        {
            if (!isFinal)
            {
                // A single token or document larger than the buffer: grow it.
                if (length == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
                var read = stream.Read(buffer, length, buffer.Length - length);
                if (read == 0) isFinal = true;
                else length += read;
            }

            var consumed = Parse(buffer.AsSpan(0, length), isFinal, ref state, parsed);

            foreach (var d in parsed) yield return d;
            parsed.Clear();

            Buffer.BlockCopy(buffer, consumed, buffer, 0, length - consumed);
            length -= consumed;

            if (isFinal && _stage != Stage.Done)
                throw new InvalidDataException(_stage == Stage.Start
                    ? "Batch file was empty."
                    : "Batch file is truncated.");
        }
    }

    /// <summary>
    /// Advances over every complete unit in <paramref name="data"/>. A unit (a structural
    /// token, a property with its whole value, or a whole document) is committed only when
    /// it is entirely in the buffer; otherwise the reader stops and waits for more bytes.
    /// </summary>
    private int Parse(ReadOnlySpan<byte> data, bool isFinal, ref JsonReaderState state, List<Document> output)
    {
        var reader = new Utf8JsonReader(data, isFinal, state);
        while (_stage != Stage.Done)
        {
            var attempt = reader;
            if (!TryStep(ref attempt, output)) break;
            reader = attempt;
        }
        state = reader.CurrentState;
        return checked((int)reader.BytesConsumed);
    }

    private bool TryStep(ref Utf8JsonReader r, List<Document> output)
    {
        if (!r.Read()) return false;

        switch (_stage)
        {
            case Stage.Start:
                if (r.TokenType == JsonTokenType.Null) throw new InvalidDataException("Batch file was empty.");
                if (r.TokenType != JsonTokenType.StartObject)
                    throw new InvalidDataException("Batch file must be a JSON object.");
                _stage = Stage.Root;
                return true;

            case Stage.Root:
                if (r.TokenType == JsonTokenType.EndObject)
                {
                    _stage = Stage.Done;
                    return true;
                }

                var name = r.GetString();
                if (!r.Read()) return false;

                if (string.Equals(name, "documents", StringComparison.OrdinalIgnoreCase))
                {
                    if (r.TokenType == JsonTokenType.Null) return true;
                    if (r.TokenType != JsonTokenType.StartArray)
                        throw new InvalidDataException("'documents' must be an array.");
                    _stage = Stage.Documents;
                    return true;
                }

                if (string.Equals(name, "batchId", StringComparison.OrdinalIgnoreCase))
                {
                    BatchId = r.TokenType == JsonTokenType.Null ? "" : r.GetString() ?? "";
                    return true;
                }

                return r.TrySkip();

            case Stage.Documents:
                if (r.TokenType == JsonTokenType.EndArray)
                {
                    _stage = Stage.Root;
                    return true;
                }
                if (r.TokenType != JsonTokenType.StartObject)
                    throw new InvalidDataException("Every entry in 'documents' must be an object.");

                // Only deserialize once the whole object is buffered.
                var probe = r;
                if (!probe.TrySkip()) return false;

                output.Add(JsonSerializer.Deserialize<Document>(ref r, _options)!);
                return true;

            default:
                return false;
        }
    }
}
