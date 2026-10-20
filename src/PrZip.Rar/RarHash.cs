// Port a C# de unrar 7.3.1 (RARLAB), de hash.cpp y crc.cpp (Checksum14). Copyright (c) Alexander L. Roshal.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

using PrZip.Primitives.Checksum;

namespace PrZip.Rar;

/// <summary>Suma de los datos descomprimidos (DataHash de unrar): CRC32, la de RAR 1.4 o BLAKE2sp.</summary>
internal sealed class RarHash
{
    private readonly HashType _type;
    private uint _crc;
    private readonly Blake2sp? _blake2;

    public RarHash(HashType type)
    {
        _type = type;
        _crc = 0;
        if (type == HashType.Blake2)
        {
            _blake2 = new Blake2sp();
        }
    }

    public HashType Type => _type;

    public void Update(ReadOnlySpan<byte> data)
    {
        switch (_type)
        {
            case HashType.Rar14:
                _crc = Checksum14((ushort)_crc, data);
                break;
            case HashType.Crc32:
                _crc = Crc32Algorithm.Update(_crc, data);
                break;
            case HashType.Blake2:
                _blake2!.Update(data);
                break;
        }
    }

    /// <summary>
    /// Compara con la suma de la cabecera. Con <paramref name="hashKey"/> (RAR5 cifrado con "MAC") la suma
    /// se convierte antes con HMAC-SHA256, como ConvertHashToMAC.
    /// </summary>
    public bool Matches(RarFileHeader hd, byte[]? hashKey)
    {
        if (_type == HashType.None || hd.HashType == HashType.None)
        {
            return true;
        }

        if (_type != hd.HashType)
        {
            return false;
        }

        if (_type is HashType.Rar14 or HashType.Crc32)
        {
            uint value = _crc;
            if (hashKey != null && _type == HashType.Crc32)
            {
                Span<byte> raw = stackalloc byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(raw, value);
                var digest = System.Security.Cryptography.HMACSHA256.HashData(hashKey, raw);
                value = 0;
                for (int i = 0; i < digest.Length; i++)
                {
                    value ^= (uint)digest[i] << ((i & 3) * 8);
                }
            }

            return value == hd.Crc32;
        }

        var final = _blake2!.Final();
        if (hashKey != null)
        {
            final = System.Security.Cryptography.HMACSHA256.HashData(hashKey, final);
        }

        return final.AsSpan().SequenceEqual(hd.Digest);
    }

    private static uint Checksum14(ushort startCrc, ReadOnlySpan<byte> data)
    {
        uint crc = startCrc;
        foreach (byte b in data)
        {
            crc = (crc + b) & 0xffff;
            crc = ((crc << 1) | (crc >> 15)) & 0xffff;
        }

        return crc;
    }
}
