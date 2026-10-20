// Port a C# de unrar 7.3.1 (RARLAB), de unpack50.cpp (algoritmo de RAR 5.0 y 7.0). Copyright (c)
// Alexander L. Roshal.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

using System.Buffers.Binary;

namespace PrZip.Rar;

internal sealed partial class Unpack
{
    private void Unpack5(bool solid)
    {
        _fileExtracted = true;

        UnpInitData(solid);
        if (!UnpReadBuf())
        {
            return;
        }

        if (!ReadBlockHeader(_inp, ref _blockHeader) || !ReadTables(_inp, ref _blockHeader, _blockTables) || !_tablesRead5)
        {
            return;
        }

        var window = _window!;
        var inp = _inp;
        while (true)
        {
            _unpPtr = WrapUp(_unpPtr);
            _firstWinDone |= _prevPtr > _unpPtr;
            _prevPtr = _unpPtr;

            if (inp.InAddr >= _readBorder)
            {
                bool fileDone = false;
                while (inp.InAddr > _blockHeader.BlockStart + _blockHeader.BlockSize - 1 ||
                       (inp.InAddr == _blockHeader.BlockStart + _blockHeader.BlockSize - 1 &&
                        inp.InBit >= _blockHeader.BlockBitSize))
                {
                    if (_blockHeader.LastBlockInFile)
                    {
                        fileDone = true;
                        break;
                    }

                    if (!ReadBlockHeader(inp, ref _blockHeader) || !ReadTables(inp, ref _blockHeader, _blockTables))
                    {
                        return;
                    }
                }

                if (fileDone || !UnpReadBuf())
                {
                    break;
                }
            }

            if (WrapDown(_writeBorder - _unpPtr) <= PackDef.MaxIncLzMatch && _writeBorder != _unpPtr)
            {
                UnpWriteBuf();
                if (_writtenFileSize > _destUnpSize)
                {
                    return;
                }
            }

            uint mainSlot = DecodeNumber(inp, _blockTables.LD);
            if (mainSlot < 256)
            {
                window[_unpPtr++] = (byte)mainSlot;
                continue;
            }

            if (mainSlot >= 262)
            {
                uint length = SlotToLength(inp, mainSlot - 262);

                ulong distance = 1;
                uint dBits, distSlot = DecodeNumber(inp, _blockTables.DD);
                if (distSlot < 4)
                {
                    dBits = 0;
                    distance += distSlot;
                }
                else
                {
                    dBits = distSlot / 2 - 1;
                    distance += (ulong)(2 | (distSlot & 1)) << (int)dBits;
                }

                if (dBits > 0)
                {
                    if (dBits >= 4)
                    {
                        if (dBits > 4)
                        {
                            if (dBits > 36)
                            {
                                distance += (inp.GetBits64() >> (int)(68 - dBits)) << 4;
                            }
                            else
                            {
                                distance += ((ulong)inp.GetBits32() >> (int)(36 - dBits)) << 4;
                            }

                            inp.AddBits(dBits - 4);
                        }

                        uint lowDist = DecodeNumber(inp, _blockTables.LDD);
                        distance += lowDist;
                    }
                    else
                    {
                        distance += inp.GetBits() >> (int)(16 - dBits);
                        inp.AddBits(dBits);
                    }
                }

                if (distance > 0x100)
                {
                    length++;
                    if (distance > 0x2000)
                    {
                        length++;
                        if (distance > 0x40000)
                        {
                            length++;
                        }
                    }
                }

                InsertOldDist(distance);
                _lastLength = length;
                CopyString(length, distance);
                continue;
            }

            if (mainSlot == 256)
            {
                if (!ReadFilter(inp, out var filter) || !AddFilter(ref filter))
                {
                    break;
                }

                continue;
            }

            if (mainSlot == 257)
            {
                if (_lastLength != 0)
                {
                    CopyString(_lastLength, _oldDist[0]);
                }

                continue;
            }

            if (mainSlot < 262)
            {
                uint distNum = mainSlot - 258;
                ulong distance = _oldDist[distNum];
                for (uint i = distNum; i > 0; i--)
                {
                    _oldDist[i] = _oldDist[i - 1];
                }

                _oldDist[0] = distance;

                uint lengthSlot = DecodeNumber(inp, _blockTables.RD);
                uint length = SlotToLength(inp, lengthSlot);
                _lastLength = length;
                CopyString(length, distance);
                continue;
            }
        }

        UnpWriteBuf();
    }

    private static uint ReadFilterData(BitInput inp)
    {
        uint byteCount = (inp.GetBits() >> 14) + 1;
        inp.AddBits(2);

        uint data = 0;
        for (uint i = 0; i < byteCount; i++)
        {
            data += (inp.GetBits() >> 8) << (int)(i * 8);
            inp.AddBits(8);
        }

        return data;
    }

