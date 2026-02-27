using SmallFile.Core;
using SmallFile.Testing;
using System;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace SmallFile.Tests;

public class EngineSecureCutoverTests
{
    private readonly ITestOutputHelper _output;
    private readonly StringWriter _consoleWriter;

    public EngineSecureCutoverTests(ITestOutputHelper output)
    {
        _output = output;
        _consoleWriter = new StringWriter();
        Console.SetOut(_consoleWriter);
    }

    [Fact]
    public async Task Engine_Should_Handshake_And_Encrypt_AuthVerify()
    {
        var clientToServer = Channel.CreateUnbounded<byte[]>();
        var serverToClient = Channel.CreateUnbounded<byte[]>();

        var clientTransport = new LoopbackTransport(serverToClient, clientToServer);
        var serverTransport = new LoopbackTransport(clientToServer, serverToClient);

        using var clientEngine = new TransferEngine(clientTransport, isServer: false);
        using var serverEngine = new TransferEngine(serverTransport, isServer: true);

        var clientSecuredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverSecuredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        clientEngine.OnError += err => clientSecuredTcs.TrySetException(new Exception($"Client crashed: {err}"));
        serverEngine.OnError += err => serverSecuredTcs.TrySetException(new Exception($"Server crashed: {err}"));

        clientEngine.OnSessionSecured += () => clientSecuredTcs.TrySetResult(true);
        serverEngine.OnSessionSecured += () => serverSecuredTcs.TrySetResult(true);

        clientEngine.OnSasGenerated += sas => _ = clientEngine.ConfirmSasAsync(true);
        serverEngine.OnSasGenerated += sas => _ = serverEngine.ConfirmSasAsync(true);

        await serverTransport.ConnectAsync();
        await clientEngine.StartConnectionAsync();

        var timeout = TimeSpan.FromSeconds(5);

        try
        {
            await clientSecuredTcs.Task.WaitAsync(timeout);
            await serverSecuredTcs.Task.WaitAsync(timeout);
        }
        finally
        {
            _output.WriteLine(_consoleWriter.ToString());
        }

        Assert.Equal(EngineState.SessionSecured, clientEngine.CurrentState);
        Assert.Equal(EngineState.SessionSecured, serverEngine.CurrentState);
    }
}