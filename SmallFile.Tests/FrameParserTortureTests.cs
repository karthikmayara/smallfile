using SmallFile.Core.Transport;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SmallFile.Tests;

public class FrameParserTortureTests
{
    [Fact]
    public void FrameParser_Should_Reassemble_Fragmented_Frames()
    {
        var parser = new FrameParser();
        var rng = new Random(1337);

        const int frameCount = 50;
        var originalFrames = new List<byte[]>();

        // Generate random frames up to 100KB each
        for (int i = 0; i < frameCount; i++)
        {
            int payloadSize = rng.Next(1, 100_000);
            byte[] payload = new byte[payloadSize];
            rng.NextBytes(payload);

            byte[] wrapped = FrameEnvelope.Wrap(0x42, payload);
            originalFrames.Add(wrapped);
        }

        // Concatenate all frames into one giant stream
        byte[] fullStream = originalFrames.SelectMany(f => f).ToArray();

        // Fragment randomly (1 to 1400 bytes per chunk to simulate TCP MTU constraints)
        var reconstructedFrames = new List<byte[]>();
        int offset = 0;

        while (offset < fullStream.Length)
        {
            int chunkSize = rng.Next(1, 1400);
            chunkSize = Math.Min(chunkSize, fullStream.Length - offset);

            var chunk = fullStream.AsSpan(offset, chunkSize);
            offset += chunkSize;

            var parsed = parser.Feed(chunk);
            reconstructedFrames.AddRange(parsed);
        }

        // Verify exact frame reconstruction
        Assert.Equal(frameCount, reconstructedFrames.Count);

        for (int i = 0; i < frameCount; i++)
        {
            // The parser strips the 4-byte length prefix but leaves the 1-byte MessageType
            Assert.True(originalFrames[i].AsSpan(4).SequenceEqual(reconstructedFrames[i]),
                $"Frame {i} corrupted.");
        }
    }

    [Fact]
    public void FrameParser_Should_Reject_Oversized_Frames()
    {
        var parser = new FrameParser();

        byte[] malicious = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(malicious, 50_000_000);

        Assert.Throws<InvalidOperationException>(() =>
        {
            parser.Feed(malicious);
        });
    }
}