    private bool ReadFilter(BitInput inp, out UnpackFilter filter)
    {
        filter = default;
        if (inp.InAddr > _readTop - 16 && !UnpReadBuf())
        {
            return false;
        }

        filter.BlockStart = ReadFilterData(inp);
        filter.BlockLength = ReadFilterData(inp);
        if (filter.BlockLength > PackDef.MaxFilterBlockSize)
        {
            filter.BlockLength = 0;
        }

        filter.Type = (byte)(inp.GetBits() >> 13);
        inp.AddBits(3);

        if (filter.Type == PackDef.FilterDelta)
        {
            filter.Channels = (byte)((inp.GetBits() >> 11) + 1);
            inp.AddBits(5);
        }

        return true;
    }

    private bool AddFilter(ref UnpackFilter filter)
    {
        if (_filters.Count >= PackDef.MaxUnpackFilters)
        {
            UnpWriteBuf();
            if (_filters.Count >= PackDef.MaxUnpackFilters)
            {
                InitFilters();
            }
        }

        filter.NextWindow = _wrPtr != _unpPtr && WrapDown(_wrPtr - _unpPtr) <= filter.BlockStart;
        filter.BlockStart = (filter.BlockStart + _unpPtr) % MaxWinSize;
        _filters.Add(filter);
        return true;
    }

    private bool UnpReadBuf()
    {
        int dataSize = _readTop - _inp.InAddr;
        if (dataSize < 0)
        {
            return false;
        }

        _blockHeader.BlockSize -= _inp.InAddr - _blockHeader.BlockStart;
        if (_inp.InAddr > BitInput.MaxSize / 2)
        {
            if (dataSize > 0)
            {
                Buffer.BlockCopy(_inp.InBuf, _inp.InAddr, _inp.InBuf, 0, dataSize);
            }

            _inp.InAddr = 0;
            _readTop = dataSize;
        }
        else
        {
            dataSize = _readTop;
        }

        int readCode = 0;
        if (BitInput.MaxSize != dataSize)
        {
            readCode = UnpRead(_inp.InBuf, dataSize, BitInput.MaxSize - dataSize);
        }

        if (readCode > 0)
        {
            _readTop += readCode;
        }

        _readBorder = _readTop - 30;
        _blockHeader.BlockStart = _inp.InAddr;
        if (_blockHeader.BlockSize != -1)
        {
            _readBorder = Math.Min(_readBorder, _blockHeader.BlockStart + _blockHeader.BlockSize - 1);
        }

        return readCode != -1;
    }

    private void UnpWriteBuf()
    {
        ulong writtenBorder = _wrPtr;
        ulong fullWriteSize = WrapDown(_unpPtr - writtenBorder);
        ulong writeSizeLeft = fullWriteSize;
        bool notAllFiltersProcessed = false;
        for (int i = 0; i < _filters.Count; i++)
        {
            var flt = _filters[i];
            if (flt.Type == PackDef.FilterNone)
            {
                continue;
            }

            if (flt.NextWindow)
            {
                if (WrapDown(flt.BlockStart - _wrPtr) <= fullWriteSize)
                {
                    flt.NextWindow = false;
                    _filters[i] = flt;
                }

                continue;
            }

            ulong blockStart = flt.BlockStart;
            uint blockLength = flt.BlockLength;
            if (WrapDown(blockStart - writtenBorder) < writeSizeLeft)
            {
                if (writtenBorder != blockStart)
                {
                    UnpWriteArea(writtenBorder, blockStart);
                    writtenBorder = blockStart;
                    writeSizeLeft = WrapDown(_unpPtr - writtenBorder);
                }

                if (blockLength <= writeSizeLeft)
                {
                    if (blockLength > 0)
                    {
                        ulong blockEnd = WrapUp(blockStart + blockLength);
                        if (_filterSrcMemory.Length < blockLength)
                        {
                            _filterSrcMemory = new byte[blockLength];
                        }

                        var mem = _filterSrcMemory;
                        var window = _window!;
                        if (blockStart < blockEnd || blockEnd == 0)
                        {
                            Buffer.BlockCopy(window, (int)blockStart, mem, 0, (int)blockLength);
                        }
                        else
                        {
                            int firstPartLength = (int)(MaxWinSize - blockStart);
                            Buffer.BlockCopy(window, (int)blockStart, mem, 0, firstPartLength);
                            Buffer.BlockCopy(window, 0, mem, firstPartLength, (int)blockEnd);
                        }

                        var outMem = ApplyFilter(mem, blockLength, ref flt);
                        flt.Type = PackDef.FilterNone;
                        _filters[i] = flt;

                        if (outMem != null)
                        {
                            UnpWriteOut(outMem.AsSpan(0, (int)blockLength));
                        }

                        _writtenFileSize += blockLength;
                        writtenBorder = blockEnd;
                        writeSizeLeft = WrapDown(_unpPtr - writtenBorder);
                    }
                }
                else
                {
                    _wrPtr = writtenBorder;
                    for (int j = i; j < _filters.Count; j++)
                    {
                        var f = _filters[j];
                        if (f.Type != PackDef.FilterNone)
                        {
                            f.NextWindow = false;
                            _filters[j] = f;
                        }
                    }

                    notAllFiltersProcessed = true;
                    break;
                }
            }
        }

        _filters.RemoveAll(f => f.Type == PackDef.FilterNone);

        if (!notAllFiltersProcessed)
        {
            UnpWriteArea(writtenBorder, _unpPtr);
            _wrPtr = _unpPtr;
        }

        _writeBorder = WrapUp(_unpPtr + Math.Min(MaxWinSize, PackDef.UnpackMaxWrite));
        if (_writeBorder == _unpPtr ||
            (_wrPtr != _unpPtr && WrapDown(_wrPtr - _unpPtr) < WrapDown(_writeBorder - _unpPtr)))
        {
            _writeBorder = _wrPtr;
        }
    }

