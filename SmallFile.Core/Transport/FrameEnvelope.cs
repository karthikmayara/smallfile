using System;
using System.Buffers.Binary;

namespace SmallFile.Core.Transport;

internal static class FrameEnvelope
{
    public static byte[] Wrap(byte messageType, byte[] payload)
    {
        int length = 1 + payload.Length;

        byte[] buffer = new byte[4 + length];
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, 4), length);

        buffer[4] = messageType;
        Buffer.BlockCopy(payload, 0, buffer, 5, payload.Length);

        return buffer;
    }

    public static (byte MessageType, byte[] Body) Unwrap(byte[] frame)
    {
        byte msgType = frame[0];
        byte[] body = frame.AsSpan(1).ToArray();
        return (msgType, body);
    }
}