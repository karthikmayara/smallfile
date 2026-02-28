using SmallFile.Core;
using SmallFile.Core.Services;
using SmallFile.Testing;
using System;
using System.IO;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace SmallFile.Tests;

public class EngineDiskIntegrationTests : IDisposable
{
    private readonly string _serverDir;
    private readonly string _clientDir;

    public EngineDiskIntegrationTests()
    {
        _serverDir = Path.Combine(Path.GetTempPath(), "SmallFile_Server_" + Guid.NewGuid());
        _clientDir = Path.Combine(Path.GetTempPath(), "SmallFile_Client_" + Guid.NewGuid());
        Directory.CreateDirectory(_serverDir);
        Directory.CreateDirectory(_clientDir);
    }

    [Fact]
    public async Task Orchestrator_Should_Read_Encrypt_Transfer_And_Write_5MB_File()
    {
        const int fileSize = 5 * 1024 * 1024;
        var testFileName = "payload.bin";
        var serverFilePath = Path.Combine(_serverDir, testFileName);

        var originalData = new byte[fileSize];
        new Random(1337).NextBytes(originalData);
        await File.WriteAllBytesAsync(serverFilePath, originalData);

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

        using var clientOrchestrator = new SingleFileTransferOrchestrator(clientEngine, _clientDir);
        using var serverOrchestrator = new SingleFileTransferOrchestrator(serverEngine, _serverDir);

        var transferCompleteTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientEngine.OnFileCompleteReceived += path =>
        {
            if (path == testFileName) transferCompleteTcs.TrySetResult(true);
        };

        var clientSecuredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverSecuredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientEngine.OnSessionSecured += () => clientSecuredTcs.TrySetResult(true);
        serverEngine.OnSessionSecured += () => serverSecuredTcs.TrySetResult(true);

        await serverTransport.ConnectAsync();
        await clientEngine.StartConnectionAsync();
        
        // 1. Await Handshake
        var secureTask = Task.WhenAll(clientSecuredTcs.Task, serverSecuredTcs.Task);
        var handshakeTimeout = Task.Delay(5000);

        var handshakeCompleted = await Task.WhenAny(secureTask, errorTcs.Task, handshakeTimeout);
        if (handshakeCompleted == errorTcs.Task) Assert.Fail(await errorTcs.Task);
        if (handshakeCompleted == handshakeTimeout) Assert.Fail("Handshake timed out.");

        // 2. Execute Transfer
        await clientEngine.RequestFileAsync(testFileName);
        
        // 3. Await Completion
        var transferTimeout = Task.Delay(15000);
        var transferCompleted = await Task.WhenAny(transferCompleteTcs.Task, errorTcs.Task, transferTimeout);

        if (transferCompleted == errorTcs.Task) Assert.Fail(await errorTcs.Task);
        if (transferCompleted == transferTimeout) Assert.Fail("File transfer timed out.");

        // 4. Verify Disk State
        var clientFilePath = Path.Combine(_clientDir, testFileName);
        Assert.True(File.Exists(clientFilePath), "Transferred file is missing on disk.");

        var transferredData = await File.ReadAllBytesAsync(clientFilePath);
        Assert.True(originalData.SequenceEqual(transferredData), "Disk file corruption detected. Bytes do not match.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_serverDir)) Directory.Delete(_serverDir, true);
        if (Directory.Exists(_clientDir)) Directory.Delete(_clientDir, true);
    }
}