    private byte[]? ApplyFilter(byte[] data, uint dataSize, ref UnpackFilter flt)
    {
        switch (flt.Type)
        {
            case PackDef.FilterE8:
            case PackDef.FilterE8E9:
                {
                    uint fileOffset = (uint)_writtenFileSize;
                    const uint fileSize = 0x1000000;
                    byte cmpByte2 = flt.Type == PackDef.FilterE8E9 ? (byte)0xe9 : (byte)0xe8;
                    int p = 0;
                    for (uint curPos = 0; curPos + 4 < dataSize;)
                    {
                        byte curByte = data[p++];
                        curPos++;
                        if (curByte == 0xe8 || curByte == cmpByte2)
                        {
                            uint offset = (curPos + fileOffset) % fileSize;
                            uint addr = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4));
                            if ((addr & 0x80000000) != 0)
                            {
                                if (((addr + offset) & 0x80000000) == 0)
                                {
                                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(p, 4), addr + fileSize);
                                }
                            }
                            else if (((addr - fileSize) & 0x80000000) != 0)
                            {
                                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(p, 4), addr - offset);
                            }

                            p += 4;
                            curPos += 4;
                        }
                    }
                }

                return data;
            case PackDef.FilterArm:
                {
                    uint fileOffset = (uint)_writtenFileSize;
                    for (uint curPos = 0; curPos + 3 < dataSize; curPos += 4)
                    {
                        int d = (int)curPos;
                        if (data[d + 3] == 0xeb)
                        {
                            uint offset = data[d] + (uint)data[d + 1] * 0x100 + (uint)data[d + 2] * 0x10000;
                            offset -= (fileOffset + curPos) / 4;
                            data[d] = (byte)offset;
                            data[d + 1] = (byte)(offset >> 8);
                            data[d + 2] = (byte)(offset >> 16);
                        }
                    }
                }

                return data;
            case PackDef.FilterDelta:
                {
                    uint channels = flt.Channels, srcPos = 0;
                    if (_filterDstMemory.Length < dataSize)
                    {
                        _filterDstMemory = new byte[dataSize];
                    }

                    var dst = _filterDstMemory;
                    for (uint curChannel = 0; curChannel < channels; curChannel++)
                    {
                        byte prevByte = 0;
                        for (uint destPos = curChannel; destPos < dataSize; destPos += channels)
                        {
                            prevByte -= data[srcPos++];
                            dst[destPos] = prevByte;
                        }
                    }

                    return dst;
                }
        }

        return null;
    }

    private void UnpWriteArea(ulong startPtr, ulong endPtr)
    {
        var window = _window!;
        if (endPtr < startPtr)
        {
            UnpWriteData(window.AsSpan((int)startPtr, (int)(MaxWinSize - startPtr)));
            UnpWriteData(window.AsSpan(0, (int)endPtr));
        }
        else
        {
            UnpWriteData(window.AsSpan((int)startPtr, (int)(endPtr - startPtr)));
        }
    }

    private void UnpWriteData(ReadOnlySpan<byte> data)
    {
        if (_writtenFileSize >= _destUnpSize)
        {
            return;
        }

        int writeSize = data.Length;
        long leftToWrite = _destUnpSize - _writtenFileSize;
        if (writeSize > leftToWrite)
        {
            writeSize = (int)leftToWrite;
        }

        UnpWriteOut(data[..writeSize]);
        _writtenFileSize += data.Length;
    }

    private void UnpInitData50(bool solid)
    {
        if (!solid)
        {
            _tablesRead5 = false;
        }
    }

    private bool ReadBlockHeader(BitInput inp, ref UnpackBlockHeader header)
    {
        header.HeaderSize = 0;

        if (inp.InAddr > _readTop - 7 && !UnpReadBuf())
        {
            return false;
        }

        inp.AddBits((uint)((8 - inp.InBit) & 7));

        byte blockFlags = (byte)(inp.GetBits() >> 8);
        inp.AddBits(8);
        uint byteCount = (uint)((blockFlags >> 3) & 3) + 1;

        if (byteCount == 4)
        {
            return false;
        }

        header.HeaderSize = (int)(2 + byteCount);
        header.BlockBitSize = (blockFlags & 7) + 1;

        byte savedCheckSum = (byte)(inp.GetBits() >> 8);
        inp.AddBits(8);

        int blockSize = 0;
        for (uint i = 0; i < byteCount; i++)
        {
            blockSize += (int)((inp.GetBits() >> 8) << (int)(i * 8));
            inp.AddBits(8);
        }

        header.BlockSize = blockSize;
        byte checkSum = (byte)(0x5a ^ blockFlags ^ blockSize ^ (blockSize >> 8) ^ (blockSize >> 16));
        if (checkSum != savedCheckSum)
        {
            return false;
        }

        header.BlockStart = inp.InAddr;
        _readBorder = Math.Min(_readBorder, header.BlockStart + header.BlockSize - 1);

        header.LastBlockInFile = (blockFlags & 0x40) != 0;
        header.TablePresent = (blockFlags & 0x80) != 0;
        return true;
    }

    private bool ReadTables(BitInput inp, ref UnpackBlockHeader header, UnpackBlockTables tables)
    {
        if (!header.TablePresent)
        {
            return true;
        }

        if (inp.InAddr > _readTop - 25 && !UnpReadBuf())
        {
            return false;
        }

        Span<byte> bitLength = stackalloc byte[PackDef.BC];
        for (int i = 0; i < PackDef.BC; i++)
        {
            uint length = (byte)(inp.GetBits() >> 12);
            inp.AddBits(4);
            if (length == 15)
            {
                uint zeroCount = (byte)(inp.GetBits() >> 12);
                inp.AddBits(4);
                if (zeroCount == 0)
                {
                    bitLength[i] = 15;
                }
                else
                {
                    zeroCount += 2;
                    while (zeroCount-- > 0 && i < bitLength.Length)
                    {
                        bitLength[i++] = 0;
                    }

                    i--;
                }
            }
            else
            {
                bitLength[i] = (byte)length;
            }
        }

        MakeDecodeTables(bitLength, tables.BD, PackDef.BC);

        Span<byte> table = stackalloc byte[PackDef.HuffTableSizeX];
        int tableSize = _extraDist ? PackDef.HuffTableSizeX : PackDef.HuffTableSizeB;
        for (int i = 0; i < tableSize;)
        {
            if (inp.InAddr > _readTop - 5 && !UnpReadBuf())
            {
                return false;
            }

            uint number = DecodeNumber(inp, tables.BD);
            if (number < 16)
            {
                table[i] = (byte)number;
                i++;
            }
            else if (number < 18)
            {
                uint n;
                if (number == 16)
                {
                    n = (inp.GetBits() >> 13) + 3;
                    inp.AddBits(3);
                }
                else
                {
                    n = (inp.GetBits() >> 9) + 11;
                    inp.AddBits(7);
                }

                if (i == 0)
                {
                    return false;
                }

                while (n-- > 0 && i < tableSize)
                {
                    table[i] = table[i - 1];
                    i++;
                }
            }
            else
            {
                uint n;
                if (number == 18)
                {
                    n = (inp.GetBits() >> 13) + 3;
                    inp.AddBits(3);
                }
                else
                {
                    n = (inp.GetBits() >> 9) + 11;
                    inp.AddBits(7);
                }

                while (n-- > 0 && i < tableSize)
                {
                    table[i++] = 0;
                }
            }
        }

        _tablesRead5 = true;
        if (inp.InAddr > _readTop)
        {
            return false;
        }

        int dCodes = _extraDist ? PackDef.DCX : PackDef.DCB;
        MakeDecodeTables(table, tables.LD, PackDef.NC);
        MakeDecodeTables(table[PackDef.NC..], tables.DD, dCodes);
        MakeDecodeTables(table[(PackDef.NC + dCodes)..], tables.LDD, PackDef.LDC);
        MakeDecodeTables(table[(PackDef.NC + dCodes + PackDef.LDC)..], tables.RD, PackDef.RC);
        return true;
    }
}
