using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiCliFeishu.Bridge.Adapters.Feishu;

public static class FeishuWebSocketHeaders
{
    public const string Type = "type";
    public const string MessageId = "message_id";
    public const string Sum = "sum";
    public const string Sequence = "seq";
    public const string TraceId = "trace_id";
    public const string BusinessRuntime = "biz_rt";
}

public static class FeishuWebSocketMessageTypes
{
    public const string Event = "event";
    public const string Ping = "ping";
    public const string Pong = "pong";
}

public sealed record FeishuWireHeader(string Key, string Value);

public sealed record FeishuWireFrame(
    ulong SequenceId,
    ulong LogId,
    int Service,
    int Method,
    IReadOnlyList<FeishuWireHeader> Headers,
    string PayloadEncoding,
    string PayloadType,
    byte[] Payload,
    string LogIdNew)
{
    public string? Header(string key) =>
        Headers.FirstOrDefault(item => item.Key == key)?.Value;
}

public static class FeishuWireFrameCodec
{
    public static byte[] Encode(FeishuWireFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var writer = new ArrayBufferWriter<byte>();
        WriteUInt64(writer, 1, frame.SequenceId);
        WriteUInt64(writer, 2, frame.LogId);
        WriteInt32(writer, 3, frame.Service);
        WriteInt32(writer, 4, frame.Method);
        foreach (var header in frame.Headers)
        {
            var nested = new ArrayBufferWriter<byte>();
            WriteString(nested, 1, header.Key);
            WriteString(nested, 2, header.Value);
            WriteBytes(writer, 5, nested.WrittenSpan);
        }
        if (!string.IsNullOrEmpty(frame.PayloadEncoding))
        {
            WriteString(writer, 6, frame.PayloadEncoding);
        }
        if (!string.IsNullOrEmpty(frame.PayloadType))
        {
            WriteString(writer, 7, frame.PayloadType);
        }
        if (frame.Payload.Length > 0)
        {
            WriteBytes(writer, 8, frame.Payload);
        }
        if (!string.IsNullOrEmpty(frame.LogIdNew))
        {
            WriteString(writer, 9, frame.LogIdNew);
        }
        return writer.WrittenSpan.ToArray();
    }

