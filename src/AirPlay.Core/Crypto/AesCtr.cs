using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AirPlay.Core.Crypto;

/// <summary>
/// AES-128-CTR as a continuous stream (like OpenSSL's EVP CTR mode): the 16-byte counter is a
/// big-endian 128-bit number, and the keystream position carries over between calls.
/// Encrypt and decrypt are the same operation.
/// </summary>
public sealed class AesCtr : IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _keystream = new byte[16];
    private int _used = 16; // bytes of _keystream already consumed

    public AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        _aes = Aes.Create();
        _aes.Key = key.ToArray();
        iv.CopyTo(_counter);
    }

    public void Transform(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (_used == 16)
            {
                _aes.EncryptEcb(_counter, _keystream, PaddingMode.None);
                Increment();
                _used = 0;
            }
            data[i] ^= _keystream[_used++];
        }
    }

    public byte[] TransformCopy(ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        Transform(copy.AsSpan());
        return copy;
    }

    private void Increment()
    {
        var hi = BinaryPrimitives.ReadUInt64BigEndian(_counter);
        var lo = BinaryPrimitives.ReadUInt64BigEndian(_counter.AsSpan(8));
        lo++;
        if (lo == 0) hi++;
        BinaryPrimitives.WriteUInt64BigEndian(_counter, hi);
        BinaryPrimitives.WriteUInt64BigEndian(_counter.AsSpan(8), lo);
    }

    public void Dispose() => _aes.Dispose();
}
