namespace SmallFile.Core.Transport;

internal interface ITransport
{
    event Action<byte[]> OnReceive;
    event Action OnConnected;
    event Action OnDisconnected;

    Task ConnectAsync();
    Task SendAsync(byte[] payload);
    Task DisconnectAsync();
}