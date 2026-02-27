using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;

namespace SmallFile.Core.Crypto;

internal sealed class AesGcmSession : IDisposable
{
    private const int TagSize = 16;
    private const int NonceSize = 12;

    private readonly AesGcm _txAes;
    private readonly AesGcm _rxAes;

    private readonly byte[] _txBaseNonce;
    private readonly byte[] _rxBaseNonce;

    private ulong _txSequence = 0;
    private ulong _rxSequence = 0;

    public AesGcmSession(SessionCrypto crypto)
    {
        if (crypto.TxKey == null || crypto.RxKey == null || 
            crypto.TxBaseNonce == null || crypto.RxBaseNonce == null)
            throw new InvalidOperationException("Crypto material is not initialized.");

        _txAes = new AesGcm(crypto.TxKey, TagSize);
        _rxAes = new AesGcm(crypto.RxKey, TagSize);

        // Enforce strict memory ownership by cloning the base nonces
        _txBaseNonce = crypto.TxBaseNonce.ToArray();
        _rxBaseNonce = crypto.RxBaseNonce.ToArray();
    }

    public byte[] Encrypt(byte[] plaintext, byte[]? associatedData = null)
    {
        if (_txSequence == ulong.MaxValue)
            throw new CryptographicException("Sequence counter exhausted. Session must terminate.");

        byte[] output = new byte[plaintext.Length + TagSize];
        
        Span<byte> ciphertextSpan = output.AsSpan(0, plaintext.Length);
        Span<byte> tagSpan = output.AsSpan(plaintext.Length, TagSize);
        Span<byte> nonceSpan = stackalloc byte[NonceSize];

        ComputeNonce(_txBaseNonce, _txSequence, nonceSpan);

        _txAes.Encrypt(nonceSpan, plaintext, ciphertextSpan, tagSpan, associatedData);

        _txSequence++;
        return output;
    }

    public byte[] Decrypt(byte[] encryptedPayload, byte[]? associatedData = null)
    {
        if (encryptedPayload.Length < TagSize)
            throw new CryptographicException("Payload too small to contain an authentication tag.");

        if (_rxSequence == ulong.MaxValue)
            throw new CryptographicException("Sequence counter exhausted. Session must terminate.");

        int ciphertextLength = encryptedPayload.Length - TagSize;
        
        ReadOnlySpan<byte> ciphertextSpan = encryptedPayload.AsSpan(0, ciphertextLength);
        ReadOnlySpan<byte> tagSpan = encryptedPayload.AsSpan(ciphertextLength, TagSize);
        
        byte[] plaintext = new byte[ciphertextLength];
        Span<byte> nonceSpan = stackalloc byte[NonceSize];

        ComputeNonce(_rxBaseNonce, _rxSequence, nonceSpan);

        try
        {
            _rxAes.Decrypt(nonceSpan, ciphertextSpan, tagSpan, plaintext, associatedData);
        }
        catch (CryptographicException ex)
        {
            // Strict memory hygiene: wipe partial plaintext before throwing
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException("AES-GCM Authentication failed. Possible MITM, corruption, or replay attack.", ex);
        }

        _rxSequence++;
        return plaintext;
    }

    private static void ComputeNonce(byte[] baseNonce, ulong sequence, Span<byte> outputNonce)
    {
        baseNonce.CopyTo(outputNonce);

        Span<byte> seqBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seqBytes, sequence);

        for (int i = 0; i < 8; i++)
        {
            outputNonce[4 + i] ^= seqBytes[i];
        }
    }

    public void Dispose()
    {
        _txAes.Dispose();
        _rxAes.Dispose();
        CryptographicOperations.ZeroMemory(_txBaseNonce);
        CryptographicOperations.ZeroMemory(_rxBaseNonce);
    }
}