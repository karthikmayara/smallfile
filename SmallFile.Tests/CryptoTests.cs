using SmallFile.Core.Crypto;
using Xunit;

namespace SmallFile.Tests;

public class CryptoTests
{
    [Fact]
    public void DirectionalKeys_Should_Align_Correctly_v1_1()
    {
        using var clientCrypto = new SessionCrypto();
        using var serverCrypto = new SessionCrypto();

        clientCrypto.DeriveKeys(serverCrypto.MyPublicKey, serverCrypto.MySalt, isServer: false);
        serverCrypto.DeriveKeys(clientCrypto.MyPublicKey, clientCrypto.MySalt, isServer: true);

        Assert.Equal(clientCrypto.SasEmojis, serverCrypto.SasEmojis);

        // Verify that the data streams are aligned:
        // Client-to-Server stream must match
        Assert.Equal(clientCrypto.TxKey, serverCrypto.RxKey);
        Assert.Equal(clientCrypto.TxBaseNonce, serverCrypto.RxBaseNonce);

        // Server-to-Client stream must match
        Assert.Equal(serverCrypto.TxKey, clientCrypto.RxKey);
        Assert.Equal(serverCrypto.TxBaseNonce, clientCrypto.RxBaseNonce);
        
        // Ensure no key reuse across directions
        Assert.NotEqual(clientCrypto.TxKey, clientCrypto.RxKey);
    }
}