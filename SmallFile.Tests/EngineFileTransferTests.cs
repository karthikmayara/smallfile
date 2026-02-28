using SmallFile.Core;
using SmallFile.Testing;
using System;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace SmallFile.Tests;

public class EngineFileTransferTests
{
    [Fact]
    public async Task Engine_Should_Stream_And_Reconstruct_5MB_File()
    {
        // 1. Setup Transports and Engines
        var clientToServer = Channel.CreateUnbounded<byte[]>();
        var serverToClient = Channel.CreateUnbounded<byte[]>();

        var clientTransport = new LoopbackTransport(serverToClient, clientToServer);
        var serverTransport = new LoopbackTransport(clientToServer, serverToClient);

        using var clientEngine = new TransferEngine(clientTransport, isServer: false);
        using var serverEngine = new TransferEngine(serverTransport, isServer: true);

        clientEngine.OnError += err => Assert.Fail($"Client error: {err}");
        serverEngine.OnError += err => Assert.Fail($"Server error: {err}");

        // Auto-Confirm SAS
        clientEngine.OnSasGenerated += sas => _ = clientEngine.ConfirmSasAsync(true);
        serverEngine.OnSasGenerated += sas => _ = serverEngine.ConfirmSasAsync(true);

        // 2. Prepare 5MB Payload
        const int fileSize = 5 * 1024 * 1024;
        const int chunkSize = 64 * 1024;
        const string testFileName = "video.mp4";

        var originalData = new byte[fileSize];
        new Random(42).NextBytes(originalData);
        var reconstructedData = new byte[fileSize];

        var transferCompleteTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 3. Server App Layer Simulation: Stream chunks when requested
        serverEngine.OnFileRequested += path =>
        {
            Assert.Equal(testFileName, path);
            
            // Fire-and-forget the streaming loop so we don't block the Engine's actor loop
            Task.Run(async () =>
            {
                long offset = 0;
                while (offset < fileSize)
                {
                    int currentChunkSize = (int)Math.Min(chunkSize, fileSize - offset);
                    byte[] chunk = new byte[currentChunkSize];
                    Buffer.BlockCopy(originalData, (int)offset, chunk, 0, currentChunkSize);

                    await serverEngine.SendFileChunkAsync(path, offset, chunk);
                    offset += currentChunkSize;
                }

                await serverEngine.SendFileCompleteAsync(path);
            });
        };

        // 4. Client App Layer Simulation: Reconstruct chunks into memory
        clientEngine.OnFileChunkReceived += (path, offset, data) =>
        {
            Assert.Equal(testFileName, path);
            Buffer.BlockCopy(data, 0, reconstructedData, (int)offset, data.Length);
        };

        clientEngine.OnFileCompleteReceived += path =>
        {
            Assert.Equal(testFileName, path);
            transferCompleteTcs.TrySetResult(true);
        };

        // 5. Connect and Execute
        var securedTcs = new TaskCompletionSource<bool>();
        clientEngine.OnSessionSecured += () => securedTcs.TrySetResult(true);

        await serverTransport.ConnectAsync();
        await clientEngine.StartConnectionAsync();

        await securedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Client initiates the transfer
        await clientEngine.RequestFileAsync(testFileName);

        // 6. Verify Completion and Integrity
        await transferCompleteTcs.Task.WaitAsync(TimeSpan.FromSeconds(15)); // 15s timeout for 5MB loopback

        Assert.True(originalData.SequenceEqual(reconstructedData), "Reconstructed data does not match original payload.");
    }
}