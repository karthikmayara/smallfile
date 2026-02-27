using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace SmallFile.Core.Transport;

internal sealed class FrameParser
{
    private const int MaxFrameSize = 10 * 1024 * 1024; // 10MB safety cap

    private byte[] _buffer = new byte[64 * 1024];
    private int _bufferedBytes = 0;

    public IEnumerable<byte[]> Feed(ReadOnlySpan<byte> incoming)
    {
        EnsureCapacity(_bufferedBytes + incoming.Length);

        incoming.CopyTo(_buffer.AsSpan(_bufferedBytes));
        _bufferedBytes += incoming.Length;

        var frames = new List<byte[]>();

        while (true)
        {
            if (_bufferedBytes < 4)
                break;

            int length = BinaryPrimitives.ReadInt32BigEndian(_buffer.AsSpan(0, 4));

            if (length <= 0 || length > MaxFrameSize)
                throw new InvalidOperationException($"Invalid frame length: {length}");

            if (_bufferedBytes < 4 + length)
                break;

            byte[] frame = new byte[length];
            Buffer.BlockCopy(_buffer, 4, frame, 0, length);
            frames.Add(frame);

            int remaining = _bufferedBytes - (4 + length);

            if (remaining > 0)
                Buffer.BlockCopy(_buffer, 4 + length, _buffer, 0, remaining);

            _bufferedBytes = remaining;
        }

        return frames;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
            return;

        int newSize = _buffer.Length * 2;
        while (newSize < required)
            newSize *= 2;

        Array.Resize(ref _buffer, newSize);
    }
}