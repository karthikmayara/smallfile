using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using SmallFile.Core.Transport;

namespace SmallFile.Testing;

public sealed class LoopbackTransport : ITransport
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;
    private readonly FrameParser _parser = new();

    public event Action<byte[]>? OnReceive;
    public event Action? OnConnected;
    public event Action? OnDisconnected;

    public LoopbackTransport(Channel<byte[]> incoming, Channel<byte[]> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public async Task ConnectAsync()
    {
        OnConnected?.Invoke();
        _ = StartReceiveLoop();
        await Task.CompletedTask;
    }

    public async Task SendAsync(byte[] payload)
    {
        await _outgoing.Writer.WriteAsync(payload);
    }

    public async Task DisconnectAsync()
    {
        _incoming.Writer.TryComplete();
        OnDisconnected?.Invoke();
        await Task.CompletedTask;
    }

    private async Task StartReceiveLoop()
    {
        await foreach (var frameChunk in _incoming.Reader.ReadAllAsync())
        {
            // Even in loopback, we treat chunks as potentially fragmented streams
            foreach (var completeFrame in _parser.Feed(frameChunk))
            {
                OnReceive?.Invoke(completeFrame);
            }
        }
    }
}