using System.Security.Cryptography;
using System.Text;
using AirPlay.Core.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace AirPlay.Tests;

/// <summary>Plays the iPhone's side of legacy pair-setup / pair-verify.</summary>
internal sealed class FakeIPhone
{
    private readonly Ed25519PrivateKeyParameters _ed = new(new SecureRandom());
    private readonly X25519PrivateKeyParameters _ecdh = new(new SecureRandom());
    private byte[]? _serverEd;
    private byte[]? _serverEcdh;
    private byte[]? _shared;

    public byte[] EdPublic => _ed.GeneratePublicKey().GetEncoded();
    public byte[] EcdhPublic => _ecdh.GeneratePublicKey().GetEncoded();
    public byte[]? SharedSecret => _shared;

    public byte[] PairSetupBody() => EdPublic;

    public void OnPairSetupResponse(byte[] body) => _serverEd = body;

    public byte[] VerifyStep1Body() => [1, 0, 0, 0, .. EcdhPublic, .. EdPublic];

    /// <summary>Checks the receiver's signature; returns the step-2 body.</summary>
    public byte[] OnVerifyStep1Response(byte[] body, bool corruptOurSignature = false)
    {
        Assert.Equal(96, body.Length);
        _serverEcdh = body[..32];
        _shared = new byte[32];
        _ecdh.GenerateSecret(new X25519PublicKeyParameters(_serverEcdh), _shared, 0);

        using var ctr = new AesCtr(Derive("Pair-Verify-AES-Key"), Derive("Pair-Verify-AES-IV"));
        var serverSig = ctr.TransformCopy(body.AsSpan(32, 64));
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(_serverEd!));
        byte[] signed = [.. _serverEcdh, .. EcdhPublic];
        verifier.BlockUpdate(signed, 0, signed.Length);
        Assert.True(verifier.VerifySignature(serverSig), "receiver signature should verify");

        var signer = new Ed25519Signer();
        signer.Init(true, _ed);
        byte[] ours = [.. EcdhPublic, .. _serverEcdh];
        signer.BlockUpdate(ours, 0, ours.Length);
        var sig = signer.GenerateSignature();
        if (corruptOurSignature) sig[0] ^= 0xFF;
        ctr.Transform(sig.AsSpan()); // same CTR stream continues
        return [0, 0, 0, 0, .. sig];
    }

    private byte[] Derive(string salt) =>
        SHA512.HashData([.. Encoding.ASCII.GetBytes(salt), .. _shared!])[..16];
}
