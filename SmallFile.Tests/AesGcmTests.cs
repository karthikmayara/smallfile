using SmallFile.Core.Crypto;
using System.Security.Cryptography;
using Xunit;

namespace SmallFile.Tests;

public class AesGcmTests
{
    [Fact]
    public void AesGcm_Should_Encrypt_And_Decrypt_With_AAD()
    {
        using var clientCrypto = new SessionCrypto();
        using var serverCrypto = new SessionCrypto();

        clientCrypto.DeriveKeys(serverCrypto.MyPublicKey, serverCrypto.MySalt, isServer: false);
        serverCrypto.DeriveKeys(clientCrypto.MyPublicKey, clientCrypto.MySalt, isServer: true);

        using var clientSession = new AesGcmSession(clientCrypto);
        using var serverSession = new AesGcmSession(serverCrypto);

        byte[] payload = System.Text.Encoding.UTF8.GetBytes("Data Payload");
        byte[] aad = new byte[] { 0x05 }; // e.g., MessageType.FileTree

        byte[] ciphertext = clientSession.Encrypt(payload, aad);
        byte[] decryptedMessage = serverSession.Decrypt(ciphertext, aad);

        Assert.Equal(payload, decryptedMessage);
    }

    [Fact]
    public void AesGcm_Should_Reject_Tampered_AAD()
    {
        using var clientCrypto = new SessionCrypto();
        using var serverCrypto = new SessionCrypto();

        clientCrypto.DeriveKeys(serverCrypto.MyPublicKey, serverCrypto.MySalt, isServer: false);
        serverCrypto.DeriveKeys(clientCrypto.MyPublicKey, clientCrypto.MySalt, isServer: true);

        using var clientSession = new AesGcmSession(clientCrypto);
        using var serverSession = new AesGcmSession(serverCrypto);

        byte[] payload = System.Text.Encoding.UTF8.GetBytes("Data Payload");
        byte[] correctAad = new byte[] { 0x05 };
        byte[] tamperedAad = new byte[] { 0x06 }; 

        byte[] ciphertext = clientSession.Encrypt(payload, correctAad);

        // Assert: Fails because the server evaluates the frame under a different AAD context
        Assert.Throws<CryptographicException>(() => serverSession.Decrypt(ciphertext, tamperedAad));
    }
}