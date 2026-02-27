using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SmallFile.Core.Commands;
using SmallFile.Core.Crypto;
using SmallFile.Core.Protocol;
using SmallFile.Core.Transport;

namespace SmallFile.Core;

public sealed class TransferEngine : IDisposable
{
    private readonly Channel<IEngineCommand> _commandQueue;
    private readonly ITransport _transport;
    private readonly bool _isServer;

    private EngineState _state = EngineState.Idle;
    public EngineState CurrentState => _state;
    
    private SessionCrypto? _crypto;
    private AesGcmSession? _aesSession;
    private readonly CancellationTokenSource _cts = new();

    public event Action<string[]>? OnSasGenerated;
    public event Action? OnSessionSecured;
    public event Action<string>? OnError;

    public TransferEngine(ITransport transport, bool isServer)
    {
        _transport = transport;
        _isServer = isServer;

        _commandQueue = Channel.CreateUnbounded<IEngineCommand>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        HookTransportEvents();
        _ = RunEngineLoopAsync(_cts.Token);
    }

    private void HookTransportEvents()
    {
        _transport.OnConnected += () => Enqueue(new TransportConnectedCommand());
        _transport.OnReceive += payload => Enqueue(new NetworkFrameReceivedCommand(payload));
        _transport.OnDisconnected += () => Enqueue(new TransportDisconnectedCommand());
    }

    private void Enqueue(IEngineCommand cmd) => _commandQueue.Writer.TryWrite(cmd);

    public async Task StartConnectionAsync()
    {
        Enqueue(new StartConnectionCommand());
        await Task.CompletedTask;
    }

    public async Task ConfirmSasAsync(bool accepted)
    {
        Enqueue(new ConfirmSasCommand(accepted));
        await Task.CompletedTask;
    }

    private async Task RunEngineLoopAsync(CancellationToken ct)
    {
        await foreach (var cmd in _commandQueue.Reader.ReadAllAsync(ct))
        {
            try
            {
                ProcessCommand(cmd);
            }
            catch (Exception ex)
            {
                TransitionTo(EngineState.Terminated);
                OnError?.Invoke(ex.Message);
            }
        }
    }

    private void ProcessCommand(IEngineCommand cmd)
    {
        switch (cmd)
        {
            case StartConnectionCommand: HandleStartConnection(); break;
            case TransportConnectedCommand: HandleTransportConnected(); break;
            case NetworkFrameReceivedCommand nf: HandleNetworkFrame(nf.Payload); break;
            case ConfirmSasCommand cs: HandleConfirmSas(cs); break;
            case TransportDisconnectedCommand: TransitionTo(EngineState.Terminated); break;
            default: throw new InvalidOperationException("Unknown command");
        }
    }

    private void HandleStartConnection()
    {
        if (_state != EngineState.Idle) return;
        _ = _transport.ConnectAsync();
    }

    private void HandleTransportConnected()
    {
        if (_state >= EngineState.HandshakingCrypto) return; 

        TransitionTo(EngineState.TcpConnected);

        var hello = new HelloFrame("1.1", "SmallFile");
        var wrapped = FrameEnvelope.Wrap(MessageType.Hello, hello.Serialize());
        _ = _transport.SendAsync(wrapped);

        TransitionTo(EngineState.HandshakingCrypto);
    }

    private void HandleNetworkFrame(byte[] payload)
    {
        if (payload.Length == 0) throw new InvalidOperationException("Empty frame");

        byte msgType = payload[0];
        byte[] body = payload.AsSpan(1).ToArray();

        if (_state >= EngineState.AwaitingSas)
        {
            if (_aesSession == null) throw new InvalidOperationException("AES session missing.");
            
            byte[] aad = new byte[] { msgType };
            try
            {
                body = _aesSession.Decrypt(body, aad);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                throw new InvalidOperationException("Encrypted frame validation failed.", ex);
            }
        }

        switch (msgType)
        {
            case MessageType.Hello: ProcessHello(body); break;
            case MessageType.KeyExchange: ProcessKeyExchange(body); break;
            case MessageType.AuthVerify: ProcessAuthVerify(body); break;
            default: throw new InvalidOperationException("Protocol violation");
        }
    }

    private void ProcessHello(byte[] body)
    {
        if (_state < EngineState.HandshakingCrypto)
        {
            var myHello = new HelloFrame("1.1", "SmallFile");
            var wrappedHello = FrameEnvelope.Wrap(MessageType.Hello, myHello.Serialize());
            _ = _transport.SendAsync(wrappedHello);
            TransitionTo(EngineState.HandshakingCrypto);
        }

        EnsureState(EngineState.HandshakingCrypto);

        var hello = HelloFrame.Deserialize(body);
        if (hello.Version != "1.1") throw new InvalidOperationException("Version mismatch");

        _crypto ??= new SessionCrypto();
        var keyFrame = new KeyExchangeFrame(_crypto.MyPublicKey, _crypto.MySalt);
        var wrappedKey = FrameEnvelope.Wrap(MessageType.KeyExchange, keyFrame.Serialize());
        _ = _transport.SendAsync(wrappedKey);
    }

    private void ProcessKeyExchange(byte[] body)
    {
        EnsureState(EngineState.HandshakingCrypto);
        var keyFrame = KeyExchangeFrame.Deserialize(body);

        _crypto ??= new SessionCrypto();
        _crypto.DeriveKeys(keyFrame.PublicKey, keyFrame.Salt, _isServer);

        _aesSession = new AesGcmSession(_crypto);

        TransitionTo(EngineState.AwaitingSas);
        OnSasGenerated?.Invoke(_crypto.SasEmojis!);
    }

    private void HandleConfirmSas(ConfirmSasCommand cmd)
    {
        EnsureState(EngineState.AwaitingSas);
        if (!cmd.Accepted) throw new InvalidOperationException("SAS rejected");

        var auth = new AuthVerifyFrame(true);
        SendEncrypted(MessageType.AuthVerify, auth.Serialize());
    }

    private void ProcessAuthVerify(byte[] body)
    {
        if (_state == EngineState.SessionSecured) return;

        EnsureState(EngineState.AwaitingSas);
        
        var auth = AuthVerifyFrame.Deserialize(body);
        if (!auth.Accepted) throw new InvalidOperationException("Peer rejected SAS");

        TransitionTo(EngineState.SessionSecured);
        OnSessionSecured?.Invoke();
    }

    private void SendEncrypted(byte msgType, byte[] plaintext)
    {
        if (_aesSession == null) throw new InvalidOperationException("AES session not initialized.");
        
        byte[] aad = new byte[] { msgType };
        byte[] ciphertext = _aesSession.Encrypt(plaintext, aad);

        var wrapped = FrameEnvelope.Wrap(msgType, ciphertext);
        _ = _transport.SendAsync(wrapped);
    }

    private void EnsureState(EngineState expected)
    {
        if (_state != expected) throw new InvalidOperationException($"State violation. Current: {_state}, Expected: {expected}");
    }

    private void TransitionTo(EngineState newState) => _state = newState;

    public void Dispose()
    {
        _cts.Cancel();
        _aesSession?.Dispose();
        _crypto?.Dispose();
    }
}