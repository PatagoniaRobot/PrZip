// Port a C# de unrar 7.3.1 (RARLAB), de crypt.cpp, crypt1.cpp, crypt2.cpp, crypt3.cpp, crypt5.cpp y
// sha1.cpp (este último basado en el SHA-1 de dominio público de Steve Reid). Copyright (c) Alexander L.
// Roshal. AES, HMAC-SHA256 y SHA-256 son las primitivas de .NET; de unrar se toma cómo se usan.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace PrZip.Rar;

/// <summary>Claves ya derivadas, compartidas por todas las entradas de un archivo (KDF3Cache/KDF5Cache).</summary>
internal sealed class RarKeyCache
{
    public readonly Dictionary<string, (byte[] Key, byte[] Init)> Kdf3 = new(StringComparer.Ordinal);
    public readonly Dictionary<string, (byte[] Key, byte[] HashKey, byte[] PswCheck)> Kdf5 = new(StringComparer.Ordinal);
}

/// <summary>Descifrado de RAR (CryptData de unrar), para cabeceras y datos.</summary>
internal sealed class RarCryptData : IBlockDecryptor, IDisposable
{
    private const int MaxPasswordRar = 128;
    private const int HashRounds3 = 0x40000;

    private readonly RarKeyCache _cache;
    private CryptMethod _method;
    private Aes? _aes;
    private ICryptoTransform? _decryptor;
    private byte[] _work = new byte[4096];

    private readonly uint[] _crcTab = BuildCrcTab();
    private readonly byte[] _key13 = new byte[3];
    private readonly ushort[] _key15 = new ushort[4];
    private readonly uint[] _key20 = new uint[4];
    private readonly byte[] _substTable20 = new byte[256];

    public RarCryptData(RarKeyCache cache)
    {
        _cache = cache;
    }

