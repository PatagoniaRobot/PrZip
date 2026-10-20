// Port a C# de unrar 7.3.1 (RARLAB), de rarvm.cpp y rarvm.hpp. Copyright (c) Alexander L. Roshal.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

using System.Buffers.Binary;
using PrZip.Primitives.Checksum;

namespace PrZip.Rar;

internal enum VMStandardFilter
{
    None,
    E8,
    E8E9,
    Itanium,
    Rgb,
    Audio,
    Delta
}

internal sealed class VMPreparedProgram
{
    public VMStandardFilter Type = VMStandardFilter.None;
    public readonly uint[] InitR = new uint[7];

    /// <summary>Datos filtrados: posición en la memoria de la VM (FilteredData de unrar) y tamaño.</summary>
    public int FilteredDataOffset = -1;
    public uint FilteredDataSize;
}

/// <summary>
/// "Máquina virtual" de RAR 3.x. Como unrar 7, no ejecuta código arbitrario: reconoce los filtros estándar
/// por el largo y el CRC32 del programa y los aplica en código nativo. Un programa desconocido no produce
/// datos (la suma del archivo lo delata).
/// </summary>
internal sealed class RarVM
{
    public const int MemSize = 0x40000;
    public const int MemMask = MemSize - 1;

    public byte[] Mem { get; } = new byte[MemSize + 4];
    private readonly uint[] _r = new uint[8];

    public void Execute(VMPreparedProgram prg)
    {
        Array.Copy(prg.InitR, _r, prg.InitR.Length);
        prg.FilteredDataOffset = -1;
        if (prg.Type != VMStandardFilter.None)
        {
            bool success = ExecuteStandardFilter(prg.Type);
            uint blockSize = prg.InitR[4] & MemMask;
            prg.FilteredDataSize = blockSize;
            if (prg.Type is VMStandardFilter.Delta or VMStandardFilter.Rgb or VMStandardFilter.Audio)
            {
                prg.FilteredDataOffset = 2 * blockSize > MemSize || !success ? 0 : (int)blockSize;
            }
            else
            {
                prg.FilteredDataOffset = 0;
            }
        }
    }

    private static readonly (uint Length, uint Crc, VMStandardFilter Type)[] StdList =
    [
        (53, 0xad576887, VMStandardFilter.E8),
        (57, 0x3cd7e57e, VMStandardFilter.E8E9),
        (120, 0x3769893f, VMStandardFilter.Itanium),
        (29, 0x0e06077d, VMStandardFilter.Delta),
        (149, 0x1c2c5dc8, VMStandardFilter.Rgb),
        (216, 0xbc85e701, VMStandardFilter.Audio),
    ];

    public static void Prepare(ReadOnlySpan<byte> code, VMPreparedProgram prg)
    {
        byte xorSum = 0;
        for (int i = 1; i < code.Length; i++)
        {
            xorSum ^= code[i];
        }

        if (code.Length == 0 || xorSum != code[0])
        {
            return;
        }

        uint codeCrc = Crc32Algorithm.Compute(code);
        foreach (var (length, crc, type) in StdList)
        {
            if (crc == codeCrc && length == code.Length)
            {
                prg.Type = type;
                break;
            }
        }
    }

    public static uint ReadData(BitInput inp)
    {
        uint data = inp.GetBits();
        switch (data & 0xc000)
        {
            case 0:
                inp.AddBits(6);
                return (data >> 10) & 0xf;
            case 0x4000:
                if ((data & 0x3c00) == 0)
                {
                    data = 0xffffff00 | ((data >> 2) & 0xff);
                    inp.AddBits(14);
                }
                else
                {
                    data = (data >> 6) & 0xff;
                    inp.AddBits(10);
                }

                return data;
            case 0x8000:
                inp.AddBits(2);
                data = inp.GetBits();
                inp.AddBits(16);
                return data;
            default:
                inp.AddBits(2);
                data = inp.GetBits() << 16;
                inp.AddBits(16);
                data |= inp.GetBits();
                inp.AddBits(16);
                return data;
        }
    }

    public void SetMemory(int pos, byte[] data, int dataOffset, int dataSize)
    {
        if (pos < MemSize && !(ReferenceEquals(data, Mem) && dataOffset == pos))
        {
            int copySize = Math.Min(dataSize, MemSize - pos);
            if (copySize > 0)
            {
                Buffer.BlockCopy(data, dataOffset, Mem, pos, copySize);
            }
        }
    }

