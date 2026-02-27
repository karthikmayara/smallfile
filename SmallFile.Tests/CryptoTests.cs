using SmallFile.Core.Crypto;
using Xunit;

namespace SmallFile.Tests;

public class CryptoTests
{
    [Fact]
    public void DirectionalKeys_Should_Align_Correctly()
    {
        // 1. Arrange: Instantiate Client and Server crypto modules
        using var clientCrypto = new SessionCrypto();
        using var serverCrypto = new SessionCrypto();

        // 2. Act: Exchange public keys and salts
        clientCrypto.DeriveKeys(serverCrypto.MyPublicKey, serverCrypto.MySalt, isServer: false);
        serverCrypto.DeriveKeys(clientCrypto.MyPublicKey, clientCrypto.MySalt, isServer: true);

        // 3. Assert: Both derived keys successfully
        Assert.NotNull(clientCrypto.TxKey);
        Assert.NotNull(clientCrypto.RxKey);
        Assert.NotNull(serverCrypto.TxKey);
        Assert.NotNull(serverCrypto.RxKey);

        // 4. Assert: SAS matches perfectly (MITM protection)
        Assert.NotNull(clientCrypto.SasEmojis);
        Assert.NotNull(serverCrypto.SasEmojis);
        Assert.Equal(clientCrypto.SasEmojis, serverCrypto.SasEmojis);

        // 5. Assert: Directional alignment is perfect
        // Client's Transmit must equal Server's Receive
        Assert.Equal(clientCrypto.TxKey, serverCrypto.RxKey);
        Assert.Equal(clientCrypto.TxBaseNonce, serverCrypto.RxBaseNonce);

        // Server's Transmit must equal Client's Receive
        Assert.Equal(serverCrypto.TxKey, clientCrypto.RxKey);
        Assert.Equal(serverCrypto.TxBaseNonce, clientCrypto.RxBaseNonce);

        // 6. Assert: Tx and Rx are strictly separated (No reflection attacks)
        Assert.NotEqual(clientCrypto.TxKey, clientCrypto.RxKey);
        Assert.NotEqual(clientCrypto.TxBaseNonce, clientCrypto.RxBaseNonce);
    }
}