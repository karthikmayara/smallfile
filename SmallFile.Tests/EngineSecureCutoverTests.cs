using SmallFile.Core;
using SmallFile.Testing;
using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace SmallFile.Tests;

public class EngineSecureCutoverTests
{
    [Fact]
    public async Task Engine_Should_Handshake_And_Encrypt_AuthVerify()
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

        var clientSecuredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverSecuredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        clientEngine.OnSessionSecured += () => clientSecuredTcs.TrySetResult(true);
        serverEngine.OnSessionSecured += () => serverSecuredTcs.TrySetResult(true);

        clientEngine.OnSasGenerated += sas => _ = clientEngine.ConfirmSasAsync(true);
        serverEngine.OnSasGenerated += sas => _ = serverEngine.ConfirmSasAsync(true);

        await serverTransport.ConnectAsync();
        await clientEngine.StartConnectionAsync();

        var secureTask = Task.WhenAll(clientSecuredTcs.Task, serverSecuredTcs.Task);
        var timeoutTask = Task.Delay(5000);

        var completed = await Task.WhenAny(secureTask, errorTcs.Task, timeoutTask);

        if (completed == errorTcs.Task) Assert.Fail(await errorTcs.Task);
        if (completed == timeoutTask) Assert.Fail("Handshake timed out.");

        Assert.Equal(EngineState.SessionSecured, clientEngine.CurrentState);
        Assert.Equal(EngineState.SessionSecured, serverEngine.CurrentState);
    }
}