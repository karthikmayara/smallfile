using System.Buffers.Binary;
using System.Text.Json;

namespace SmallFile.Core.Protocol;

internal static class MessageType
{
    public const byte Hello = 0x01;
    public const byte KeyExchange = 0x02;
    public const byte AuthVerify = 0x03;
}

internal sealed record HelloFrame(string Version, string DeviceName)
{
    public byte[] Serialize()
    {
        var json = JsonSerializer.Serialize(this);
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var buffer = new byte[1 + payload.Length];
        buffer[0] = MessageType.Hello;
        Buffer.BlockCopy(payload, 0, buffer, 1, payload.Length);
        return buffer;
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
        // [1 byte Type] [4 bytes PubKey Length] [PubKey] [32 bytes Salt]
        var buffer = new byte[1 + 4 + PublicKey.Length + 32];
        buffer[0] = MessageType.KeyExchange;
        
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1, 4), PublicKey.Length);
        Buffer.BlockCopy(PublicKey, 0, buffer, 5, PublicKey.Length);
        Buffer.BlockCopy(Salt, 0, buffer, 5 + PublicKey.Length, 32);

        return buffer;
    }

    public static KeyExchangeFrame Deserialize(byte[] body)
    {
        // Body starts after the MessageType byte
        if (body.Length < 4 + 32) 
            throw new Exception("Malformed KeyExchange: Payload too small.");

        int pubLength = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(0, 4));
        
        // Defensive check against memory abuse
        if (pubLength <= 0 || pubLength > 512) 
            throw new Exception($"Invalid public key length: {pubLength}");

        if (body.Length != 4 + pubLength + 32)
            throw new Exception("Malformed KeyExchange: Frame size mismatch.");

        var pub = new byte[pubLength];
        var salt = new byte[32];

        Buffer.BlockCopy(body, 4, pub, 0, pubLength);
        Buffer.BlockCopy(body, 4 + pubLength, salt, 0, 32);

        return new KeyExchangeFrame(pub, salt);
    }
}

internal sealed record AuthVerifyFrame(bool Accepted)
{
    public byte[] Serialize()
    {
        return new byte[] { MessageType.AuthVerify, Accepted ? (byte)1 : (byte)0 };
    }

    public static AuthVerifyFrame Deserialize(byte[] body)
    {
        return new AuthVerifyFrame(body[0] == 1);
    }
}