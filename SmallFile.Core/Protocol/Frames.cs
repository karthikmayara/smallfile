namespace SmallFile.Core.Protocol;

internal sealed record KeyExchangeFrame(byte[] PublicKey, byte[] Salt)
{
    public byte[] Serialize()
    {
        // [1 byte ID] [4 bytes PubKey Length] [PubKey] [32 bytes Salt]
        var buffer = new byte[1 + 4 + PublicKey.Length + 32];
        buffer[0] = MessageType.KeyExchange;
        
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1, 4), PublicKey.Length);
        Buffer.BlockCopy(PublicKey, 0, buffer, 5, PublicKey.Length);
        Buffer.BlockCopy(Salt, 0, buffer, 5 + PublicKey.Length, 32);

        return buffer;
    }

    public static KeyExchangeFrame Deserialize(byte[] payload)
    {
        int pubLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));
        
        var pub = new byte[pubLength];
        var salt = new byte[32];

        Buffer.BlockCopy(payload, 4, pub, 0, pubLength);
        Buffer.BlockCopy(payload, 4 + pubLength, salt, 0, 32);

        return new KeyExchangeFrame(pub, salt);
    }
}