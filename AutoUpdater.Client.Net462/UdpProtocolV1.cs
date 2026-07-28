using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AutoUpdater.Client.Net462
{
    internal enum UdpCommand : byte
    {
        DiscoverRequest = 0x01,
        DiscoverResponse = 0x02,
        UpdateRequest = 0x10,
        UpdateAccepted = 0x11,
        UpdateResult = 0x12,
        RollbackRequest = 0x14,
        TaskReceived = 0x15,
        DatabaseSyncRequest = 0x30,
        DatabaseSyncResult = 0x31,
        DatabaseSyncFileRequest = 0x32
    }

    internal sealed class UdpPacket
    {
        public UdpCommand Command { get; set; }
        public Guid RequestId { get; set; }
        public byte[] Payload { get; set; }
    }

    internal static class UdpProtocolV1
    {
        public const int HeaderSize = 28;
        public const int MaxPayloadSize = 60 * 1024;
        private static readonly JsonSerializerSettings JsonSettings =
            new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };

        public static byte[] Encode(
            UdpCommand command,
            Guid requestId,
            object payload)
        {
            var payloadBytes = payload == null
                ? new byte[0]
                : Encoding.UTF8.GetBytes(
                    JsonConvert.SerializeObject(payload, JsonSettings));
            if (payloadBytes.Length > MaxPayloadSize)
                throw new InvalidOperationException(
                    "协议载荷不能超过 " + MaxPayloadSize + " 字节。");

            var packet = new byte[HeaderSize + payloadBytes.Length];
            packet[0] = 0x41;
            packet[1] = 0x55;
            packet[2] = 0x01;
            packet[3] = (byte)command;
            WriteGuidNetworkOrder(requestId, packet, 4);
            WriteInt32BigEndian(packet, 20, payloadBytes.Length);
            Buffer.BlockCopy(payloadBytes, 0, packet, HeaderSize, payloadBytes.Length);
            WriteUInt32BigEndian(packet, 24, Crc32.Compute(payloadBytes));
            return packet;
        }

        public static bool TryDecode(byte[] bytes, out UdpPacket packet)
        {
            packet = null;
            if (bytes == null || bytes.Length < HeaderSize ||
                bytes[0] != 0x41 || bytes[1] != 0x55 || bytes[2] != 0x01)
                return false;
            var payloadLength = ReadInt32BigEndian(bytes, 20);
            if (payloadLength < 0 || payloadLength > MaxPayloadSize ||
                bytes.Length != HeaderSize + payloadLength)
                return false;
            var payload = new byte[payloadLength];
            Buffer.BlockCopy(bytes, HeaderSize, payload, 0, payloadLength);
            if (ReadUInt32BigEndian(bytes, 24) != Crc32.Compute(payload))
                return false;
            packet = new UdpPacket
            {
                Command = (UdpCommand)bytes[3],
                RequestId = ReadGuidNetworkOrder(bytes, 4),
                Payload = payload
            };
            return Enum.IsDefined(typeof(UdpCommand), packet.Command);
        }

        public static T DecodePayload<T>(UdpPacket packet)
            where T : class
        {
            if (packet == null || packet.Payload == null ||
                packet.Payload.Length == 0)
                return null;
            return JsonConvert.DeserializeObject<T>(
                Encoding.UTF8.GetString(packet.Payload), JsonSettings);
        }

        private static void WriteGuidNetworkOrder(
            Guid value, byte[] target, int offset)
        {
            var source = value.ToByteArray();
            target[offset] = source[3];
            target[offset + 1] = source[2];
            target[offset + 2] = source[1];
            target[offset + 3] = source[0];
            target[offset + 4] = source[5];
            target[offset + 5] = source[4];
            target[offset + 6] = source[7];
            target[offset + 7] = source[6];
            Buffer.BlockCopy(source, 8, target, offset + 8, 8);
        }

        private static Guid ReadGuidNetworkOrder(byte[] source, int offset)
        {
            var bytes = new byte[16];
            bytes[0] = source[offset + 3];
            bytes[1] = source[offset + 2];
            bytes[2] = source[offset + 1];
            bytes[3] = source[offset];
            bytes[4] = source[offset + 5];
            bytes[5] = source[offset + 4];
            bytes[6] = source[offset + 7];
            bytes[7] = source[offset + 6];
            Buffer.BlockCopy(source, offset + 8, bytes, 8, 8);
            return new Guid(bytes);
        }

        private static void WriteInt32BigEndian(
            byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value >> 24);
            target[offset + 1] = (byte)(value >> 16);
            target[offset + 2] = (byte)(value >> 8);
            target[offset + 3] = (byte)value;
        }

        private static int ReadInt32BigEndian(byte[] source, int offset)
        {
            return (source[offset] << 24) |
                   (source[offset + 1] << 16) |
                   (source[offset + 2] << 8) |
                   source[offset + 3];
        }

        private static void WriteUInt32BigEndian(
            byte[] target, int offset, uint value)
        {
            target[offset] = (byte)(value >> 24);
            target[offset + 1] = (byte)(value >> 16);
            target[offset + 2] = (byte)(value >> 8);
            target[offset + 3] = (byte)value;
        }

        private static uint ReadUInt32BigEndian(byte[] source, int offset)
        {
            return ((uint)source[offset] << 24) |
                   ((uint)source[offset + 1] << 16) |
                   ((uint)source[offset + 2] << 8) |
                   source[offset + 3];
        }
    }

    internal static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Compute(byte[] data)
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
                    value = (value & 1) != 0
                        ? 0xEDB88320u ^ (value >> 1)
                        : value >> 1;
                table[i] = value;
            }
            return table;
        }
    }

    internal sealed class DiscoverResponsePayload
    {
        public string DeviceId { get; set; }
        public string Name { get; set; }
        public string IpAddress { get; set; }
        public string Version { get; set; }
        public int ListenPort { get; set; }
    }

    internal sealed class UpdateRequestPayload
    {
        public string SenderId { get; set; }
        public string TargetDeviceId { get; set; }
        public string UpdatePath { get; set; }
    }

    internal sealed class RollbackRequestPayload
    {
        public string SenderId { get; set; }
        public string TargetDeviceId { get; set; }
        public string TargetVersion { get; set; }
    }

    internal sealed class TaskReceivedPayload
    {
        public string DeviceId { get; set; }
    }

    internal sealed class UpdateAcceptedPayload
    {
        public string DeviceId { get; set; }
        public bool Accepted { get; set; }
        public string Message { get; set; }
    }

    internal sealed class UpdateResultPayload
    {
        public string DeviceId { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public string CurrentVersion { get; set; }
    }

    internal sealed class DatabaseSyncFileRequestPayload
    {
        public string SenderId { get; set; }
        public string TargetDeviceId { get; set; }
        public string PackagePath { get; set; }
        public long PackageSize { get; set; }
        public string Sha256 { get; set; }
    }

    internal sealed class DatabaseSyncResultPayload
    {
        public string DeviceId { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public int AcceptedChanges { get; set; }
    }

    internal sealed class DatabaseSyncPackage
    {
        public int SchemaVersion { get; set; }
        public Guid PackageId { get; set; }
        public string DatabaseName { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public List<DatabaseChangePayload> Changes { get; set; }
    }

    internal sealed class DatabaseChangePayload
    {
        public Guid ChangeId { get; set; }
        public string TableName { get; set; }
        public string Operation { get; set; }
        public Dictionary<string, Newtonsoft.Json.Linq.JToken> Values { get; set; }
        public Dictionary<string, Newtonsoft.Json.Linq.JToken> KeyValues { get; set; }
    }
}
