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
        var clientToServer = Channel.CreateUnbounded<byte[]>();
        var serverToClient = Channel.CreateUnbounded<byte[]>();

        var clientTransport = new LoopbackTransport(serverToClient, clientToServer);
        var serverTransport = new LoopbackTransport(clientToServer, serverToClient);

        using var clientEngine = new TransferEngine(clientTransport, isServer: false);
        using var serverEngine = new TransferEngine(serverTransport, isServer: true);

        var errorTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientEngine.OnError += err => errorTcs.TrySetResult($"Client error: {err}");
        serverEngine.OnError += err => errorTcs.TrySetResult($"Server error: {err}");

        clientEngine.OnSasGenerated += sas => _ = clientEngine.ConfirmSasAsync(true);
        serverEngine.OnSasGenerated += sas => _ = serverEngine.ConfirmSasAsync(true);

        var treeReceivedTcs = new TaskCompletionSource<List<FileEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientSecuredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverSecuredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        clientEngine.OnSessionSecured += () => clientSecuredTcs.TrySetResult(true);
        serverEngine.OnSessionSecured += () => serverSecuredTcs.TrySetResult(true);

        var dummyManifest = new List<FileEntry>
        {
            new("test1.txt", 1024, 123456789),
            new("folder/test2.jpg", 2048, 987654321)
        };

        serverEngine.OnRemoteTreeRequested += () => _ = serverEngine.SendFileTreeAsync(dummyManifest);
        clientEngine.OnRemoteTreeReceived += files => treeReceivedTcs.TrySetResult(files);

        await serverTransport.ConnectAsync();
        await clientEngine.StartConnectionAsync();

        // 1. Await Handshake
        var secureTask = Task.WhenAll(clientSecuredTcs.Task, serverSecuredTcs.Task);
        var handshakeTimeout = Task.Delay(5000);
        
        var handshakeCompleted = await Task.WhenAny(secureTask, errorTcs.Task, handshakeTimeout);
        if (handshakeCompleted == errorTcs.Task) Assert.Fail(await errorTcs.Task);
        if (handshakeCompleted == handshakeTimeout) Assert.Fail("Handshake timed out.");

        // 2. Execute Request
        await clientEngine.RequestRemoteTreeAsync();

        // 3. Await Response
        var receiveTimeout = Task.Delay(5000);
        var receiveCompleted = await Task.WhenAny(treeReceivedTcs.Task, errorTcs.Task, receiveTimeout);
        
        if (receiveCompleted == errorTcs.Task) Assert.Fail(await errorTcs.Task);
        if (receiveCompleted == receiveTimeout) Assert.Fail("Tree exchange timed out.");

        var receivedFiles = await treeReceivedTcs.Task;

        Assert.Equal(2, receivedFiles.Count);
        Assert.Equal("test1.txt", receivedFiles[0].RelativePath);
        Assert.Equal(2048, receivedFiles[1].Size);
    }
}