    public static FeishuWireFrame Decode(ReadOnlySpan<byte> buffer)
    {
        var reader = new ProtoReader(buffer);
        ulong sequenceId = 0;
        ulong logId = 0;
        var service = 0;
        var method = 0;
        var headers = new List<FeishuWireHeader>();
        var payloadEncoding = "";
        var payloadType = "";
        var payload = Array.Empty<byte>();
        var logIdNew = "";
        while (reader.TryTag(out var field, out var wireType))
        {
            switch (field)
            {
                case 1 when wireType == 0:
                    sequenceId = reader.Varint();
                    break;
                case 2 when wireType == 0:
                    logId = reader.Varint();
                    break;
                case 3 when wireType == 0:
                    service = unchecked((int)reader.Varint());
                    break;
                case 4 when wireType == 0:
                    method = unchecked((int)reader.Varint());
                    break;
                case 5 when wireType == 2:
                    headers.Add(DecodeHeader(reader.Bytes()));
                    break;
                case 6 when wireType == 2:
                    payloadEncoding = reader.String();
                    break;
                case 7 when wireType == 2:
                    payloadType = reader.String();
                    break;
                case 8 when wireType == 2:
                    payload = reader.Bytes().ToArray();
                    break;
                case 9 when wireType == 2:
                    logIdNew = reader.String();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }
        return new(
            sequenceId,
            logId,
            service,
            method,
            headers,
            payloadEncoding,
            payloadType,
            payload,
            logIdNew);
    }

    private static FeishuWireHeader DecodeHeader(ReadOnlySpan<byte> buffer)
    {
        var reader = new ProtoReader(buffer);
        string? key = null;
        string? value = null;
        while (reader.TryTag(out var field, out var wireType))
        {
            if (field == 1 && wireType == 2)
            {
                key = reader.String();
            }
            else if (field == 2 && wireType == 2)
            {
                value = reader.String();
            }
            else
            {
                reader.Skip(wireType);
            }
        }
        return key is not null && value is not null
            ? new(key, value)
            : throw new InvalidDataException("飞书 WebSocket Header 缺少 key 或 value。 ");
    }

    private static void WriteUInt64(IBufferWriter<byte> writer, int field, ulong value)
    {
        WriteVarint(writer, (ulong)(field << 3));
        WriteVarint(writer, value);
    }

    private static void WriteInt32(IBufferWriter<byte> writer, int field, int value) =>
        WriteUInt64(writer, field, unchecked((ulong)(long)value));

    private static void WriteString(IBufferWriter<byte> writer, int field, string value) =>
        WriteBytes(writer, field, Encoding.UTF8.GetBytes(value));

    private static void WriteBytes(
        IBufferWriter<byte> writer,
        int field,
        ReadOnlySpan<byte> value)
    {
        WriteVarint(writer, (ulong)((field << 3) | 2));
        WriteVarint(writer, (ulong)value.Length);
        writer.Write(value);
    }

    private static void WriteVarint(IBufferWriter<byte> writer, ulong value)
    {
        var span = writer.GetSpan(10);
        var length = 0;
        do
        {
            var current = (byte)(value & 0x7f);
            value >>= 7;
            span[length++] = value == 0 ? current : (byte)(current | 0x80);
        }
        while (value != 0);
        writer.Advance(length);
    }

    private ref struct ProtoReader(ReadOnlySpan<byte> buffer)
    {
        private readonly ReadOnlySpan<byte> source = buffer;
        private int position;

        public bool TryTag(out int field, out int wireType)
        {
            if (position >= source.Length)
            {
                field = 0;
                wireType = 0;
                return false;
            }
            var tag = Varint();
            field = checked((int)(tag >> 3));
            wireType = (int)(tag & 7);
            if (field == 0)
            {
                throw new InvalidDataException("Protobuf 字段编号不能为 0。 ");
            }
            return true;
        }

        public ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (position >= source.Length)
                {
                    throw new EndOfStreamException("Protobuf varint 被截断。 ");
                }
                var current = source[position++];
                value |= (ulong)(current & 0x7f) << shift;
                if ((current & 0x80) == 0)
                {
                    return value;
                }
            }
            throw new InvalidDataException("Protobuf varint 超出 64 位。 ");
        }

        public ReadOnlySpan<byte> Bytes()
        {
            var length = checked((int)Varint());
            if (length < 0 || position + length > source.Length)
            {
                throw new EndOfStreamException("Protobuf length-delimited 字段被截断。 ");
            }
            var value = source.Slice(position, length);
            position += length;
            return value;
        }

        public string String() => Encoding.UTF8.GetString(Bytes());

        public void Skip(int wireType)
        {
            switch (wireType)
            {
                case 0:
                    _ = Varint();
                    break;
                case 1:
                    Advance(8);
                    break;
                case 2:
                    _ = Bytes();
                    break;
                case 5:
                    Advance(4);
                    break;
                default:
                    throw new InvalidDataException($"不支持的 Protobuf wire type：{wireType}。 ");
            }
        }

        private void Advance(int count)
        {
            if (position + count > source.Length)
            {
                throw new EndOfStreamException("Protobuf 字段被截断。 ");
            }
            position += count;
        }
    }
}

public sealed record FeishuMergedWebSocketEvent(
    FeishuWireFrame ResponseFrame,
    string MessageId,
    string TraceId,
    byte[] Payload);

public sealed class FeishuWebSocketFragmentAssembler(int capacity = 1_024)
{
    private readonly int boundedCapacity = Math.Max(1, capacity);
    private readonly Dictionary<string, PendingFragments> pending = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();

