using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SmallFile.Core.Crypto;

internal sealed class SessionCrypto : IDisposable
{
    private readonly ECDiffieHellman _ecdh;
    
    public byte[] MyPublicKey { get; }
    public byte[] MySalt { get; }

    public byte[]? TxKey { get; private set; }
    public byte[]? RxKey { get; private set; }
    public byte[]? TxBaseNonce { get; private set; }
    public byte[]? RxBaseNonce { get; private set; }
    
    public string[]? SasEmojis { get; private set; }

    public SessionCrypto()
    {
        // Use nistP256 for native Windows CNG and Android compatibility
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        // Export the full SPKI (Standard Public Key Infrastructure) payload
        MyPublicKey = _ecdh.ExportSubjectPublicKeyInfo();

        // 32 bytes of secure entropy for HKDF salt
        MySalt = RandomNumberGenerator.GetBytes(32);
    }

    public void DeriveKeys(byte[] peerPublicKey, byte[] peerSalt, bool isServer)
    {
        if (peerSalt.Length != 32)
            throw new ArgumentException("Invalid peer salt length.");

        // 1. Import Peer's Public Key
        using var peerEcdh = ECDiffieHellman.Create();
        try 
        {
            peerEcdh.ImportSubjectPublicKeyInfo(peerPublicKey, out _);
            
            // SECURITY: Enforce that the peer is actually using P-256. 
            // Prevents curve-switching attacks.
            if (peerEcdh.KeySize != 256)
                throw new CryptographicException("Peer key size mismatch (Expected P-256).");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to import peer public key.", ex);
        }

        // 2. Compute Shared Secret
        byte[] sharedSecret = _ecdh.DeriveRawSecretAgreement(peerEcdh.PublicKey);

        // 3. Salt ordering (Client || Server) - Deterministic based on TCP Role
        byte[] combinedSalt = new byte[64];
        byte[] clientSalt = isServer ? peerSalt : MySalt;
        byte[] serverSalt = isServer ? MySalt : peerSalt;
        
        Buffer.BlockCopy(clientSalt, 0, combinedSalt, 0, 32);
        Buffer.BlockCopy(serverSalt, 0, combinedSalt, 32, 32);

        // 4. HKDF Expansion with Domain Separation
        byte[] keyC2S = DeriveHkdf(sharedSecret, combinedSalt, "local-p2p v1.1 key c2s", 32);
        byte[] keyS2C = DeriveHkdf(sharedSecret, combinedSalt, "local-p2p v1.1 key s2c", 32);
        byte[] nonceC2S = DeriveHkdf(sharedSecret, combinedSalt, "local-p2p v1.1 nonce c2s", 12);
        byte[] nonceS2C = DeriveHkdf(sharedSecret, combinedSalt, "local-p2p v1.1 nonce s2c", 12);
        byte[] sasBytes = DeriveHkdf(sharedSecret, combinedSalt, "local-p2p v1.1 sas", 4);

        // 5. Directional Mapping (Clone to establish absolute memory ownership)
        TxKey = (isServer ? keyS2C : keyC2S).ToArray();
        RxKey = (isServer ? keyC2S : keyS2C).ToArray();
        TxBaseNonce = (isServer ? nonceS2C : nonceC2S).ToArray();
        RxBaseNonce = (isServer ? nonceC2S : nonceS2C).ToArray();

        SasEmojis = GenerateSas(sasBytes);

        // 6. Zeroize intermediate material for Forward Secrecy
        CryptographicOperations.ZeroMemory(sharedSecret);
        CryptographicOperations.ZeroMemory(combinedSalt);
        CryptographicOperations.ZeroMemory(sasBytes);
        CryptographicOperations.ZeroMemory(keyC2S);
        CryptographicOperations.ZeroMemory(keyS2C);
        CryptographicOperations.ZeroMemory(nonceC2S);
        CryptographicOperations.ZeroMemory(nonceS2C);
    }

    private static byte[] DeriveHkdf(byte[] ikm, byte[] salt, string info, int length)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, Encoding.UTF8.GetBytes(info));
    }

    private static string[] GenerateSas(byte[] sasBytes)
    {
        return new[]
        {
            EmojiDictionary.List[sasBytes[0]],
            EmojiDictionary.List[sasBytes[1]],
            EmojiDictionary.List[sasBytes[2]],
            EmojiDictionary.List[sasBytes[3]]
        };
    }

    public void Dispose()
    {
        _ecdh.Dispose();
        if (TxKey != null) CryptographicOperations.ZeroMemory(TxKey);
        if (RxKey != null) CryptographicOperations.ZeroMemory(RxKey);
        if (TxBaseNonce != null) CryptographicOperations.ZeroMemory(TxBaseNonce);
        if (RxBaseNonce != null) CryptographicOperations.ZeroMemory(RxBaseNonce);
    }
}