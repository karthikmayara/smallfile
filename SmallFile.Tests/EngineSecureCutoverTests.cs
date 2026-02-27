using SmallFile.Core;
using SmallFile.Core.Protocol;
using SmallFile.Testing;
using System.Threading.Channels;
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

        var clientSecuredTcs = new TaskCompletionSource<bool>();
        var serverSecuredTcs = new TaskCompletionSource<bool>();

        clientEngine.OnSessionSecured += () => clientSecuredTcs.TrySetResult(true);
        serverEngine.OnSessionSecured += () => serverSecuredTcs.TrySetResult(true);

        // Auto-accept SAS to simulate the human validation step
        clientEngine.OnSasGenerated += sas => _ = clientEngine.ConfirmSasAsync(true);
        serverEngine.OnSasGenerated += sas => _ = serverEngine.ConfirmSasAsync(true);

        await serverTransport.ConnectAsync();
        await clientEngine.StartConnectionAsync();

        // 5 second timeout prevents CI deadlocks if the state machine swallows a frame
        var timeout = TimeSpan.FromSeconds(5);

        await clientSecuredTcs.Task.WaitAsync(timeout);
        await serverSecuredTcs.Task.WaitAsync(timeout);

        // Prove the state machine correctly navigated the encrypted cutover
        Assert.Equal(EngineState.SessionSecured, clientEngine.CurrentState);
        Assert.Equal(EngineState.SessionSecured, serverEngine.CurrentState);
    }
}