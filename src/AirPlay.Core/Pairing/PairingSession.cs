using System.Security.Cryptography;
using System.Text;
using AirPlay.Core.Crypto;
using AirPlay.Core.Identity;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace AirPlay.Core.Pairing;

/// <summary>
/// Legacy AirPlay pairing (no PIN), one per connection:
///   pair-setup : iPhone sends its Ed25519 public key, we answer with ours.
///   pair-verify: X25519 key exchange; each side signs both X25519 keys with Ed25519 and
///                sends the signature AES-CTR encrypted with a key derived from the shared secret.
/// The X25519 shared secret is needed later to derive the media stream keys.
/// </summary>
public sealed class PairingSession
{
    private readonly DeviceIdentity _identity;
    private byte[]? _ourEcdhPublic;
    private byte[]? _theirEcdhPublic;
    private byte[]? _theirEdPublic;

    public PairingSession(DeviceIdentity identity) => _identity = identity;

    public bool SetupDone { get; private set; }
    public bool Verified { get; private set; }
    public byte[]? SharedSecret { get; private set; }

    /// <summary>POST /pair-setup: 32-byte client Ed25519 key in, our Ed25519 public key out.</summary>
    public byte[] Setup(ReadOnlySpan<byte> body)
    {
        if (body.Length != 32)
            throw new PairingException($"pair-setup body must be 32 bytes, got {body.Length}");
        SetupDone = true;
        return _identity.Ed25519PublicKey;
    }

    /// <summary>
    /// POST /pair-verify step 1: body = 01 00 00 00 | client X25519 (32) | client Ed25519 (32).
    /// Returns our X25519 public key (32) | encrypted signature (64).
    /// </summary>
    public byte[] VerifyStart(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4 + 32 + 32)
            throw new PairingException($"pair-verify step 1 body must be 68 bytes, got {body.Length}");

        _theirEcdhPublic = body.Slice(4, 32).ToArray();
        _theirEdPublic = body.Slice(36, 32).ToArray();

        var ours = new X25519PrivateKeyParameters(new SecureRandom());
        _ourEcdhPublic = ours.GeneratePublicKey().GetEncoded();
        SharedSecret = new byte[32];
        ours.GenerateSecret(new X25519PublicKeyParameters(_theirEcdhPublic), SharedSecret, 0);

        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(_identity.Ed25519PrivateKey));
        var message = Concat(_ourEcdhPublic, _theirEcdhPublic);
        signer.BlockUpdate(message, 0, message.Length);
        var signature = signer.GenerateSignature();

        using var ctr = CreateCipher();
        ctr.Transform(signature.AsSpan());
        return Concat(_ourEcdhPublic, signature);
    }

    /// <summary>
    /// POST /pair-verify step 2: body = 00 00 00 00 | encrypted client signature (64).
    /// The client's ciphertext continues the same CTR keystream after our 64 bytes.
    /// </summary>
    public void VerifyFinish(ReadOnlySpan<byte> body)
    {
        if (_ourEcdhPublic is null || _theirEcdhPublic is null || _theirEdPublic is null)
            throw new PairingException("pair-verify step 2 received before step 1");
        if (body.Length != 4 + 64)
            throw new PairingException($"pair-verify step 2 body must be 68 bytes, got {body.Length}");

        using var ctr = CreateCipher();
        ctr.Transform(new byte[64].AsSpan()); // skip the keystream we used for our own signature
        var signature = ctr.TransformCopy(body.Slice(4, 64));

        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(_theirEdPublic));
        var message = Concat(_theirEcdhPublic, _ourEcdhPublic);
        verifier.BlockUpdate(message, 0, message.Length);
        if (!verifier.VerifySignature(signature))
            throw new PairingException("pair-verify: client signature is invalid");
        Verified = true;
    }

    private AesCtr CreateCipher()
    {
        var key = Derive("Pair-Verify-AES-Key");
        var iv = Derive("Pair-Verify-AES-IV");
        return new AesCtr(key, iv);
    }

    private byte[] Derive(string salt) =>
        SHA512.HashData(Concat(Encoding.ASCII.GetBytes(salt), SharedSecret!))[..16];

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }
}

public sealed class PairingException(string message) : Exception(message);