    private bool ExecuteStandardFilter(VMStandardFilter filterType)
    {
        var mem = Mem;
        switch (filterType)
        {
            case VMStandardFilter.E8:
            case VMStandardFilter.E8E9:
                {
                    uint dataSize = _r[4], fileOffset = _r[6];
                    if (dataSize > MemSize || dataSize < 4)
                    {
                        return false;
                    }

                    const uint fileSize = 0x1000000;
                    byte cmpByte2 = filterType == VMStandardFilter.E8E9 ? (byte)0xe9 : (byte)0xe8;
                    int data = 0;
                    for (uint curPos = 0; curPos < dataSize - 4;)
                    {
                        byte curByte = mem[data++];
                        curPos++;
                        if (curByte == 0xe8 || curByte == cmpByte2)
                        {
                            uint offset = curPos + fileOffset;
                            uint addr = BinaryPrimitives.ReadUInt32LittleEndian(mem.AsSpan(data, 4));
                            if ((addr & 0x80000000) != 0)
                            {
                                if (((addr + offset) & 0x80000000) == 0)
                                {
                                    BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(data, 4), addr + fileSize);
                                }
                            }
                            else if (((addr - fileSize) & 0x80000000) != 0)
                            {
                                BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(data, 4), addr - offset);
                            }

                            data += 4;
                            curPos += 4;
                        }
                    }
                }

                break;
            case VMStandardFilter.Itanium:
                {
                    uint dataSize = _r[4], fileOffset = _r[6];
                    if (dataSize > MemSize || dataSize < 21)
                    {
                        return false;
                    }

                    ReadOnlySpan<byte> masks = [4, 4, 6, 6, 0, 0, 7, 7, 4, 4, 0, 0, 4, 4, 0, 0];
                    uint curPos = 0;
                    int data = 0;
                    fileOffset >>= 4;
                    while (curPos < dataSize - 21)
                    {
                        int b = (mem[data] & 0x1f) - 0x10;
                        if (b >= 0)
                        {
                            byte cmdMask = masks[b];
                            if (cmdMask != 0)
                            {
                                for (uint i = 0; i <= 2; i++)
                                {
                                    if ((cmdMask & (1 << (int)i)) != 0)
                                    {
                                        uint startPos = i * 41 + 5;
                                        uint opType = ItaniumGetBits(mem, data, startPos + 37, 4);
                                        if (opType == 5)
                                        {
                                            uint offset = ItaniumGetBits(mem, data, startPos + 13, 20);
                                            ItaniumSetBits(mem, data, (offset - fileOffset) & 0xfffff, startPos + 13, 20);
                                        }
                                    }
                                }
                            }
                        }

                        data += 16;
                        curPos += 16;
                        fileOffset++;
                    }
                }

                break;
            case VMStandardFilter.Delta:
                {
                    uint dataSize = _r[4], channels = _r[0], srcPos = 0, border = dataSize * 2;
                    if (dataSize > MemSize / 2 || channels > PackDef.Max3UnpackChannels || channels == 0)
                    {
                        return false;
                    }

                    for (uint curChannel = 0; curChannel < channels; curChannel++)
                    {
                        byte prevByte = 0;
                        for (uint destPos = dataSize + curChannel; destPos < border; destPos += channels)
                        {
                            prevByte -= mem[srcPos++];
                            mem[destPos] = prevByte;
                        }
                    }
                }

                break;
            case VMStandardFilter.Rgb:
                {
                    uint dataSize = _r[4], width = _r[0] - 3, posR = _r[1];
                    if (dataSize > MemSize / 2 || dataSize < 3 || width > dataSize || posR > 2)
                    {
                        return false;
                    }

                    int srcData = 0;
                    int destData = (int)dataSize;
                    const uint channels = 3;
                    for (uint curChannel = 0; curChannel < channels; curChannel++)
                    {
                        uint prevByte = 0;
                        for (uint i = curChannel; i < dataSize; i += channels)
                        {
                            uint predicted;
                            if (i >= width + 3)
                            {
                                int upperData = destData + (int)i - (int)width;
                                uint upperByte = mem[upperData];
                                uint upperLeftByte = mem[upperData - 3];
                                predicted = prevByte + upperByte - upperLeftByte;
                                int pa = Math.Abs((int)(predicted - prevByte));
                                int pb = Math.Abs((int)(predicted - upperByte));
                                int pc = Math.Abs((int)(predicted - upperLeftByte));
                                if (pa <= pb && pa <= pc)
                                {
                                    predicted = prevByte;
                                }
                                else if (pb <= pc)
                                {
                                    predicted = upperByte;
                                }
                                else
                                {
                                    predicted = upperLeftByte;
                                }
                            }
                            else
                            {
                                predicted = prevByte;
                            }

                            byte v = (byte)(predicted - mem[srcData++]);
                            mem[destData + i] = v;
                            prevByte = v;
                        }
                    }

                    for (uint i = posR, b2 = dataSize - 2; i < b2; i += 3)
                    {
                        byte g = mem[destData + i + 1];
                        mem[destData + i] += g;
                        mem[destData + i + 2] += g;
                    }
                }