    private static uint[] BuildCrcTab()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320 : c >> 1;
            }

            t[i] = c;
        }

        return t;
    }

    /// <summary>
    /// Prepara las claves (SetCryptKeys). Para RAR5 devuelve además la clave de las sumas "MAC" y el valor de
    /// chequeo de la contraseña. <paramref name="initV"/> null sirve para obtener solo el chequeo.
    /// </summary>
    public bool SetCryptKeys(CryptMethod method, string password, byte[]? salt, byte[]? initV, uint lg2Count,
        byte[]? hashKey, byte[]? pswCheck)
    {
        if (method == CryptMethod.None || string.IsNullOrEmpty(password))
        {
            return false;
        }

        _method = method;

        // Como unrar: la contraseña se corta en 127 caracteres por compatibilidad con archivos existentes.
        string pwdW = password.Length >= MaxPasswordRar ? password[..(MaxPasswordRar - 1)] : password;
        switch (method)
        {
            case CryptMethod.Rar13:
                SetKey13(PasswordA(pwdW));
                return true;
            case CryptMethod.Rar15:
                SetKey15(PasswordA(pwdW));
                return true;
            case CryptMethod.Rar20:
                SetKey20(PasswordA(pwdW));
                return true;
            case CryptMethod.Rar30:
                SetKey30(pwdW, salt);
                return true;
            case CryptMethod.Rar50:
                return SetKey50(pwdW, salt!, initV, lg2Count, hashKey, pswCheck);
            default:
                return false;
        }
    }

    /// <summary>Contraseña en la página ANSI (WideToChar), cortada a 127 bytes, para los cifrados viejos.</summary>
    private static byte[] PasswordA(string pwdW)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding ansi;
        try
        {
            ansi = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            ansi = Encoding.GetEncoding(1252);
        }

        var bytes = ansi.GetBytes(pwdW);
        return bytes.Length >= MaxPasswordRar ? bytes[..(MaxPasswordRar - 1)] : bytes;
    }

    public void DecryptBlock(Span<byte> data)
    {
        switch (_method)
        {
            case CryptMethod.Rar13:
                Decrypt13(data);
                break;
            case CryptMethod.Rar15:
                Crypt15(data);
                break;
            case CryptMethod.Rar20:
                for (int i = 0; i + RawRead.CryptBlockSize <= data.Length; i += RawRead.CryptBlockSize)
                {
                    DecryptBlock20(data.Slice(i, RawRead.CryptBlockSize));
                }

                break;
            case CryptMethod.Rar30:
            case CryptMethod.Rar50:
                {
                    int len = data.Length & ~(RawRead.CryptBlockSize - 1);
                    if (len == 0)
                    {
                        break;
                    }

                    if (_work.Length < len)
                    {
                        _work = new byte[len];
                    }

                    data[..len].CopyTo(_work);
                    _decryptor!.TransformBlock(_work, 0, len, _work, 0);
                    _work.AsSpan(0, len).CopyTo(data);
                }

                break;
        }
    }

    private void InitAes(byte[] key, byte[] iv)
    {
        _decryptor?.Dispose();
        _aes?.Dispose();
        _aes = Aes.Create();
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.None;
        _aes.Key = key;
        _aes.IV = iv;
        _decryptor = _aes.CreateDecryptor();
    }

    // ---------------- RAR 3.x: AES-128 con derivación SHA-1 propia ----------------

    private void SetKey30(string pwdW, byte[]? salt)
    {
        string cacheKey = pwdW + "\0" + (salt == null ? "-" : Convert.ToHexString(salt));
        if (!_cache.Kdf3.TryGetValue(cacheKey, out var cached))
        {
            var rawPsw = new byte[2 * pwdW.Length + (salt != null ? RarConst.SizeSalt30 : 0)];
            for (int i = 0; i < pwdW.Length; i++)
            {
                rawPsw[2 * i] = (byte)pwdW[i];
                rawPsw[2 * i + 1] = (byte)(pwdW[i] >> 8);
            }

            salt?.AsSpan(0, RarConst.SizeSalt30).CopyTo(rawPsw.AsSpan(2 * pwdW.Length));

            var aesInit = new byte[16];
            var c = new Sha1Rar();
            Span<byte> pswNum = stackalloc byte[3];
            Span<uint> digest = stackalloc uint[5];
            for (int i = 0; i < HashRounds3; i++)
            {
                c.ProcessRar29(rawPsw);
                pswNum[0] = (byte)i;
                pswNum[1] = (byte)(i >> 8);
                pswNum[2] = (byte)(i >> 16);
                c.Process(pswNum);
                if (i % (HashRounds3 / 16) == 0)
                {
                    var temp = c.Clone();
                    temp.Done(digest);
                    aesInit[i / (HashRounds3 / 16)] = (byte)digest[4];
                }
            }

            c.Done(digest);
            var aesKey = new byte[16];
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    aesKey[i * 4 + j] = (byte)(digest[i] >> (j * 8));
                }
            }

            CryptographicOperations.ZeroMemory(rawPsw);
            cached = (aesKey, aesInit);
            _cache.Kdf3[cacheKey] = cached;
        }

        InitAes(cached.Key, cached.Init);
    }

    // ---------------- RAR 5.0: PBKDF2-HMAC-SHA256 + AES-256 ----------------

    private bool SetKey50(string pwdW, byte[] salt, byte[]? initV, uint lg2Count, byte[]? hashKey, byte[]? pswCheck)
    {
        if (lg2Count > RarConst.Crypt5KdfLg2CountMax)
        {
            return false;
        }

        string cacheKey = pwdW + "\0" + Convert.ToHexString(salt) + "\0" + lg2Count.ToString(CultureInfo.InvariantCulture);
        if (!_cache.Kdf5.TryGetValue(cacheKey, out var cached))
        {
            byte[] pwdUtf = Encoding.UTF8.GetBytes(pwdW);
            cached = Pbkdf2(pwdUtf, salt, 1u << (int)lg2Count);
            CryptographicOperations.ZeroMemory(pwdUtf);
            _cache.Kdf5[cacheKey] = cached;
        }

        if (hashKey != null)
        {
            cached.HashKey.CopyTo(hashKey, 0);
        }

        if (pswCheck != null)
        {
            Array.Clear(pswCheck);
            for (int i = 0; i < cached.PswCheck.Length; i++)
            {
                pswCheck[i % RarConst.SizePswCheck] ^= cached.PswCheck[i];
            }
        }

        if (initV != null)
        {
            InitAes(cached.Key, initV);
        }

        return true;
    }

    /// <summary>
    /// PBKDF2-HMAC-SHA256 de 32 bytes, más los valores tras 16 y 32 iteraciones extra (clave de sumas y
    /// chequeo de contraseña), como pbkdf2() de unrar.
    /// </summary>
    private static (byte[] Key, byte[] HashKey, byte[] PswCheck) Pbkdf2(byte[] pwd, byte[] salt, uint count)
    {
        using var hmac = new HMACSHA256(pwd);
        var saltData = new byte[salt.Length + 4];
        salt.CopyTo(saltData, 0);
        saltData[^1] = 1;

        var u = hmac.ComputeHash(saltData);
        var fn = (byte[])u.Clone();
        var results = new byte[3][];
        uint[] curCount = [count - 1, 16, 16];
        Span<byte> u2 = stackalloc byte[32];
        for (int i = 0; i < 3; i++)
        {
            for (uint j = 0; j < curCount[i]; j++)
            {
                hmac.TryComputeHash(u, u2, out _);
                u2.CopyTo(u);
                for (int k = 0; k < fn.Length; k++)
                {
                    fn[k] ^= u[k];
                }
            }

            results[i] = (byte[])fn.Clone();
        }

        return (results[0], results[1], results[2]);
    }

    // ---------------- RAR 1.3, 1.5 y 2.0 ----------------

    /// <summary>Clave fija con la que RAR 1.4 cifra su comentario comprimido (SetCmt13Encryption).</summary>
    public void SetCmt13Encryption()
    {
        _method = CryptMethod.Rar13;
        _key13[0] = 0;
        _key13[1] = 7;
        _key13[2] = 77;
    }

    private void SetKey13(byte[] password)
    {
        _key13[0] = _key13[1] = _key13[2] = 0;
        foreach (byte p in password)
        {
            _key13[0] += p;
            _key13[1] ^= p;
            _key13[2] += p;
            _key13[2] = (byte)((_key13[2] << 1) | (_key13[2] >> 7));
        }
    }

    private void SetKey15(byte[] password)
    {
        uint pswCrc = ~PrZip.Primitives.Checksum.Crc32Algorithm.Compute(password);
        _key15[0] = (ushort)(pswCrc & 0xffff);
        _key15[1] = (ushort)((pswCrc >> 16) & 0xffff);
        _key15[2] = _key15[3] = 0;
        foreach (byte p in password)
        {
            _key15[2] ^= (ushort)(p ^ _crcTab[p]);
            _key15[3] += (ushort)(p + (_crcTab[p] >> 16));
        }
    }

    private void Decrypt13(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            _key13[1] += _key13[2];
            _key13[0] += _key13[1];
            data[i] -= _key13[0];
        }
    }

    private void Crypt15(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            _key15[0] += 0x1234;
            _key15[1] ^= (ushort)_crcTab[(_key15[0] & 0x1fe) >> 1];
            _key15[2] -= (ushort)(_crcTab[(_key15[0] & 0x1fe) >> 1] >> 16);
            _key15[0] ^= _key15[2];
            _key15[3] = (ushort)(RotR16(_key15[3]) ^ _key15[1]);
            _key15[3] = RotR16(_key15[3]);
            _key15[0] ^= _key15[3];
            data[i] ^= (byte)(_key15[0] >> 8);
        }
    }

    private static ushort RotR16(ushort x) => (ushort)((x >> 1) | (x << 15));

    private static ReadOnlySpan<byte> InitSubstTable20 =>
    [
        215, 19, 149, 35, 73, 197, 192, 205, 249, 28, 16, 119, 48, 221, 2, 42,
        232, 1, 177, 233, 14, 88, 219, 25, 223, 195, 244, 90, 87, 239, 153, 137,
        255, 199, 147, 70, 92, 66, 246, 13, 216, 40, 62, 29, 217, 230, 86, 6,
        71, 24, 171, 196, 101, 113, 218, 123, 93, 91, 163, 178, 202, 67, 44, 235,
        107, 250, 75, 234, 49, 167, 125, 211, 83, 114, 157, 144, 32, 193, 143, 36,
        158, 124, 247, 187, 89, 214, 141, 47, 121, 228, 61, 130, 213, 194, 174, 251,
        97, 110, 54, 229, 115, 57, 152, 94, 105, 243, 212, 55, 209, 245, 63, 11,
        164, 200, 31, 156, 81, 176, 227, 21, 76, 99, 139, 188, 127, 17, 248, 51,
        207, 120, 189, 210, 8, 226, 41, 72, 183, 203, 135, 165, 166, 60, 98, 7,
        122, 38, 155, 170, 69, 172, 252, 238, 39, 134, 59, 128, 236, 27, 240, 80,
        131, 3, 85, 206, 145, 79, 154, 142, 159, 220, 201, 133, 74, 64, 20, 129,
        224, 185, 138, 103, 173, 182, 43, 34, 254, 82, 198, 151, 231, 180, 58, 10,
        118, 26, 102, 12, 50, 132, 22, 191, 136, 111, 162, 179, 45, 4, 148, 108,
        161, 56, 78, 126, 242, 222, 15, 175, 146, 23, 33, 241, 181, 190, 77, 225,
        0, 46, 169, 186, 68, 95, 237, 65, 53, 208, 253, 168, 9, 18, 100, 52,
        116, 184, 160, 96, 109, 37, 30, 106, 140, 104, 150, 5, 204, 117, 112, 84,
    ];

    private void SetKey20(byte[] password)
    {
        int pswLength = password.Length;
        // Copia con espacio para el relleno del último bloque y el cero final que unrar lee de más.
        var psw = new byte[(pswLength | (RawRead.CryptBlockSize - 1)) + 2];
        password.CopyTo(psw, 0);

        _key20[0] = 0xD3A3B879;
        _key20[1] = 0x3F6D12F7;
        _key20[2] = 0x7515A235;
        _key20[3] = 0xA4E7F123;
        InitSubstTable20.CopyTo(_substTable20);
        for (uint j = 0; j < 256; j++)
        {
            for (int i = 0; i < pswLength; i += 2)
            {
                uint n1 = (byte)_crcTab[(psw[i] - j) & 0xff];
                uint n2 = (byte)_crcTab[(psw[i + 1] + j) & 0xff];
                for (int k = 1; n1 != n2; n1 = (n1 + 1) & 0xff, k++)
                {
                    (_substTable20[n1], _substTable20[(n1 + i + k) & 0xff]) = (_substTable20[(n1 + i + k) & 0xff], _substTable20[n1]);
                }
            }
        }

        for (int i = 0; i < pswLength; i += RawRead.CryptBlockSize)
        {
            EncryptBlock20(psw.AsSpan(i, RawRead.CryptBlockSize));
        }

        CryptographicOperations.ZeroMemory(psw);
    }

    private uint SubstLong(uint t) =>
        _substTable20[t & 255] | ((uint)_substTable20[(t >> 8) & 255] << 8) |
        ((uint)_substTable20[(t >> 16) & 255] << 16) | ((uint)_substTable20[(t >> 24) & 255] << 24);

    private void EncryptBlock20(Span<byte> buf)
    {
        uint a = BinaryPrimitives.ReadUInt32LittleEndian(buf) ^ _key20[0];
        uint b = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]) ^ _key20[1];
        uint c = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..]) ^ _key20[2];
        uint d = BinaryPrimitives.ReadUInt32LittleEndian(buf[12..]) ^ _key20[3];
        for (int i = 0; i < 32; i++)
        {
            uint t = (c + BitOperations.RotateLeft(d, 11)) ^ _key20[i & 3];
            uint ta = a ^ SubstLong(t);
            t = (d ^ BitOperations.RotateLeft(c, 17)) + _key20[i & 3];
            uint tb = b ^ SubstLong(t);
            a = c;
            b = d;
            c = ta;
            d = tb;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buf, c ^ _key20[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[4..], d ^ _key20[1]);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[8..], a ^ _key20[2]);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[12..], b ^ _key20[3]);
        UpdKeys20(buf);
    }

    private void DecryptBlock20(Span<byte> buf)
    {
        Span<byte> inBuf = stackalloc byte[16];
        uint a = BinaryPrimitives.ReadUInt32LittleEndian(buf) ^ _key20[0];
        uint b = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]) ^ _key20[1];
        uint c = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..]) ^ _key20[2];
        uint d = BinaryPrimitives.ReadUInt32LittleEndian(buf[12..]) ^ _key20[3];
        buf[..16].CopyTo(inBuf);
        for (int i = 31; i >= 0; i--)
        {
            uint t = (c + BitOperations.RotateLeft(d, 11)) ^ _key20[i & 3];
            uint ta = a ^ SubstLong(t);
            t = (d ^ BitOperations.RotateLeft(c, 17)) + _key20[i & 3];
            uint tb = b ^ SubstLong(t);
            a = c;
            b = d;
            c = ta;
            d = tb;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buf, c ^ _key20[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[4..], d ^ _key20[1]);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[8..], a ^ _key20[2]);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[12..], b ^ _key20[3]);
        UpdKeys20(inBuf);
    }

    private void UpdKeys20(ReadOnlySpan<byte> buf)
    {
        for (int i = 0; i < 16; i += 4)
        {
            _key20[0] ^= _crcTab[buf[i]];
            _key20[1] ^= _crcTab[buf[i + 1]];
            _key20[2] ^= _crcTab[buf[i + 2]];
            _key20[3] ^= _crcTab[buf[i + 3]];
        }
    }

    public void Dispose()
    {
        _decryptor?.Dispose();
        _aes?.Dispose();
    }
}

