// Port a C# de unrar 7.3.1 (RARLAB), de unpack.cpp, unpack.hpp, unpackinline.cpp, getbits.hpp y
// compress.hpp. Copyright (c) Alexander L. Roshal.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using PrZip.Exceptions;

namespace PrZip.Rar;

/// <summary>Lectura de bits de mayor a menor (BitInput de unrar).</summary>
internal sealed class BitInput
{
    public const int MaxSize = 0x8000;

    // unrar reserva 8 bytes extra para que getbits* no se salga del buffer; aquí se reserva más y, además,
    // C# controla los límites: un archivo hostil termina en excepción, nunca leyendo memoria ajena.
    public readonly byte[] InBuf;
    public int InAddr;
    public int InBit;

    public BitInput(int size = MaxSize)
    {
        InBuf = new byte[size + 64];
    }

    public void InitBitInput()
    {
        InAddr = InBit = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddBits(uint bits)
    {
        bits += (uint)InBit;
        InAddr += (int)(bits >> 3);
        InBit = (int)(bits & 7);
    }

    /// <summary>16 bits desde la posición actual; el bit actual queda arriba.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetBits()
    {
        uint bitField = BinaryPrimitives.ReadUInt32BigEndian(InBuf.AsSpan(InAddr, 4));
        bitField >>= 16 - InBit;
        return bitField & 0xffff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetBits32()
    {
        uint bitField = BinaryPrimitives.ReadUInt32BigEndian(InBuf.AsSpan(InAddr, 4));
        bitField <<= InBit;
        bitField |= (uint)InBuf[InAddr + 4] >> (8 - InBit);
        return bitField;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetBits64()
    {
        ulong bitField = BinaryPrimitives.ReadUInt64BigEndian(InBuf.AsSpan(InAddr, 8));
        bitField <<= InBit;
        bitField |= (uint)InBuf[InAddr + 8] >> (8 - InBit);
        return bitField;
    }

    public bool Overflow(uint incPtr) => InAddr + incPtr >= MaxSize;
}

/// <summary>Tabla de decodificación Huffman (DecodeTable de unrar).</summary>
internal sealed class DecodeTable
{
    public const int MaxQuickDecodeBits = 9;

    public uint MaxNum;
    public readonly uint[] DecodeLen = new uint[16];
    public readonly uint[] DecodePos = new uint[16];
    public uint QuickBits;
    public readonly byte[] QuickLen = new byte[1 << MaxQuickDecodeBits];
    public readonly ushort[] QuickNum = new ushort[1 << MaxQuickDecodeBits];
    public readonly ushort[] DecodeNum = new ushort[PackDef.LargestTableSize];

    public void Clear()
    {
        MaxNum = 0;
        Array.Clear(DecodeLen);
        Array.Clear(DecodePos);
        QuickBits = 0;
        Array.Clear(QuickLen);
        Array.Clear(QuickNum);
        Array.Clear(DecodeNum);
    }
}

/// <summary>Constantes comunes de compresión (PackDef de unrar).</summary>
internal static class PackDef
{
    public const uint MaxLzMatch = 0x1001;
    public const uint MaxIncLzMatch = MaxLzMatch + 3;
    public const uint Max3LzMatch = 0x101;
    public const uint Max3IncLzMatch = Max3LzMatch + 3;
    public const uint LowDistRepCount = 16;

    public const int NC = 306;
    public const int DCB = 64;
    public const int DCX = 80;
    public const int LDC = 16;
    public const int RC = 44;
    public const int HuffTableSizeB = NC + DCB + RC + LDC;
    public const int HuffTableSizeX = NC + DCX + RC + LDC;
    public const int BC = 20;

    public const int NC30 = 299;
    public const int DC30 = 60;
    public const int LDC30 = 17;
    public const int RC30 = 28;
    public const int BC30 = 20;
    public const int HuffTableSize30 = NC30 + DC30 + RC30 + LDC30;

    public const int NC20 = 298;
    public const int DC20 = 48;
    public const int RC20 = 28;
    public const int BC20 = 19;
    public const int MC20 = 257;

    public const int LargestTableSize = 306;

    public const int MaxUnpackFilters = 8192;
    public const int Max3UnpackFilters = 8192;
    public const int Max3UnpackChannels = 1024;
    public const uint MaxFilterBlockSize = 0x400000;
    public const ulong UnpackMaxWrite = 0x400000;

    // FilterType de unrar (los valores se usan tal cual en RAR5).
    public const byte FilterDelta = 0;
    public const byte FilterE8 = 1;
    public const byte FilterE8E9 = 2;
    public const byte FilterArm = 3;
    public const byte FilterAudio = 4;
    public const byte FilterRgb = 5;
    public const byte FilterItanium = 6;
    public const byte FilterText = 7;
    public const byte FilterNone = 10;
}

/// <summary>
/// Descompresor de RAR (clase Unpack de unrar): ventana deslizante común y los algoritmos de RAR 1.5, 2.x,
/// 3.x y 5.0/7.0 en archivos parciales. La entrada son los datos comprimidos de la entrada (ya descifrados)
/// y la salida va a un <see cref="RarArchive.HashingWriter"/>.
/// </summary>
internal sealed partial class Unpack : IDisposable
{
    /// <summary>
    /// Ventana máxima admitida. unrar llega a 64 GB, pero aquí la ventana es un arreglo administrado (con
    /// control de límites, para que un archivo hostil no pueda tocar memoria ajena): hasta 1 GB, el mayor
    /// diccionario de WinRAR 5 y 6. WinRAR 7 crea mayores solo si se le pide explícitamente.
    /// </summary>
    public const ulong MaxSupportedWindow = 1UL << 30;

    private Func<byte[], int, int, int> _read = null!;
    private RarArchive.HashingWriter _write = null!;

    private readonly BitInput _inp = new();

    private readonly List<UnpackFilter> _filters = [];
    private byte[] _filterSrcMemory = [];
    private byte[] _filterDstMemory = [];

    private readonly ulong[] _oldDist = new ulong[4];
    private ulong _oldDistPtr;
    private uint _lastDist;
    private bool _tablesRead2;
    private bool _tablesRead3;
    private uint _lastLength;

    private ulong _unpPtr;
    private ulong _prevPtr;
    private bool _firstWinDone;
    private ulong _wrPtr;

    private int _readTop;
    private int _readBorder;

    private UnpackBlockHeader _blockHeader;
    private readonly UnpackBlockTables _blockTables = new();

    private ulong _writeBorder;

    private byte[]? _window;

    private long _destUnpSize;
    private long _writtenFileSize;
    private bool _fileExtracted;

    public ulong AllocWinSize;
    public ulong MaxWinSize;
    public ulong MaxWinMask;

    private bool _extraDist;
    private bool _tablesRead5;

    public bool IsFileExtracted => _fileExtracted;

    public Unpack()
    {
        UnpInitData(false);
        UnpInitData15(false);
        InitHuff();
    }

    /// <summary>Conecta la entrada y la salida de la entrada actual.</summary>
    public void SetIO(Func<byte[], int, int, int> read, RarArchive.HashingWriter write)
    {
        _read = read;
        _write = write;
    }

    public void SetDestSize(long destSize)
    {
        _destUnpSize = destSize;
        _fileExtracted = false;
    }

    public void Init(ulong winSize, bool solid)
    {
        const ulong minAllocSize = 0x40000;
        if (winSize < minAllocSize)
        {
            winSize = minAllocSize;
        }

        if (winSize > Math.Min(0x10000000000UL, RarConst.UnpackMaxDict))
        {
            throw new LimitExceededException($"RAR: diccionario de {winSize >> 20} MB fuera del rango del formato.");
        }

        if (winSize > MaxSupportedWindow)
        {
            throw new LimitExceededException($"RAR: diccionario de {winSize >> 20} MB; el máximo admitido es {MaxSupportedWindow >> 20} MB.");
        }

        if (!solid || _window == null)
        {
            MaxWinSize = winSize;
            MaxWinMask = MaxWinSize - 1;
        }

        if (winSize <= AllocWinSize)
        {
            return;
        }

        if (solid && _window != null)
        {
            throw new CorruptArchiveException("RAR: un archivo sólido no puede agrandar el diccionario.");
        }

        _window = null;
        _window = new byte[winSize];
        AllocWinSize = winSize;
    }

    public void DoUnpack(uint method, bool solid)
    {
        switch (method)
        {
            case 15:
                Unpack15(solid);
                break;
            case 20:
            case 26:
                Unpack20(solid);
                break;
            case 29:
                Unpack29(solid);
                break;
            case RarConst.VerPack5:
            case RarConst.VerPack7:
                _extraDist = method == RarConst.VerPack7;
                Unpack5(solid);
                break;
            default:
                throw new UnsupportedMethodException($"RAR: algoritmo de compresión {method} desconocido.");
        }
    }

    private void UnpInitData(bool solid)
    {
        if (!solid)
        {
            _oldDist[0] = _oldDist[1] = _oldDist[2] = _oldDist[3] = ulong.MaxValue;
            _oldDistPtr = 0;
            _lastDist = uint.MaxValue;
            _lastLength = 0;
            _blockTables.Clear();
            _unpPtr = _wrPtr = 0;
            _prevPtr = 0;
            _firstWinDone = false;
            _writeBorder = Math.Min(MaxWinSize, PackDef.UnpackMaxWrite);
        }

        InitFilters();

        _inp.InitBitInput();
        _writtenFileSize = 0;
        _readTop = 0;
        _readBorder = 0;

        _blockHeader = default;
        _blockHeader.BlockSize = -1;
        UnpInitData20(solid);
        UnpInitData30(solid);
        UnpInitData50(solid);
    }

    private static void MakeDecodeTables(ReadOnlySpan<byte> lengthTable, DecodeTable dec, int size)
    {
        dec.MaxNum = (uint)size;

        Span<uint> lengthCount = stackalloc uint[16];
        lengthCount.Clear();
        for (int i = 0; i < size; i++)
        {
            lengthCount[lengthTable[i] & 0xf]++;
        }

        lengthCount[0] = 0;
        Array.Clear(dec.DecodeNum, 0, size);
        dec.DecodePos[0] = 0;
        dec.DecodeLen[0] = 0;

        uint upperLimit = 0;
        for (int i = 1; i < 16; i++)
        {
            upperLimit += lengthCount[i];
            uint leftAligned = upperLimit << (16 - i);
            upperLimit *= 2;
            dec.DecodeLen[i] = leftAligned;
            dec.DecodePos[i] = dec.DecodePos[i - 1] + lengthCount[i - 1];
        }

        Span<uint> copyDecodePos = stackalloc uint[16];
        dec.DecodePos.CopyTo(copyDecodePos);

        for (int i = 0; i < size; i++)
        {
            int curBitLength = lengthTable[i] & 0xf;
            if (curBitLength != 0)
            {
                uint lastPos = copyDecodePos[curBitLength];
                dec.DecodeNum[lastPos] = (ushort)i;
                copyDecodePos[curBitLength]++;
            }
        }

        dec.QuickBits = size switch
        {
            PackDef.NC or PackDef.NC20 or PackDef.NC30 => DecodeTable.MaxQuickDecodeBits,
            _ => DecodeTable.MaxQuickDecodeBits > 3 ? DecodeTable.MaxQuickDecodeBits - 3 : 0
        };

        uint quickDataSize = 1u << (int)dec.QuickBits;
        uint bitLength = 1;
        for (uint code = 0; code < quickDataSize; code++)
        {
            uint bitField = code << (int)(16 - dec.QuickBits);
            while (bitLength < dec.DecodeLen.Length && bitField >= dec.DecodeLen[bitLength])
            {
                bitLength++;
            }

            dec.QuickLen[code] = (byte)bitLength;
            uint dist = bitField - dec.DecodeLen[bitLength - 1];
            dist >>= (int)(16 - bitLength);

            uint pos;
            if (bitLength < dec.DecodePos.Length && (pos = dec.DecodePos[bitLength] + dist) < size)
            {
                dec.QuickNum[code] = dec.DecodeNum[pos];
            }
            else
            {
                dec.QuickNum[code] = 0;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InsertOldDist(ulong distance)
    {
        _oldDist[3] = _oldDist[2];
        _oldDist[2] = _oldDist[1];
        _oldDist[1] = _oldDist[0];
        _oldDist[0] = distance;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong WrapDown(ulong winPos) => winPos >= MaxWinSize ? winPos + MaxWinSize : winPos;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong WrapUp(ulong winPos) => winPos >= MaxWinSize ? winPos - MaxWinSize : winPos;

    private void CopyString(uint length, ulong distance)
    {
        var window = _window!;
        ulong srcPtr = _unpPtr - distance;

        if (distance > _unpPtr)
        {
            srcPtr += MaxWinSize;
            if (distance > MaxWinSize || !_firstWinDone)
            {
                while (length-- > 0)
                {
                    window[_unpPtr] = 0;
                    _unpPtr = WrapUp(_unpPtr + 1);
                }

                return;
            }
        }

        if (srcPtr < MaxWinSize - PackDef.MaxIncLzMatch && _unpPtr < MaxWinSize - PackDef.MaxIncLzMatch)
        {
            int src = (int)srcPtr;
            int dest = (int)_unpPtr;
            _unpPtr += length;
            if (distance >= length)
            {
                window.AsSpan(src, (int)length).CopyTo(window.AsSpan(dest, (int)length));
            }
            else
            {
                // Copias superpuestas: byte a byte, como en unrar.
                for (int i = 0; i < length; i++)
                {
                    window[dest + i] = window[src + i];
                }
            }
        }
        else
        {
            while (length-- > 0)
            {
                window[_unpPtr] = window[WrapUp(srcPtr++)];
                _unpPtr = WrapUp(_unpPtr + 1);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint DecodeNumber(BitInput inp, DecodeTable dec)
    {
        uint bitField = inp.GetBits() & 0xfffe;
        if (bitField < dec.DecodeLen[dec.QuickBits])
        {
            uint code = bitField >> (int)(16 - dec.QuickBits);
            inp.AddBits(dec.QuickLen[code]);
            return dec.QuickNum[code];
        }

        uint bits = 15;
        for (uint i = dec.QuickBits + 1; i < 15; i++)
        {
            if (bitField < dec.DecodeLen[i])
            {
                bits = i;
                break;
            }
        }

        inp.AddBits(bits);
        uint dist = bitField - dec.DecodeLen[bits - 1];
        dist >>= (int)(16 - bits);
        uint pos = dec.DecodePos[bits] + dist;
        if (pos >= dec.MaxNum)
        {
            pos = 0;
        }

        return dec.DecodeNum[pos];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint SlotToLength(BitInput inp, uint slot)
    {
        uint lBits, length = 2;
        if (slot < 8)
        {
            lBits = 0;
            length += slot;
        }
        else
        {
            lBits = slot / 4 - 1;
            length += (4 | (slot & 3)) << (int)lBits;
        }

        if (lBits > 0)
        {
            length += inp.GetBits() >> (int)(16 - lBits);
            inp.AddBits(lBits);
        }

        return length;
    }

    private void InitFilters() => _filters.Clear();

    /// <summary>Lee datos comprimidos (ComprDataIO::UnpRead). Devuelve 0 al terminar.</summary>
    private int UnpRead(byte[] buffer, int offset, int count) => _read(buffer, offset, count);

    private void UnpWriteOut(ReadOnlySpan<byte> data) => _write.Write(data);

    public void Dispose()
    {
        _ppm.Dispose();
        _window = null;
    }
}

internal struct UnpackBlockHeader
{
    public int BlockSize;
    public int BlockBitSize;
    public int BlockStart;
    public int HeaderSize;
    public bool LastBlockInFile;
    public bool TablePresent;
}

internal sealed class UnpackBlockTables
{
    public readonly DecodeTable LD = new();
    public readonly DecodeTable DD = new();
    public readonly DecodeTable LDD = new();
    public readonly DecodeTable RD = new();
    public readonly DecodeTable BD = new();

    public void Clear()
    {
        LD.Clear();
        DD.Clear();
        LDD.Clear();
        RD.Clear();
        BD.Clear();
    }
}

internal struct UnpackFilter
{
    public byte Type;
    public byte Channels;
    public bool NextWindow;
    public ulong BlockStart;
    public uint BlockLength;
}
