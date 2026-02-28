using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SmallFile.Core.Commands;
using SmallFile.Core.Crypto;
using SmallFile.Core.Models;
using SmallFile.Core.Protocol;
using SmallFile.Core.Transport;

namespace SmallFile.Core;

public sealed class TransferEngine : IDisposable
{
    private readonly Channel<IEngineCommand> _commandQueue;
    private readonly ITransport _transport;
    private readonly bool _isServer;
    private readonly string _name;

    private EngineState _state = EngineState.Idle;
    public EngineState CurrentState => _state;
    
    private SessionCrypto? _crypto;
    private AesGcmSession? _aesSession;
    private readonly CancellationTokenSource _cts = new();

    // Security Events
    public event Action<string[]>? OnSasGenerated;
    public event Action? OnSessionSecured;
    public event Action<string>? OnError;
    
    // Application Layer Integration Events - Metadata
    public event Action? OnRemoteTreeRequested;
    public event Action<List<FileEntry>>? OnRemoteTreeReceived;

    // Application Layer Integration Events - File Transfer
    public event Action<string>? OnFileRequested;
    public event Action<string, long, byte[]>? OnFileChunkReceived;
    public event Action<string>? OnFileCompleteReceived;

    public TransferEngine(ITransport transport, bool isServer)
    {
        _transport = transport;
        _isServer = isServer;
        _name = isServer ? "Server" : "Client";

        _commandQueue = Channel.CreateUnbounded<IEngineCommand>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        HookTransportEvents();
        _ = RunEngineLoopAsync(_cts.Token);
    }

    private void Log(string message) => Console.WriteLine($"[{_name}] {message}");

    private void HookTransportEvents()
    {
        _transport.OnConnected += () => Enqueue(new TransportConnectedCommand());
        _transport.OnReceive += payload => Enqueue(new NetworkFrameReceivedCommand(payload));
        _transport.OnDisconnected += () => Enqueue(new TransportDisconnectedCommand());
    }

    private void Enqueue(IEngineCommand cmd) => _commandQueue.Writer.TryWrite(cmd);

    public async Task StartConnectionAsync()
    {
        Log("StartConnectionAsync requested.");
        Enqueue(new StartConnectionCommand());
        await Task.Yield(); 
    }

    public async Task ConfirmSasAsync(bool accepted)
    {
        Log($"ConfirmSasAsync({accepted}) requested.");
        Enqueue(new ConfirmSasCommand(accepted));
        await Task.Yield();
    }

    public async Task RequestRemoteTreeAsync()
    {
        Log("RequestRemoteTreeAsync requested.");
        Enqueue(new RequestTreeCommand());
        await Task.Yield();
    }

    public async Task SendFileTreeAsync(List<FileEntry> files)
    {
        Log($"SendFileTreeAsync requested with {files.Count} files.");
        Enqueue(new SendTreeCommand(files));
        await Task.Yield();
    }

    public async Task RequestFileAsync(string relativePath)
    {
        Log($"RequestFileAsync: {relativePath}");
        Enqueue(new RequestFileCommand(relativePath));
        await Task.Yield();
    }

    public async Task SendFileChunkAsync(string relativePath, long offset, byte[] data)
    {
        Enqueue(new SendFileChunkCommand(relativePath, offset, data));
        await Task.Yield();
    }