/// <summary>
/// SHA-1 de unrar (sha1.cpp, basado en el de dominio público de Steve Reid), con la variante
/// sha1_process_rar29 que reescribe los bloques de entrada completos con el estado expandido: RAR 3.x la usa
/// al derivar la clave y hay que reproducirla tal cual con contraseñas largas.
/// </summary>
internal sealed class Sha1Rar
{
    private readonly uint[] _state = new uint[5];
    private readonly byte[] _buffer = new byte[64];
    private ulong _count;

    public Sha1Rar()
    {
        Init();
    }

    private void Init()
    {
        _count = 0;
        _state[0] = 0x67452301;
        _state[1] = 0xEFCDAB89;
        _state[2] = 0x98BADCFE;
        _state[3] = 0x10325476;
        _state[4] = 0xC3D2E1F0;
    }

    public Sha1Rar Clone()
    {
        var c = new Sha1Rar();
        _state.CopyTo(c._state, 0);
        _buffer.CopyTo(c._buffer, 0);
        c._count = _count;
        return c;
    }

    /// <summary>Transforma un bloque de 64 bytes; <paramref name="w"/> termina con el estado expandido.</summary>
    private void Transform(ReadOnlySpan<byte> block, Span<uint> w)
    {
        for (int i = 0; i < 16; i++)
        {
            w[i] = BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);
        }

