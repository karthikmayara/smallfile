using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json;
using SmallFile.Core.Models;

namespace SmallFile.Core.Protocol;

internal static class MessageType
{
    public const byte Hello = 0x01;
    public const byte KeyExchange = 0x02;
    public const byte AuthVerify = 0x03;
    public const byte RequestTree = 0x04;
    public const byte FileTreeChunk = 0x05;
    
    // Phase 1 Transfer Messages
    public const byte FileRequest = 0x06;
    public const byte FileChunk = 0x07;
    public const byte FileComplete = 0x08;
}

internal sealed record HelloFrame(string Version, string DeviceName)
{
    public byte[] Serialize() => System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
    public static HelloFrame Deserialize(byte[] body) => 
        JsonSerializer.Deserialize<HelloFrame>(System.Text.Encoding.UTF8.GetString(body)) ?? throw new Exception("Invalid Hello JSON");
}

internal sealed record KeyExchangeFrame(byte[] PublicKey, byte[] Salt)
{
    public byte[] Serialize()
    {
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

internal sealed record RequestTreeFrame()
{
    public byte[] Serialize() => Array.Empty<byte>();
    public static RequestTreeFrame Deserialize(byte[] _) => new RequestTreeFrame();
}

internal sealed record FileTreeFrame(List<FileEntry> Files)
{
    public byte[] Serialize() => System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this.Files));
    public static FileTreeFrame Deserialize(byte[] body)
    {
        var files = JsonSerializer.Deserialize<List<FileEntry>>(System.Text.Encoding.UTF8.GetString(body));
        return new FileTreeFrame(files ?? new List<FileEntry>());
    }
}

internal sealed record FileRequestFrame(string RelativePath)
{
    public byte[] Serialize() => System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
    public static FileRequestFrame Deserialize(byte[] body) => 
        JsonSerializer.Deserialize<FileRequestFrame>(System.Text.Encoding.UTF8.GetString(body)) ?? throw new Exception("Invalid FileRequest JSON");
}

internal sealed record FileCompleteFrame(string RelativePath)
{
    public byte[] Serialize() => System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
    public static FileCompleteFrame Deserialize(byte[] body) => 
        JsonSerializer.Deserialize<FileCompleteFrame>(System.Text.Encoding.UTF8.GetString(body)) ?? throw new Exception("Invalid FileComplete JSON");
}

internal sealed record FileChunkFrame(string RelativePath, long Offset, byte[] Data)
{
    public byte[] Serialize()
    {
        byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(RelativePath);
        if (pathBytes.Length > ushort.MaxValue) throw new Exception("Path too long");

        byte[] buffer = new byte[2 + pathBytes.Length + 8 + Data.Length];
        
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), (ushort)pathBytes.Length);
        Buffer.BlockCopy(pathBytes, 0, buffer, 2, pathBytes.Length);
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(2 + pathBytes.Length, 8), Offset);
        Buffer.BlockCopy(Data, 0, buffer, 2 + pathBytes.Length + 8, Data.Length);

        return buffer;
    }

    public static FileChunkFrame Deserialize(byte[] body)
    {
        if (body.Length < 10) 
            throw new Exception("Malformed FileChunk: Payload too small.");

        ushort pathLen = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(0, 2));
        
        if (body.Length < 2 + pathLen + 8) 
            throw new Exception("Malformed FileChunk: Path length exceeds payload bounds.");

        string path = System.Text.Encoding.UTF8.GetString(body, 2, pathLen);
        long offset = BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(2 + pathLen, 8));

        if (offset < 0) 
            throw new Exception("Malformed FileChunk: Negative offset.");

        int dataLen = body.Length - (2 + pathLen + 8);
        byte[] data = new byte[dataLen];
        
        if (dataLen > 0)
        {
            Buffer.BlockCopy(body, 2 + pathLen + 8, data, 0, dataLen);
        }

        return new FileChunkFrame(path, offset, data);
    }
}