    public FeishuMergedWebSocketEvent? Add(FeishuWireFrame frame)
    {
        if (frame.Method != 1 || frame.Header(FeishuWebSocketHeaders.Type) != FeishuWebSocketMessageTypes.Event)
        {
            return null;
        }
        var messageId = frame.Header(FeishuWebSocketHeaders.MessageId);
        var traceId = frame.Header(FeishuWebSocketHeaders.TraceId) ?? messageId;
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(traceId))
        {
            throw new InvalidDataException("飞书事件帧缺少 message_id 或 trace_id。 ");
        }
        var sum = PositiveInt(frame.Header(FeishuWebSocketHeaders.Sum), 1);
        var sequence = NonNegativeInt(frame.Header(FeishuWebSocketHeaders.Sequence), 0);
        if (sequence >= sum)
        {
            throw new InvalidDataException("飞书事件分片序号超出总数。 ");
        }
        if (!pending.TryGetValue(messageId, out var state))
        {
            state = new(sum, traceId);
            pending.Add(messageId, state);
            order.Enqueue(messageId);
            Prune();
        }
        if (state.Sum != sum || state.TraceId != traceId)
        {
            pending.Remove(messageId);
            throw new InvalidDataException("同一飞书 message_id 的分片元数据不一致。 ");
        }
        state.Chunks.TryAdd(sequence, frame.Payload);
        state.ResponseFrame = frame;
        if (state.Chunks.Count != sum)
        {
            return null;
        }
        using var stream = new MemoryStream();
        for (var index = 0; index < sum; index++)
        {
            if (!state.Chunks.TryGetValue(index, out var chunk))
            {
                return null;
            }
            stream.Write(chunk);
        }
        pending.Remove(messageId);
        return new(state.ResponseFrame, messageId, traceId, stream.ToArray());
    }

    private void Prune()
    {
        while (pending.Count > boundedCapacity && order.Count > 0)
        {
            pending.Remove(order.Dequeue());
        }
        while (order.Count > boundedCapacity * 2)
        {
            order.Dequeue();
        }
    }

    private static int PositiveInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static int NonNegativeInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;

    private sealed class PendingFragments(int sum, string traceId)
    {
        public int Sum { get; } = sum;

        public string TraceId { get; } = traceId;

        public Dictionary<int, byte[]> Chunks { get; } = [];

        public FeishuWireFrame ResponseFrame { get; set; } = null!;
    }
}

public static class FeishuWebSocketEnvelopeParser
{
    public static (string EventId, string EventType, JsonElement Payload) Parse(
        FeishuMergedWebSocketEvent message)
    {
        using var document = JsonDocument.Parse(message.Payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("飞书事件 payload 不是 JSON 对象。 ");
        }
        if (root.TryGetProperty("schema", out _))
        {
            var header = RequiredObject(root, "header");
            var payload = RequiredObject(root, "event");
            return (
                Text(header, "event_id") ?? message.MessageId,
                Text(header, "event_type") ??
                    throw new InvalidDataException("飞书 v2 事件缺少 event_type。 "),
                payload.Clone());
        }
        var eventNode = RequiredObject(root, "event");
        return (
            Text(root, "uuid") ?? message.MessageId,
            Text(eventNode, "type") ??
                throw new InvalidDataException("飞书 v1 事件缺少 type。 "),
            eventNode.Clone());
    }

    public static FeishuWireFrame Response(
        FeishuMergedWebSocketEvent message,
        FeishuCallbackResult? callback,
        int statusCode,
        long elapsedMilliseconds)
    {
        var body = new JsonObject { ["code"] = statusCode };
        if (callback is not null)
        {
            var callbackJson = new JsonObject
            {
                ["toast"] = new JsonObject
                {
                    ["type"] = callback.ToastType,
                    ["content"] = callback.ToastContent,
                },
            };
            if (callback.Card is not null)
            {
                callbackJson["card"] = callback.Card.Content.DeepClone();
            }
            body["data"] = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(callbackJson.ToJsonString()));
        }
        return message.ResponseFrame with
        {
            Headers =
            [
                .. message.ResponseFrame.Headers,
                new(FeishuWebSocketHeaders.BusinessRuntime,
                    Math.Max(0, elapsedMilliseconds).ToString()),
            ],
            Payload = Encoding.UTF8.GetBytes(body.ToJsonString()),
        };
    }

    private static JsonElement RequiredObject(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }
        throw new InvalidDataException($"飞书事件缺少对象字段 {property}。 ");
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
