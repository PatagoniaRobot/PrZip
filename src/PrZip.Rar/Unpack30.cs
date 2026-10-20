// Port a C# de unrar 7.3.1 (RARLAB), de unpack30.cpp (algoritmo de RAR 2.9–4.x) y de las partes de
// model.cpp que leen la cabecera PPMd (DecodeInit). Copyright (c) Alexander L. Roshal.
// El modelo PPMd y su decodificación aritmética no se portan de unrar: son Ppmd7Model y Ppmd7aDecoder de
// PrZip.Codecs, ports de C/Ppmd7.c y C/Ppmd7aDec.c del LZMA SDK (dominio público).
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

using PrZip.Codecs.Ppmd;

namespace PrZip.Rar;

internal sealed class UnpackFilter30
{
    public uint BlockStart;
    public uint BlockLength;
    public bool NextWindow;
    public int ParentFilter;
    public readonly VMPreparedProgram Prg = new();
}

internal sealed partial class Unpack : IPpmdByteSource
{
    private const int BlockLz = 0;
    private const int BlockPpm = 1;

    private static readonly byte[] LDecode30 = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224];
    private static readonly byte[] LBits30 = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5];
    private static readonly byte[] SDDecode30 = [0, 4, 8, 16, 32, 64, 128, 192];
    private static readonly byte[] SDBits30 = [2, 2, 3, 4, 5, 6, 6, 6];
    private static readonly int[] DDecode30 = new int[PackDef.DC30];
    private static readonly byte[] DBits30 = new byte[PackDef.DC30];

    static Unpack()
    {
        int[] dBitLengthCounts = [4, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 14, 0, 12];
        int dist = 0, bitLength = 0, slot = 0;
        for (int i = 0; i < dBitLengthCounts.Length; i++, bitLength++)
        {
            for (int j = 0; j < dBitLengthCounts[i]; j++, slot++, dist += 1 << bitLength)
            {
                DDecode30[slot] = dist;
                DBits30[slot] = (byte)bitLength;
            }
        }
    }

    private readonly Ppmd7aDecoder _ppm = new();
    private int _ppmEscChar;
    private int _prevLowDist, _lowDistRepCount;
    private readonly byte[] _unpOldTable = new byte[PackDef.HuffTableSize30];
    private int _unpBlockType;

    private readonly RarVM _vm = new();
    private readonly BitInput _vmCodeInp = new();
    private readonly List<UnpackFilter30> _filters30 = [];
    private readonly List<UnpackFilter30?> _prgStack = [];
    private readonly List<uint> _oldFilterLengths = [];
    private int _lastFilter;

    /// <summary>Byte siguiente de los datos comprimidos (GetChar de unrar), usado por PPMd.</summary>
    public byte ReadByte() => GetChar();

    private byte GetChar()
    {
        if (_inp.InAddr > BitInput.MaxSize - 30)
        {
            UnpReadBuf();
            if (_inp.InAddr >= BitInput.MaxSize)
            {
                return 0;
            }
        }

        return _inp.InBuf[_inp.InAddr++];
    }

    /// <summary>Lee la cabecera de un bloque PPMd y prepara el modelo (ModelPPM::DecodeInit).</summary>
    private bool PpmDecodeInit(ref int escChar)
    {
        int maxOrder = GetChar();
        bool reset = (maxOrder & 0x20) != 0;
        int maxMB = 0;
        if (reset)
        {
            maxMB = GetChar();
        }
        else if (!_ppm.HasModel)
        {
            return false;
        }

        if ((maxOrder & 0x40) != 0)
        {
            escChar = GetChar();
        }

        _ppm.InitRangeDecoder(this);
        if (reset)
        {
            maxOrder = (maxOrder & 0x1f) + 1;
            if (maxOrder > 16)
            {
                maxOrder = 16 + (maxOrder - 16) * 3;
            }

            if (maxOrder == 1)
            {
                _ppm.Reset();
                return false;
            }

            _ppm.StartModel((uint)(maxMB + 1) << 20, (uint)maxOrder);
        }

        return _ppm.HasModel;
    }

    /// <summary>Un símbolo PPMd o -1 si los datos están dañados (ModelPPM::DecodeChar).</summary>
    private int PpmDecodeChar()
    {
        int ch = _ppm.DecodeSymbol();
        return ch < 0 ? -1 : ch;
    }

    /// <summary>Reinicia PPMd tras datos dañados (ModelPPM::CleanUp): un modelo mínimo de 1 MB y orden 2.</summary>
    private void PpmCleanUp() => _ppm.StartModel(1 << 20, 2);

    private int SafePPMDecodeChar()
    {
        int ch = PpmDecodeChar();
        if (ch == -1)
        {
            PpmCleanUp();
            _unpBlockType = BlockLz;
        }

        return ch;
    }

    private void Unpack29(bool solid)
    {
        uint bits;
        _fileExtracted = true;

        UnpInitData(solid);
        if (!UnpReadBuf30())
        {
            return;
        }

        if ((!solid || !_tablesRead3) && !ReadTables30())
        {
            return;
        }

        var window = _window!;
        var inp = _inp;
        while (true)
        {
            _unpPtr &= MaxWinMask;
            _firstWinDone |= _prevPtr > _unpPtr;
            _prevPtr = _unpPtr;

            if (inp.InAddr > _readBorder && !UnpReadBuf30())
            {
                break;
            }

            if (((_wrPtr - _unpPtr) & MaxWinMask) <= PackDef.Max3IncLzMatch && _wrPtr != _unpPtr)
            {
                UnpWriteBuf30();
                if (_writtenFileSize > _destUnpSize)
                {
                    return;
                }
            }

            if (_unpBlockType == BlockPpm)
            {
                int ch = PpmDecodeChar();
                if (ch == -1)
                {
                    PpmCleanUp();
                    _unpBlockType = BlockLz;
                    break;
                }

                if (ch == _ppmEscChar)
                {
                    int nextCh = SafePPMDecodeChar();
                    if (nextCh == 0)
                    {
                        if (!ReadTables30())
                        {
                            break;
                        }

                        continue;
                    }

                    if (nextCh == -1 || nextCh == 2)
                    {
                        break;
                    }

                    if (nextCh == 3)
                    {
                        if (!ReadVMCodePPM())
                        {
                            break;
                        }

                        continue;
                    }

                    if (nextCh == 4)
                    {
                        uint distance = 0, length = 0;
                        bool failed = false;
                        for (int i = 0; i < 4 && !failed; i++)
                        {
                            int c = SafePPMDecodeChar();
                            if (c == -1)
                            {
                                failed = true;
                            }
                            else if (i == 3)
                            {
                                length = (byte)c;
                            }
                            else
                            {
                                distance = (distance << 8) + (byte)c;
                            }
                        }

                        if (failed)
                        {
                            break;
                        }

                        CopyString(length + 32, distance + 2);
                        continue;
                    }

                    if (nextCh == 5)
                    {
                        int length = SafePPMDecodeChar();
                        if (length == -1)
                        {
                            break;
                        }

                        CopyString((uint)(length + 4), 1);
                        continue;
                    }

                    // nextCh == 1: el byte es igual al de escape y se guarda tal cual.
                }

                window[_unpPtr++] = (byte)ch;
                continue;
            }

            uint number = DecodeNumber(inp, _blockTables.LD);
            if (number < 256)
            {
                window[_unpPtr++] = (byte)number;
                continue;
            }

            if (number >= 271)
            {
                number -= 271;
                uint length = LDecode30[number] + 3u;
                if ((bits = LBits30[number]) > 0)
                {
                    length += inp.GetBits() >> (int)(16 - bits);
                    inp.AddBits(bits);
                }

                uint distNumber = DecodeNumber(inp, _blockTables.DD);
                uint distance = (uint)DDecode30[distNumber] + 1;
                if ((bits = DBits30[distNumber]) > 0)
                {
                    if (distNumber > 9)
                    {
                        if (bits > 4)
                        {
                            distance += (inp.GetBits() >> (int)(20 - bits)) << 4;
                            inp.AddBits(bits - 4);
                        }

                        if (_lowDistRepCount > 0)
                        {
                            _lowDistRepCount--;
                            distance += (uint)_prevLowDist;
                        }
                        else
                        {
                            uint lowDist = DecodeNumber(inp, _blockTables.LDD);
                            if (lowDist == 16)
                            {
                                _lowDistRepCount = (int)PackDef.LowDistRepCount - 1;
                                distance += (uint)_prevLowDist;
                            }
                            else
                            {
                                distance += lowDist;
                                _prevLowDist = (int)lowDist;
                            }
                        }
                    }
                    else
                    {
                        distance += inp.GetBits() >> (int)(16 - bits);
                        inp.AddBits(bits);
                    }
                }

                if (distance >= 0x2000)
                {
                    length++;
                    if (distance >= 0x40000)
                    {
                        length++;
                    }
                }

                InsertOldDist(distance);
                _lastLength = length;
                CopyString(length, distance);
                continue;
            }

            if (number == 256)
            {
                if (!ReadEndOfBlock())
                {
                    break;
                }

                continue;
            }

            if (number == 257)
            {
                if (!ReadVMCode())
                {
                    break;
                }

                continue;
            }

            if (number == 258)
            {
                if (_lastLength != 0)
                {
                    CopyString(_lastLength, _oldDist[0]);
                }

                continue;
            }

            if (number < 263)
            {
                uint distNum = number - 259;
                uint distance = (uint)_oldDist[distNum];
                for (uint i = distNum; i > 0; i--)
                {
                    _oldDist[i] = _oldDist[i - 1];
                }

                _oldDist[0] = distance;

                uint lengthNumber = DecodeNumber(inp, _blockTables.RD);
                uint length = LDecode30[lengthNumber] + 2u;
                if ((bits = LBits30[lengthNumber]) > 0)
                {
                    length += inp.GetBits() >> (int)(16 - bits);
                    inp.AddBits(bits);
                }

                _lastLength = length;
                CopyString(length, distance);
                continue;
            }

            if (number < 272)
            {
                number -= 263;
                uint distance = SDDecode30[number] + 1u;
                if ((bits = SDBits30[number]) > 0)
                {
                    distance += inp.GetBits() >> (int)(16 - bits);
                    inp.AddBits(bits);
                }

                InsertOldDist(distance);
                _lastLength = 2;
                CopyString(2, distance);
                continue;
            }
        }

        UnpWriteBuf30();
    }

    private bool ReadEndOfBlock()
    {
        uint bitField = _inp.GetBits();
        bool newTable, newFile = false;
        if ((bitField & 0x8000) != 0)
        {
            newTable = true;
            _inp.AddBits(1);
        }
        else
        {
            newFile = true;
            newTable = (bitField & 0x4000) != 0;
            _inp.AddBits(2);
        }

        _tablesRead3 = !newTable;
        if (newFile)
        {
            return false;
        }

        return ReadTables30();
    }

    private bool ReadVMCode()
    {
        uint firstByte = _inp.GetBits() >> 8;
        _inp.AddBits(8);
        uint length = (firstByte & 7) + 1;
        if (length == 7)
        {
            length = (_inp.GetBits() >> 8) + 7;
            _inp.AddBits(8);
        }
        else if (length == 8)
        {
            length = _inp.GetBits();
            _inp.AddBits(16);
        }

        if (length == 0)
        {
            return false;
        }

        var vmCode = new byte[length];
        for (uint i = 0; i < length; i++)
        {
            if (_inp.InAddr >= _readTop - 1 && !UnpReadBuf30() && i < length - 1)
            {
                return false;
            }

            vmCode[i] = (byte)(_inp.GetBits() >> 8);
            _inp.AddBits(8);
        }

        return AddVMCode(firstByte, vmCode);
    }

    private bool ReadVMCodePPM()
    {
        int firstByteI = SafePPMDecodeChar();
        if (firstByteI == -1)
        {
            return false;
        }

        uint firstByte = (uint)firstByteI;
        uint length = (firstByte & 7) + 1;
        if (length == 7)
        {
            int b1 = SafePPMDecodeChar();
            if (b1 == -1)
            {
                return false;
            }

            length = (uint)(b1 + 7);
        }
        else if (length == 8)
        {
            int b1 = SafePPMDecodeChar();
            if (b1 == -1)
            {
                return false;
            }

            int b2 = SafePPMDecodeChar();
            if (b2 == -1)
            {
                return false;
            }

            length = (uint)(b1 * 256 + b2);
        }

        if (length == 0)
        {
            return false;
        }

        var vmCode = new byte[length];
        for (uint i = 0; i < length; i++)
        {
            int ch = SafePPMDecodeChar();
            if (ch == -1)
            {
                return false;
            }

            vmCode[i] = (byte)ch;
        }

        return AddVMCode(firstByte, vmCode);
    }

    private bool AddVMCode(uint firstByte, byte[] code)
    {
        uint codeSize = (uint)code.Length;
        _vmCodeInp.InitBitInput();
        Array.Clear(_vmCodeInp.InBuf);
        Buffer.BlockCopy(code, 0, _vmCodeInp.InBuf, 0, (int)Math.Min(BitInput.MaxSize, codeSize));

        uint filtPos;
        if ((firstByte & 0x80) != 0)
        {
            filtPos = RarVM.ReadData(_vmCodeInp);
            if (filtPos == 0)
            {
                InitFilters30(false);
            }
            else
            {
                filtPos--;
            }
        }
        else
        {
            filtPos = (uint)_lastFilter;
        }

        if (filtPos > _filters30.Count || filtPos > _oldFilterLengths.Count)
        {
            return false;
        }

        _lastFilter = (int)filtPos;
        bool newFilter = filtPos == _filters30.Count;

        var stackFilter = new UnpackFilter30();
        UnpackFilter30 filter;
        if (newFilter)
        {
            if (filtPos > PackDef.Max3UnpackFilters)
            {
                return false;
            }

            stackFilter.ParentFilter = _filters30.Count;
            filter = new UnpackFilter30();
            _filters30.Add(filter);
            _oldFilterLengths.Add(0);
        }
        else
        {
            filter = _filters30[(int)filtPos];
            stackFilter.ParentFilter = (int)filtPos;
        }

        int emptyCount = 0;
        for (int i = 0; i < _prgStack.Count; i++)
        {
            _prgStack[i - emptyCount] = _prgStack[i];
            if (_prgStack[i] == null)
            {
                emptyCount++;
            }

            if (emptyCount > 0)
            {
                _prgStack[i] = null;
            }
        }

        if (emptyCount == 0)
        {
            if (_prgStack.Count > PackDef.Max3UnpackFilters)
            {
                return false;
            }

            _prgStack.Add(null);
            emptyCount = 1;
        }

        int stackPos = _prgStack.Count - emptyCount;
        _prgStack[stackPos] = stackFilter;

        uint blockStart = RarVM.ReadData(_vmCodeInp);
        if ((firstByte & 0x40) != 0)
        {
            blockStart += 258;
        }

        stackFilter.BlockStart = (uint)((blockStart + _unpPtr) & MaxWinMask);
        if ((firstByte & 0x20) != 0)
        {
            stackFilter.BlockLength = RarVM.ReadData(_vmCodeInp);
            _oldFilterLengths[(int)filtPos] = stackFilter.BlockLength;
        }
        else
        {
            stackFilter.BlockLength = filtPos < _oldFilterLengths.Count ? _oldFilterLengths[(int)filtPos] : 0;
        }

        stackFilter.NextWindow = _wrPtr != _unpPtr && ((_wrPtr - _unpPtr) & MaxWinMask) <= blockStart;

        Array.Clear(stackFilter.Prg.InitR);
        stackFilter.Prg.InitR[4] = stackFilter.BlockLength;

        if ((firstByte & 0x10) != 0)
        {
            uint initMask = _vmCodeInp.GetBits() >> 9;
            _vmCodeInp.AddBits(7);
            for (int i = 0; i < 7; i++)
            {
                if ((initMask & (1u << i)) != 0)
                {
                    stackFilter.Prg.InitR[i] = RarVM.ReadData(_vmCodeInp);
                }
            }
        }

        if (newFilter)
        {
            uint vmCodeSize = RarVM.ReadData(_vmCodeInp);
            if (vmCodeSize >= 0x10000 || vmCodeSize == 0 || _vmCodeInp.InAddr + vmCodeSize > codeSize)
            {
                return false;
            }

            var vmCode = new byte[vmCodeSize];
            for (uint i = 0; i < vmCodeSize; i++)
            {
                if (_vmCodeInp.Overflow(3))
                {
                    return false;
                }

                vmCode[i] = (byte)(_vmCodeInp.GetBits() >> 8);
                _vmCodeInp.AddBits(8);
            }

            RarVM.Prepare(vmCode, filter.Prg);
        }

        stackFilter.Prg.Type = filter.Prg.Type;
        return true;
    }

    private bool UnpReadBuf30()
    {
        int dataSize = _readTop - _inp.InAddr;
        if (dataSize < 0)
        {
            return false;
        }

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

        int readCode = UnpRead(_inp.InBuf, dataSize, BitInput.MaxSize - dataSize);
        if (readCode > 0)
        {
            _readTop += readCode;
        }

        _readBorder = _readTop - 30;
        return readCode != -1;
    }

    private void UnpWriteBuf30()
    {
        uint writtenBorder = (uint)_wrPtr;
        uint writeSize = (uint)((_unpPtr - writtenBorder) & MaxWinMask);
        var window = _window!;
        for (int i = 0; i < _prgStack.Count; i++)
        {
            var flt = _prgStack[i];
            if (flt == null)
            {
                continue;
            }

            if (flt.NextWindow)
            {
                flt.NextWindow = false;
                continue;
            }

            uint blockStart = flt.BlockStart;
            uint blockLength = flt.BlockLength;
            if (((blockStart - writtenBorder) & MaxWinMask) < writeSize)
            {
                if (writtenBorder != blockStart)
                {
                    UnpWriteArea(writtenBorder, blockStart);
                    writtenBorder = blockStart;
                    writeSize = (uint)((_unpPtr - writtenBorder) & MaxWinMask);
                }

                if (blockLength <= writeSize)
                {
                    uint blockEnd = (uint)((blockStart + blockLength) & MaxWinMask);
                    if (blockStart < blockEnd || blockEnd == 0)
                    {
                        _vm.SetMemory(0, window, (int)blockStart, (int)blockLength);
                    }
                    else
                    {
                        uint firstPartLength = (uint)(MaxWinSize - blockStart);
                        _vm.SetMemory(0, window, (int)blockStart, (int)firstPartLength);
                        _vm.SetMemory((int)firstPartLength, window, 0, (int)blockEnd);
                    }

                    var prg = flt.Prg;
                    ExecuteCode(prg);

                    int filteredOffset = prg.FilteredDataOffset;
                    uint filteredSize = prg.FilteredDataSize;

                    _prgStack[i] = null;
                    while (i + 1 < _prgStack.Count)
                    {
                        var nextFilter = _prgStack[i + 1];
                        if (nextFilter == null || nextFilter.BlockStart != blockStart ||
                            nextFilter.BlockLength != filteredSize || nextFilter.NextWindow)
                        {
                            break;
                        }

                        // Varios filtros sobre el mismo bloque de datos.
                        if (filteredOffset >= 0)
                        {
                            _vm.SetMemory(0, _vm.Mem, filteredOffset, (int)filteredSize);
                        }

                        var nextPrg = nextFilter.Prg;
                        ExecuteCode(nextPrg);
                        filteredOffset = nextPrg.FilteredDataOffset;
                        filteredSize = nextPrg.FilteredDataSize;
                        i++;
                        _prgStack[i] = null;
                    }

                    if (filteredOffset >= 0)
                    {
                        UnpWriteOut(_vm.Mem.AsSpan(filteredOffset, (int)filteredSize));
                    }

                    _writtenFileSize += filteredSize;
                    writtenBorder = blockEnd;
                    writeSize = (uint)((_unpPtr - writtenBorder) & MaxWinMask);
                }
                else
                {
                    for (int j = i; j < _prgStack.Count; j++)
                    {
                        var f = _prgStack[j];
                        if (f != null && f.NextWindow)
                        {
                            f.NextWindow = false;
                        }
                    }

                    _wrPtr = writtenBorder;
                    return;
                }
            }
        }

        UnpWriteArea(writtenBorder, _unpPtr);
        _wrPtr = _unpPtr;
    }

    private void ExecuteCode(VMPreparedProgram prg)
    {
        prg.InitR[6] = (uint)_writtenFileSize;
        _vm.Execute(prg);
    }

    private bool ReadTables30()
    {
        Span<byte> bitLength = stackalloc byte[PackDef.BC];
        Span<byte> table = stackalloc byte[PackDef.HuffTableSize30];
        if (_inp.InAddr > _readTop - 25 && !UnpReadBuf30())
        {
            return false;
        }

        _inp.AddBits((uint)((8 - _inp.InBit) & 7));
        uint bitField = _inp.GetBits();
        if ((bitField & 0x8000) != 0)
        {
            _unpBlockType = BlockPpm;
            return PpmDecodeInit(ref _ppmEscChar);
        }

        _unpBlockType = BlockLz;
        _prevLowDist = 0;
        _lowDistRepCount = 0;

        if ((bitField & 0x4000) == 0)
        {
            Array.Clear(_unpOldTable);
        }

        _inp.AddBits(2);

        for (int i = 0; i < PackDef.BC; i++)
        {
            uint length = (byte)(_inp.GetBits() >> 12);
            _inp.AddBits(4);
            if (length == 15)
            {
                uint zeroCount = (byte)(_inp.GetBits() >> 12);
                _inp.AddBits(4);
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

        MakeDecodeTables(bitLength, _blockTables.BD, PackDef.BC30);

        const int tableSize = PackDef.HuffTableSize30;
        for (int i = 0; i < tableSize;)
        {
            if (_inp.InAddr > _readTop - 5 && !UnpReadBuf30())
            {
                return false;
            }

            uint number = DecodeNumber(_inp, _blockTables.BD);
            if (number < 16)
            {
                table[i] = (byte)((number + _unpOldTable[i]) & 0xf);
                i++;
            }
            else if (number < 18)
            {
                uint n;
                if (number == 16)
                {
                    n = (_inp.GetBits() >> 13) + 3;
                    _inp.AddBits(3);
                }
                else
                {
                    n = (_inp.GetBits() >> 9) + 11;
                    _inp.AddBits(7);
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
                    n = (_inp.GetBits() >> 13) + 3;
                    _inp.AddBits(3);
                }
                else
                {
                    n = (_inp.GetBits() >> 9) + 11;
                    _inp.AddBits(7);
                }

                while (n-- > 0 && i < tableSize)
                {
                    table[i++] = 0;
                }
            }
        }

        _tablesRead3 = true;
        if (_inp.InAddr > _readTop)
        {
            return false;
        }

        MakeDecodeTables(table, _blockTables.LD, PackDef.NC30);
        MakeDecodeTables(table[PackDef.NC30..], _blockTables.DD, PackDef.DC30);
        MakeDecodeTables(table[(PackDef.NC30 + PackDef.DC30)..], _blockTables.LDD, PackDef.LDC30);
        MakeDecodeTables(table[(PackDef.NC30 + PackDef.DC30 + PackDef.LDC30)..], _blockTables.RD, PackDef.RC30);
        table.CopyTo(_unpOldTable);
        return true;
    }

    private void UnpInitData30(bool solid)
    {
        if (!solid)
        {
            _tablesRead3 = false;
            Array.Clear(_unpOldTable);
            _ppmEscChar = 2;
            _unpBlockType = BlockLz;
        }

        InitFilters30(solid);
    }

    private void InitFilters30(bool solid)
    {
        if (!solid)
        {
            _oldFilterLengths.Clear();
            _lastFilter = 0;
            _filters30.Clear();
        }

        _prgStack.Clear();
    }
}
