using AirPlay.Core.Crypto;
using AirPlay.Core.FairPlay;
using AirPlay.Core.Identity;
using AirPlay.Core.Pairing;

namespace AirPlay.Tests;

public class AesCtrTests
{
    // NIST SP 800-38A, F.5.1 CTR-AES128.Encrypt
    private static readonly byte[] Key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
    private static readonly byte[] Iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
    private static readonly byte[] Plain = Convert.FromHexString(
        "6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51");
    private static readonly byte[] Cipher = Convert.FromHexString(
        "874d6191b620e3261bef6864990db6ce9806f66b7970fdff8617187bb9fffdff");

    [Fact]
    public void MatchesNistVector()
    {
        using var ctr = new AesCtr(Key, Iv);
        Assert.Equal(Cipher, ctr.TransformCopy(Plain));
    }

    [Fact]
    public void KeystreamContinuesAcrossCalls()
    {
        using var ctr = new AesCtr(Key, Iv);
        var a = ctr.TransformCopy(Plain.AsSpan(0, 5));
        var b = ctr.TransformCopy(Plain.AsSpan(5, 20));
        var c = ctr.TransformCopy(Plain.AsSpan(25));
        Assert.Equal(Cipher, a.Concat(b).Concat(c).ToArray());
    }
}

public class PairingTests
{
    private static DeviceIdentity NewIdentity() =>
        DeviceIdentity.LoadOrCreate("Test", Path.Combine(Path.GetTempPath(), $"ap-id-{Guid.NewGuid()}.json"));

    [Fact]
    public void FullHandshakeSucceedsAndBothSidesShareSecret()
    {
        var server = new PairingSession(NewIdentity());
        var phone = new FakeIPhone();

        phone.OnPairSetupResponse(server.Setup(phone.PairSetupBody()));
        var step2 = phone.OnVerifyStep1Response(server.VerifyStart(phone.VerifyStep1Body()));
        server.VerifyFinish(step2);

        Assert.True(server.Verified);
        Assert.Equal(phone.SharedSecret, server.SharedSecret);
    }

    [Fact]
    public void RejectsBadClientSignature()
    {
        var server = new PairingSession(NewIdentity());
        var phone = new FakeIPhone();

        phone.OnPairSetupResponse(server.Setup(phone.PairSetupBody()));
        var step2 = phone.OnVerifyStep1Response(server.VerifyStart(phone.VerifyStep1Body()), corruptOurSignature: true);

        Assert.Throws<PairingException>(() => server.VerifyFinish(step2));
        Assert.False(server.Verified);
    }
}

public class FairPlayTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SetupReturnsReplyForMode(byte mode)
    {
        var fp = new FairPlaySession();
        var request = new byte[16];
        "FPLY"u8.CopyTo(request);
        request[4] = 0x03; request[5] = 0x01; request[6] = 0x01; request[14] = mode;

        var reply = fp.Setup(request);

        Assert.Equal(142, reply.Length);
        Assert.Equal("FPLY"u8.ToArray(), reply[..4]);
        Assert.Equal(mode, reply[13]);
    }

    [Fact]
    public void HandshakeEchoesLast20BytesAndEnablesDecrypt()
    {
        var fp = new FairPlaySession();
        var request = new byte[164];
        "FPLY"u8.CopyTo(request);
        request[4] = 0x03;
        for (var i = 144; i < 164; i++) request[i] = (byte)i;

        var reply = fp.Setup(request);

        Assert.Equal(32, reply.Length);
        Assert.Equal(request[144..], reply[12..]);
        Assert.True(fp.Ready);

        // playfair is deterministic: the same inputs give the same key.
        var ekey = Enumerable.Range(0, 72).Select(i => (byte)(i * 7)).ToArray();
        Assert.Equal(fp.DecryptKey(ekey), fp.DecryptKey(ekey));
    }

    [Fact]
    public void RejectsUnknownFairPlayType()
    {
        var request = new byte[16];
        request[4] = 0x02;
        Assert.Throws<FairPlayException>(() => new FairPlaySession().Setup(request));
    }
}
