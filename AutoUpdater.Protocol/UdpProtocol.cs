using System.Buffers.Binary;
using System.Text.Json;

namespace AutoUpdater.Protocol;

/// <summary>
/// UDP 协议 V1
/// Header: Magic(2) + Version(1) + Command(1) + RequestId(16) + PayloadLength(4) + Crc32(4)
/// 多字节数字使用网络字节序（Big Endian），Payload 使用 UTF-8 JSON。
/// </summary>
public static class UdpProtocol
{
    public const byte Version = 0x01;
    public const int HeaderSize = 28;
    public const int MaxPayloadSize = 60 * 1024;
    private const byte MagicA = 0x41; // A
    private const byte MagicU = 0x55; // U

    public static byte[] Encode<T>(UdpCommand command, Guid requestId, T? payload = default)
    {
        var payloadBytes = payload is null
            ? []
            : JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        if (payloadBytes.Length > MaxPayloadSize)
            throw new InvalidOperationException($"协议载荷不能超过 {MaxPayloadSize} 字节。");

        var packet = new byte[HeaderSize + payloadBytes.Length];
        packet[0] = MagicA;
        packet[1] = MagicU;
        packet[2] = Version;
        packet[3] = (byte)command;
        requestId.TryWriteBytes(packet.AsSpan(4, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(20, 4), payloadBytes.Length);
        payloadBytes.CopyTo(packet.AsSpan(HeaderSize));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(24, 4), Crc32.Compute(payloadBytes));
        return packet;
    }

    public static bool TryDecode(ReadOnlySpan<byte> packet, out UdpPacket result)
    {
        result = default;
        if (packet.Length < HeaderSize ||
            packet[0] != MagicA || packet[1] != MagicU ||
            packet[2] != Version)
            return false;

        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(20, 4));
        if (payloadLength < 0 || payloadLength > MaxPayloadSize ||
            packet.Length != HeaderSize + payloadLength)
            return false;

        var payload = packet.Slice(HeaderSize, payloadLength);
        var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(24, 4));
        if (Crc32.Compute(payload) != expectedCrc)
            return false;

        result = new UdpPacket(
            (UdpCommand)packet[3],
            new Guid(packet.Slice(4, 16), bigEndian: true),
            payload.ToArray());
        return Enum.IsDefined(result.Command);
    }

    public static T? DecodePayload<T>(UdpPacket packet) =>
        packet.Payload.Length == 0
            ? default
            : JsonSerializer.Deserialize<T>(packet.Payload, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public enum UdpCommand : byte
{
    DiscoverRequest = 0x01,
    DiscoverResponse = 0x02,
    UpdateRequest = 0x10,
    UpdateAccepted = 0x11,
    UpdateResult = 0x12,
    CancelTask = 0x13,
    RollbackRequest = 0x14,
    TaskReceived = 0x15,
    TaskProgress = 0x16,
    Heartbeat = 0x20,
    StatusQuery = 0x21,
    StatusResponse = 0x22,
    DatabaseSyncRequest = 0x30,
    DatabaseSyncResult = 0x31,
    DatabaseSyncFileRequest = 0x32
}

public readonly record struct UdpPacket(UdpCommand Command, Guid RequestId, byte[] Payload);

public sealed record DiscoverResponsePayload(
    string DeviceId, string Name, string IpAddress, string Version, int ListenPort = 45678);

public sealed record UpdateRequestPayload(
    string SenderId, string TargetDeviceId, string UpdatePath);

public sealed record RollbackRequestPayload(
    string SenderId, string TargetDeviceId, string? TargetVersion);

public sealed record CancelTaskPayload(
    string SenderId, string TargetDeviceId, Guid TaskRequestId);

public sealed record UpdateAcceptedPayload(string DeviceId, bool Accepted, string Message);

public sealed record TaskReceivedPayload(string DeviceId);

public sealed record TaskProgressPayload(
    string DeviceId,
    string Stage,
    int Percentage,
    string Message,
    string? Detail,
    DateTimeOffset OccurredAt);

public sealed record UpdateResultPayload(
    string DeviceId, bool Success, string Message, string? CurrentVersion = null);

public sealed record StatusResponsePayload(
    string DeviceId, string State, string CurrentVersion, string? ActiveTaskId);

public sealed record DatabaseSyncRequestPayload(
    string SenderId,
    string TargetDeviceId,
    string DatabaseName,
    IReadOnlyList<DatabaseChangePayload> Changes);

public sealed record DatabaseChangePayload(
    Guid ChangeId,
    string TableName,
    string Operation,
    IReadOnlyDictionary<string, JsonElement> Values,
    IReadOnlyDictionary<string, JsonElement> KeyValues);

public sealed record DatabaseSyncResultPayload(
    string DeviceId,
    bool Success,
    string Message,
    int AcceptedChanges);

public sealed record DatabaseSyncFileRequestPayload(
    string SenderId,
    string TargetDeviceId,
    string PackagePath,
    long PackageSize,
    string Sha256);

public sealed record DatabaseSyncPackage(
    int SchemaVersion,
    Guid PackageId,
    string DatabaseName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DatabaseChangePayload> Changes);

internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
