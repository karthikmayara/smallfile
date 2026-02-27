using System;
using System.Buffers.Binary;
using System.Text.Json;
using SmallFile.Core.Models;

namespace SmallFile.Core.Protocol;

internal static class MessageType
{
    public const byte Hello = 0x01;
    public const byte KeyExchange = 0x02;
    public const byte AuthVerify = 0x03;
    public const byte FileTreeChunk = 0x05;
}

internal sealed record HelloFrame(string Version, string DeviceName)
{
    public byte[] Serialize()
    {
        var json = JsonSerializer.Serialize(this);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    public static HelloFrame Deserialize(byte[] body)
    {
        var json = System.Text.Encoding.UTF8.GetString(body);
        return JsonSerializer.Deserialize<HelloFrame>(json) ?? throw new Exception("Invalid Hello JSON");
    }
}

internal sealed record KeyExchangeFrame(byte[] PublicKey, byte[] Salt)
{
    public byte[] Serialize()
    {
        // Pure data: [4 bytes PubKey Length] [PubKey] [32 bytes Salt]
        var buffer = new byte[4 + PublicKey.Length + 32];
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, 4), PublicKey.Length);
        Buffer.BlockCopy(PublicKey, 0, buffer, 4, PublicKey.Length);
        Buffer.BlockCopy(Salt, 0, buffer, 4 + PublicKey.Length, 32);
        return buffer;
    }

    public static KeyExchangeFrame Deserialize(byte[] body)
    {
        int pubLength = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(0, 4));
        var pub = new byte[pubLength];
        var salt = new byte[32];
        Buffer.BlockCopy(body, 4, pub, 0, pubLength);
        Buffer.BlockCopy(body, 4 + pubLength, salt, 0, 32);
        return new KeyExchangeFrame(pub, salt);
    }
}

internal sealed record AuthVerifyFrame(bool Accepted)
{
    public byte[] Serialize() => new byte[] { Accepted ? (byte)1 : (byte)0 };
    public static AuthVerifyFrame Deserialize(byte[] body) => new AuthVerifyFrame(body[0] == 1);
}

internal sealed record FileTreeFrame(List<FileEntry> Files)
{
    public byte[] Serialize()
    {
        // Serializes as a raw JSON array: [{"RelativePath":...}, {...}]
        var json = JsonSerializer.Serialize(this.Files);
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    public static FileTreeFrame Deserialize(byte[] body)
    {
        var json = System.Text.Encoding.UTF8.GetString(body);
        var files = JsonSerializer.Deserialize<List<FileEntry>>(json);
        return new FileTreeFrame(files ?? new List<FileEntry>());
    }
}