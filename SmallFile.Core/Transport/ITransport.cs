using System;
using System.Threading.Tasks;

namespace SmallFile.Core.Transport;

public interface ITransport
{
    event Action<byte[]>? OnReceive;
    event Action? OnConnected;
    event Action? OnDisconnected;

    Task ConnectAsync();
    Task SendAsync(byte[] payload);
    Task DisconnectAsync();
}