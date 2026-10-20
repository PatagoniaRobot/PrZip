// Port a C# de unrar 7.3.1 (RARLAB), de rawread.cpp. Copyright (c) Alexander L. Roshal.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

using PrZip.Primitives.Checksum;

namespace PrZip.Rar;

/// <summary>Descifrado por bloques de 16 bytes de las cabeceras cifradas.</summary>
internal interface IBlockDecryptor
{
    void DecryptBlock(Span<byte> data);
}

/// <summary>
/// Buffer de una cabecera: se llena leyendo del archivo (descifrando si hace falta) y se consume campo a
/// campo. Como en unrar, leer más allá del final devuelve ceros en lugar de fallar: los límites se
/// controlan después con los tamaños declarados y el CRC de la cabecera.
/// </summary>
internal sealed class RawRead
{
    public const int CryptBlockSize = 16;
    private const int CryptBlockMask = CryptBlockSize - 1;

    private readonly Stream? _src;
    private byte[] _data = new byte[64];
    private int _fullSize;   // bytes en _data, incluidos los leídos de más para alinear el descifrado
    private int _dataSize;   // bytes válidos de la cabecera
    private int _readPos;
    private IBlockDecryptor? _crypt;

    public RawRead(Stream? src = null)
    {
        _src = src;
    }

    public int Size => _dataSize;

    public int Position
    {
        get => _readPos;
        set => _readPos = value;
    }

    public int DataLeft => _dataSize - _readPos;

    public void SetCrypt(IBlockDecryptor? crypt) => _crypt = crypt;

    private void EnsureCapacity(int size)
    {
        if (_data.Length < size)
        {
            Array.Resize(ref _data, Math.Max(size, _data.Length * 2));
        }
    }

    /// <summary>Lee <paramref name="size"/> bytes más del archivo. Devuelve cuántos se leyeron.</summary>
    public int Read(int size)
    {
        if (_src == null || size <= 0)
        {
            return 0;
        }

        int readSize;
        if (_crypt != null)
        {
            int dataLeft = _fullSize - _dataSize;
            if (size > dataLeft)
            {
                int sizeToRead = size - dataLeft;
                int alignedReadSize = sizeToRead + ((~sizeToRead + 1) & CryptBlockMask);
                EnsureCapacity(_fullSize + alignedReadSize);
                readSize = _src.ReadAtLeast(_data.AsSpan(_fullSize, alignedReadSize), alignedReadSize, throwOnEndOfStream: false);
                _crypt.DecryptBlock(_data.AsSpan(_fullSize, readSize & ~CryptBlockMask));
                _fullSize += readSize;
                _dataSize += readSize == 0 ? 0 : size;
                if (_dataSize > _fullSize)
                {
                    _dataSize = _fullSize;
                }
            }
            else
            {
                readSize = size;
                _dataSize += size;
            }
        }
        else
        {
            EnsureCapacity(_dataSize + size);
            readSize = _src.ReadAtLeast(_data.AsSpan(_dataSize, size), size, throwOnEndOfStream: false);
            _dataSize += readSize;
            _fullSize = _dataSize;
        }

        return readSize;
    }

    /// <summary>Carga bytes de memoria (campos anidados, como el porcentaje de recuperación).</summary>
    public void Read(ReadOnlySpan<byte> src)
    {
        EnsureCapacity(_dataSize + src.Length);
        src.CopyTo(_data.AsSpan(_dataSize));
        _dataSize += src.Length;
        _fullSize = _dataSize;
    }

    public byte Get1() => _readPos < _dataSize ? _data[_readPos++] : (byte)0;

    public ushort Get2()
    {
        if (_readPos + 1 < _dataSize)
        {
            ushort result = (ushort)(_data[_readPos] | (_data[_readPos + 1] << 8));
            _readPos += 2;
            return result;
        }

        return 0;
    }

    public uint Get4()
    {
        if (_readPos + 3 < _dataSize)
        {
            uint result = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_readPos));
            _readPos += 4;
            return result;
        }

        return 0;
    }

    public ulong Get8()
    {
        uint low = Get4(), high = Get4();
        return ((ulong)high << 32) | low;
    }

    /// <summary>Entero de largo variable de RAR5 (7 bits por byte). 0 si se sale del buffer.</summary>
    public ulong GetV()
    {
        ulong result = 0;
        for (int shift = 0; _readPos < _dataSize && shift < 64; shift += 7)
        {
            byte cur = _data[_readPos++];
            result += (ulong)(cur & 0x7f) << shift;
            if ((cur & 0x80) == 0)
            {
                return result;
            }
        }

        return 0;
    }

    /// <summary>Cantidad de bytes del entero variable que empieza en <paramref name="pos"/>; 0 si no termina.</summary>
    public int GetVSize(int pos)
    {
        for (int cur = pos; cur < _dataSize; cur++)
        {
            if ((_data[cur] & 0x80) == 0)
            {
                return cur - pos + 1;
            }
        }

        return 0;
    }

    /// <summary>Copia bytes al campo; lo que falte se completa con ceros.</summary>
    public int GetB(Span<byte> field)
    {
        int copySize = Math.Min(_dataSize - _readPos, field.Length);
        if (copySize > 0)
        {
            _data.AsSpan(_readPos, copySize).CopyTo(field);
        }
        else
        {
            copySize = 0;
        }

        field[copySize..].Clear();
        _readPos += copySize;
        return copySize;
    }

    public byte[] GetBytes(int size)
    {
        var result = new byte[size];
        GetB(result);
        return result;
    }

    /// <summary>CRC de cabecera de RAR 1.5–4: 16 bits bajos del CRC32 desde el byte 2.</summary>
    public uint GetCrc15(bool processedOnly)
    {
        if (_dataSize <= 2)
        {
            return 0;
        }

        int end = processedOnly ? _readPos : _dataSize;
        uint crc = Crc32Algorithm.Compute(_data.AsSpan(2, Math.Max(0, end - 2)));
        return crc & 0xffff;
    }

    /// <summary>CRC de cabecera de RAR5: CRC32 desde el byte 4.</summary>
    public uint GetCrc50()
    {
        if (_dataSize <= 4)
        {
            return 0xffffffff;
        }

        return Crc32Algorithm.Compute(_data.AsSpan(4, _dataSize - 4));
    }
}
