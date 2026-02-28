using SmallFile.Core;
using SmallFile.Core.Models;
using SmallFile.Testing;
using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace SmallFile.Tests;

public class EngineTreeExchangeTests
{
    [Fact]
    public async Task Client_Should_Request_And_Receive_FileTree_From_Server()
    {
        // 1. Setup Transports
        var clientToServer = Channel.CreateUnbounded<byte[]>();
        var serverToClient = Channel.CreateUnbounded<byte[]>();

        var clientTransport = new LoopbackTransport(serverToClient, clientToServer);
        var serverTransport = new LoopbackTransport(clientToServer, serverToClient);

        // 2. Setup Engines
        using var clientEngine = new TransferEngine(clientTransport, isServer: false);
        using var serverEngine = new TransferEngine(serverTransport, isServer: true);

        // Fail test immediately if state machine crashes
        clientEngine.OnError += err => Assert.Fail($"Client error: {err}");
        serverEngine.OnError += err => Assert.Fail($"Server error: {err}");

        // 3. Auto-Confirm SAS to fast-track handshake
        clientEngine.OnSasGenerated += sas => _ = clientEngine.ConfirmSasAsync(true);
        serverEngine.OnSasGenerated += sas => _ = serverEngine.ConfirmSasAsync(true);

        var treeReceivedTcs = new TaskCompletionSource<List<FileEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 4. App Layer Simulation: Server responds to request with a dummy manifest
        var dummyManifest = new List<FileEntry>
        {
            new("test1.txt", 1024, 123456789),
            new("folder/test2.jpg", 2048, 987654321)
        };

        serverEngine.OnRemoteTreeRequested += () => 
        {
            _ = serverEngine.SendFileTreeAsync(dummyManifest);
        };

        // 5. App Layer Simulation: Client captures the received tree
        clientEngine.OnRemoteTreeReceived += files => 
        {
            treeReceivedTcs.TrySetResult(files);
        };

        // 6. Start Connection
        await serverTransport.ConnectAsync();
        await clientEngine.StartConnectionAsync();

        // Wait for session to secure, then client requests the tree
        var securedTcs = new TaskCompletionSource<bool>();
        clientEngine.OnSessionSecured += () => securedTcs.TrySetResult(true);
        await securedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 7. Execute Tree Exchange
        await clientEngine.RequestRemoteTreeAsync();

        // 8. Verify
        var receivedFiles = await treeReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        
        Assert.Equal(2, receivedFiles.Count);
        Assert.Equal("test1.txt", receivedFiles[0].RelativePath);
        Assert.Equal(2048, receivedFiles[1].Size);
    }
}