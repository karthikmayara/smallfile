using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SmallFile.Core.Transport;

public sealed class SocketTransport : ITransport, IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly FrameParser _parser = new();
    private readonly CancellationTokenSource _cts = new();

    public event Action<byte[]>? OnReceive;
    public event Action? OnConnected;
    public event Action? OnDisconnected;

    public SocketTransport(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task ConnectAsync()
    {
        _client = new TcpClient();
        await _client.ConnectAsync(_host, _port);
        _stream = _client.GetStream();
        _ = Task.Run(ReadLoopAsync);
        OnConnected?.Invoke();
    }

    public async Task SendAsync(byte[] payload)
    {
        if (_stream == null) throw new InvalidOperationException("Not connected.");
        await _stream.WriteAsync(payload, _cts.Token);
    }

    public async Task DisconnectAsync()
    {
        _cts.Cancel();
        if (_stream != null) await _stream.DisposeAsync();
        _client?.Close();
        OnDisconnected?.Invoke();
    }

    private async Task ReadLoopAsync()
    {
        if (_stream == null) return;
        byte[] buffer = new byte[8192];
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                int bytesRead = await _stream.ReadAsync(buffer, _cts.Token);
                if (bytesRead == 0) break;

                foreach (var frame in _parser.Feed(buffer.AsSpan(0, bytesRead)))
                {
                    OnReceive?.Invoke(frame);
                }
            }
        }
        catch { /* Engine handles termination via Disconnect */ }
        OnDisconnected?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stream?.Dispose();
        _client?.Dispose();
        _cts.Dispose();
    }
}