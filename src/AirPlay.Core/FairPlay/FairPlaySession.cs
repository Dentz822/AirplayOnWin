using System.Runtime.InteropServices;

namespace AirPlay.Core.FairPlay;

/// <summary>
/// FairPlay SAP handshake (POST /fp-setup, twice) and decryption of the stream key ("ekey")
/// that the iPhone sends in SETUP. The math lives in playfair.dll (native\playfair).
/// </summary>
public sealed class FairPlaySession
{
    private byte[]? _keyMessage; // the 164-byte second fp-setup request, needed to decrypt ekey

    public bool Ready => _keyMessage is not null;

    /// <summary>Handles one POST /fp-setup body and returns the reply body.</summary>
    public byte[] Setup(ReadOnlySpan<byte> body)
    {
        var request = body.ToArray();
        switch (request.Length)
        {
            case 16:
            {
                if (request[4] != 0x03)
                    throw new FairPlayException($"unsupported FairPlay type 0x{request[4]:X2} (only 0x03 is known)");
                var reply = new byte[142];
                var rc = Native.fp_setup(request, reply);
                if (rc != 0) throw new FairPlayException($"fp_setup failed ({rc}), mode={request[14]}");
                _keyMessage = null;
                return reply;
            }
            case 164:
            {
                // Byte 12 is the key mode (0-3); playfair indexes 4-entry tables with it.
                if (request[4] != 0x03 || request[12] > 3)
                    throw new FairPlayException($"invalid FairPlay key message (type 0x{request[4]:X2}, mode {request[12]})");
                var reply = new byte[32];
                var rc = Native.fp_handshake(request, reply);
                if (rc != 0) throw new FairPlayException($"fp_handshake failed ({rc})");
                _keyMessage = request;
                return reply;
            }
            default:
                throw new FairPlayException($"unexpected fp-setup body length {request.Length}");
        }
    }

    /// <summary>Decrypts the 72-byte ekey from SETUP into the 16-byte AES key.</summary>
    public byte[] DecryptKey(ReadOnlySpan<byte> ekey)
    {
        if (_keyMessage is null) throw new FairPlayException("FairPlay handshake not completed");
        if (ekey.Length != 72) throw new FairPlayException($"ekey must be 72 bytes, got {ekey.Length}");
        var key = new byte[16];
        Native.fp_decrypt(_keyMessage, ekey.ToArray(), key);
        return key;
    }

    private static class Native
    {
        [DllImport("playfair.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int fp_setup(byte[] request16, byte[] reply142);

        [DllImport("playfair.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int fp_handshake(byte[] request164, byte[] reply32);

        [DllImport("playfair.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void fp_decrypt(byte[] keyMessage164, byte[] ekey72, byte[] key16);
    }
}

public sealed class FairPlayException(string message) : Exception(message);