                break;
            case VMStandardFilter.Audio:
                {
                    uint dataSize = _r[4], channels = _r[0];
                    int srcData = 0;
                    int destData = (int)dataSize;
                    if (dataSize > MemSize / 2 || channels > 128 || channels == 0)
                    {
                        return false;
                    }

                    Span<uint> dif = stackalloc uint[7];
                    for (uint curChannel = 0; curChannel < channels; curChannel++)
                    {
                        uint prevByte = 0, prevDelta = 0;
                        int d1 = 0, d2 = 0, d3;
                        int k1 = 0, k2 = 0, k3 = 0;
                        dif.Clear();

                        for (uint i = curChannel, byteCount = 0; i < dataSize; i += channels, byteCount++)
                        {
                            d3 = d2;
                            d2 = (int)prevDelta - d1;
                            d1 = (int)prevDelta;

                            uint predicted = (uint)(8 * (int)prevByte + k1 * d1 + k2 * d2 + k3 * d3);
                            predicted = (predicted >> 3) & 0xff;

                            uint curByte = mem[srcData++];
                            predicted -= curByte;
                            mem[destData + i] = (byte)predicted;
                            prevDelta = (uint)(sbyte)(byte)(predicted - prevByte);
                            prevByte = predicted & 0xff;

                            int d = (sbyte)(byte)curByte;
                            d = (int)((uint)d << 3);

                            dif[0] += (uint)Math.Abs(d);
                            dif[1] += (uint)Math.Abs(d - d1);
                            dif[2] += (uint)Math.Abs(d + d1);
                            dif[3] += (uint)Math.Abs(d - d2);
                            dif[4] += (uint)Math.Abs(d + d2);
                            dif[5] += (uint)Math.Abs(d - d3);
                            dif[6] += (uint)Math.Abs(d + d3);

                            if ((byteCount & 0x1f) == 0)
                            {
                                uint minDif = dif[0], numMinDif = 0;
                                dif[0] = 0;
                                for (int j = 1; j < dif.Length; j++)
                                {
                                    if (dif[j] < minDif)
                                    {
                                        minDif = dif[j];
                                        numMinDif = (uint)j;
                                    }

                                    dif[j] = 0;
                                }

                                switch (numMinDif)
                                {
                                    case 1: if (k1 >= -16) k1--; break;
                                    case 2: if (k1 < 16) k1++; break;
                                    case 3: if (k2 >= -16) k2--; break;
                                    case 4: if (k2 < 16) k2++; break;
                                    case 5: if (k3 >= -16) k3--; break;
                                    case 6: if (k3 < 16) k3++; break;
                                }
                            }
                        }
                    }
                }

                break;
        }

        return true;
    }

    private static uint ItaniumGetBits(byte[] mem, int data, uint bitPos, uint bitCount)
    {
        int inAddr = data + (int)(bitPos / 8);
        int inBit = (int)(bitPos & 7);
        uint bitField = mem[inAddr++];
        bitField |= (uint)mem[inAddr++] << 8;
        bitField |= (uint)mem[inAddr++] << 16;
        bitField |= (uint)mem[inAddr] << 24;
        bitField >>= inBit;
        return bitField & (0xffffffff >> (int)(32 - bitCount));
    }

    private static void ItaniumSetBits(byte[] mem, int data, uint bitField, uint bitPos, uint bitCount)
    {
        int inAddr = data + (int)(bitPos / 8);
        int inBit = (int)(bitPos & 7);
        uint andMask = 0xffffffff >> (int)(32 - bitCount);
        andMask = ~(andMask << inBit);
        bitField <<= inBit;
        for (int i = 0; i < 4; i++)
        {
            mem[inAddr + i] &= (byte)andMask;
            mem[inAddr + i] |= (byte)bitField;
            andMask = (andMask >> 8) | 0xff000000;
            bitField >>= 8;
        }
    }
}