        uint a = _state[0], b = _state[1], c = _state[2], d = _state[3], e = _state[4];
        for (int i = 0; i < 80; i++)
        {
            uint wi;
            if (i < 16)
            {
                wi = w[i];
            }
            else
            {
                wi = BitOperations.RotateLeft(w[(i + 13) & 15] ^ w[(i + 8) & 15] ^ w[(i + 2) & 15] ^ w[i & 15], 1);
                w[i & 15] = wi;
            }

            uint f, k;
            if (i < 20)
            {
                f = (b & (c ^ d)) ^ d;
                k = 0x5A827999;
            }
            else if (i < 40)
            {
                f = b ^ c ^ d;
                k = 0x6ED9EBA1;
            }
            else if (i < 60)
            {
                f = ((b | c) & d) | (b & c);
                k = 0x8F1BBCDC;
            }
            else
            {
                f = b ^ c ^ d;
                k = 0xCA62C1D6;
            }

            uint temp = BitOperations.RotateLeft(a, 5) + f + e + k + wi;
            e = d;
            d = c;
            c = BitOperations.RotateLeft(b, 30);
            b = a;
            a = temp;
        }

        _state[0] += a;
        _state[1] += b;
        _state[2] += c;
        _state[3] += d;
        _state[4] += e;
    }

    public void Process(ReadOnlySpan<byte> data) => ProcessCore(data, null);

    /// <summary>Como sha1_process_rar29: los bloques completos tomados de <paramref name="data"/> se reescriben.</summary>
    public void ProcessRar29(byte[] data) => ProcessCore(data, data);

    private void ProcessCore(ReadOnlySpan<byte> data, byte[]? writeBack)
    {
        Span<uint> w = stackalloc uint[16];
        int len = data.Length;
        int j = (int)(_count & 63);
        _count += (ulong)len;
        int i;
        if (j + len > 63)
        {
            i = 64 - j;
            data[..i].CopyTo(_buffer.AsSpan(j));
            Transform(_buffer, w);
            for (; i + 63 < len; i += 64)
            {
                Transform(data.Slice(i, 64), w);
                if (writeBack != null)
                {
                    for (int k = 0; k < 16; k++)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(writeBack.AsSpan(i + k * 4), w[k]);
                    }
                }
            }

            j = 0;
        }
        else
        {
            i = 0;
        }

        if (len > i)
        {
            data[i..].CopyTo(_buffer.AsSpan(j));
        }
    }

    public void Done(Span<uint> digest)
    {
        Span<uint> w = stackalloc uint[16];
        ulong bitLength = _count * 8;
        int bufPos = (int)(_count & 0x3f);
        _buffer[bufPos++] = 0x80;
        if (bufPos != 56)
        {
            if (bufPos > 56)
            {
                while (bufPos < 64)
                {
                    _buffer[bufPos++] = 0;
                }

                bufPos = 0;
            }

            if (bufPos == 0)
            {
                Transform(_buffer, w);
            }

            _buffer.AsSpan(bufPos, 56 - bufPos).Clear();
        }

        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(56), (uint)(bitLength >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(60), (uint)bitLength);
        Transform(_buffer, w);
        for (int i = 0; i < 5; i++)
        {
            digest[i] = _state[i];
        }

        Init();
    }
}