    public async Task SendFileCompleteAsync(string relativePath)
    {
        Log($"SendFileCompleteAsync: {relativePath}");
        Enqueue(new SendFileCompleteCommand(relativePath));
        await Task.Yield();
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
                Log($"CRASHED: {ex.Message}");
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
            case RequestTreeCommand: HandleRequestTreeCommand(); break;
            case SendTreeCommand st: HandleSendTreeCommand(st.Files); break;
            case RequestFileCommand rf: HandleRequestFileCommand(rf.RelativePath); break;
            case SendFileChunkCommand sfc: HandleSendFileChunkCommand(sfc.RelativePath, sfc.Offset, sfc.Data); break;
            case SendFileCompleteCommand sfc: HandleSendFileCompleteCommand(sfc.RelativePath); break;
            case TransportDisconnectedCommand: TransitionTo(EngineState.Terminated); break;
            default: throw new InvalidOperationException("Unknown command");
        }
    }

    private void HandleStartConnection()
    {
        if (_state != EngineState.Idle) return;
        Log("Initiating Transport.ConnectAsync().");
        _ = _transport.ConnectAsync();
    }

    private void HandleTransportConnected()
    {
        Log("Transport Connected event fired.");
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

        // Only log non-chunk frames to prevent console flooding during large transfers
        if (msgType != MessageType.FileChunk)
        {
            Log($"Received frame type: 0x{msgType:X2}. Current State: {_state}");
        }

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
            case MessageType.RequestTree: ProcessRequestTree(body); break;
            case MessageType.FileTreeChunk: ProcessFileTree(body); break;
            case MessageType.FileRequest: ProcessFileRequest(body); break;
            case MessageType.FileChunk: ProcessFileChunk(body); break;
            case MessageType.FileComplete: ProcessFileComplete(body); break;
            default: throw new InvalidOperationException("Protocol violation");
        }
    }

    private void ProcessHello(byte[] body)
    {
        Log("Processing HELLO.");
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
        Log("Processing KEY_EXCHANGE.");
        EnsureState(EngineState.HandshakingCrypto);
        var keyFrame = KeyExchangeFrame.Deserialize(body);

        _crypto ??= new SessionCrypto();
        _crypto.DeriveKeys(keyFrame.PublicKey, keyFrame.Salt, _isServer);

        _aesSession = new AesGcmSession(_crypto);

        Log("AES Session created. Transitioning to AwaitingSas and emitting SAS.");
        TransitionTo(EngineState.AwaitingSas);
        OnSasGenerated?.Invoke(_crypto.SasEmojis!);
    }

    private void HandleConfirmSas(ConfirmSasCommand cmd)
    {
        Log("Processing Confirm SAS Command.");
        EnsureState(EngineState.AwaitingSas);
        if (!cmd.Accepted) throw new InvalidOperationException("SAS rejected");

        var auth = new AuthVerifyFrame(true);
        Log("Sending encrypted AUTH_VERIFY.");
        SendEncrypted(MessageType.AuthVerify, auth.Serialize());
    }

    private void ProcessAuthVerify(byte[] body)
    {
        Log("Processing AUTH_VERIFY.");
        if (_state == EngineState.SessionSecured) 
        {
            Log("Already secured. Ignoring duplicate AUTH_VERIFY.");
            return;
        }

        EnsureState(EngineState.AwaitingSas);
        
        var auth = AuthVerifyFrame.Deserialize(body);
        if (!auth.Accepted) throw new InvalidOperationException("Peer rejected SAS");

        Log("SAS accepted by peer. Transitioning to SessionSecured.");
        TransitionTo(EngineState.SessionSecured);
        OnSessionSecured?.Invoke();
    }

    private void HandleRequestTreeCommand()
    {
        EnsureState(EngineState.SessionSecured);
        SendEncrypted(MessageType.RequestTree, new RequestTreeFrame().Serialize());
    }

    private void HandleSendTreeCommand(List<FileEntry> files)
    {
        EnsureState(EngineState.SessionSecured);
        var frame = new FileTreeFrame(files);
        SendEncrypted(MessageType.FileTreeChunk, frame.Serialize());
    }

    private void HandleRequestFileCommand(string relativePath)
    {
        EnsureState(EngineState.SessionSecured);
        var frame = new FileRequestFrame(relativePath);
        SendEncrypted(MessageType.FileRequest, frame.Serialize());
    }

    private void HandleSendFileChunkCommand(string relativePath, long offset, byte[] data)
    {
        EnsureState(EngineState.SessionSecured);
        var frame = new FileChunkFrame(relativePath, offset, data);
        SendEncrypted(MessageType.FileChunk, frame.Serialize());
    }

    private void HandleSendFileCompleteCommand(string relativePath)
    {
        EnsureState(EngineState.SessionSecured);
        var frame = new FileCompleteFrame(relativePath);
        SendEncrypted(MessageType.FileComplete, frame.Serialize());
    }

    private void ProcessRequestTree(byte[] body)
    {
        EnsureState(EngineState.SessionSecured);
        _ = RequestTreeFrame.Deserialize(body);
        Log("Remote requested file tree.");
        OnRemoteTreeRequested?.Invoke();
    }

    private void ProcessFileTree(byte[] body)
    {
        EnsureState(EngineState.SessionSecured);
        var frame = FileTreeFrame.Deserialize(body);
        Log($"Received file tree with {frame.Files.Count} entries.");
        OnRemoteTreeReceived?.Invoke(frame.Files);
    }

    private void ProcessFileRequest(byte[] body)
    {
        EnsureState(EngineState.SessionSecured);
        var frame = FileRequestFrame.Deserialize(body);
        Log($"Remote requested file: {frame.RelativePath}");
        OnFileRequested?.Invoke(frame.RelativePath);
    }

    private void ProcessFileChunk(byte[] body)
    {
        EnsureState(EngineState.SessionSecured);
        var frame = FileChunkFrame.Deserialize(body);
        OnFileChunkReceived?.Invoke(frame.RelativePath, frame.Offset, frame.Data);
    }

    private void ProcessFileComplete(byte[] body)
    {
        EnsureState(EngineState.SessionSecured);
        var frame = FileCompleteFrame.Deserialize(body);
        Log($"Remote completed sending file: {frame.RelativePath}");
        OnFileCompleteReceived?.Invoke(frame.RelativePath);
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

    private void TransitionTo(EngineState newState)
    {
        Log($"State transitioned from {_state} to {newState}");
        _state = newState;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _aesSession?.Dispose();
        _crypto?.Dispose();
    }
}