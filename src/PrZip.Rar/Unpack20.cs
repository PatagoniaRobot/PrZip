// Port a C# de unrar 7.3.1 (RARLAB), de unpack20.cpp (algoritmo de RAR 2.0, con su modo de audio).
// Copyright (c) Alexander L. Roshal.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

namespace PrZip.Rar;

internal sealed partial class Unpack
{
    private sealed class AudioVariables
    {
        public int K1, K2, K3, K4, K5;
        public int D1, D2, D3, D4;
        public int LastDelta;
        public readonly uint[] Dif = new uint[11];
        public uint ByteCount;
        public int LastChar;

        public void Clear()
        {
            K1 = K2 = K3 = K4 = K5 = 0;
            D1 = D2 = D3 = D4 = 0;
            LastDelta = 0;
            Array.Clear(Dif);
            ByteCount = 0;
            LastChar = 0;
        }
    }

    private static readonly byte[] LDecode20 = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224];
    private static readonly byte[] LBits20 = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5];
    private static readonly uint[] DDecode20 =
    [
        0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048, 3072, 4096,
        6144, 8192, 12288, 16384, 24576, 32768, 49152, 65536, 98304, 131072, 196608, 262144, 327680, 393216, 458752,
        524288, 589824, 655360, 720896, 786432, 851968, 917504, 983040
    ];
    private static readonly byte[] DBits20 =
    [
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15,
        15, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16
    ];
    private static readonly byte[] SDDecode20 = [0, 4, 8, 16, 32, 64, 128, 192];
    private static readonly byte[] SDBits20 = [2, 2, 3, 4, 5, 6, 6, 6];

    private readonly DecodeTable[] _md = [new(), new(), new(), new()];
    private readonly byte[] _unpOldTable20 = new byte[PackDef.MC20 * 4];
    private bool _unpAudioBlock;
    private uint _unpChannels, _unpCurChannel;
    private int _unpChannelDelta;
    private readonly AudioVariables[] _audV = [new(), new(), new(), new()];

    private void CopyString20(uint length, uint distance)
    {
        _lastDist = distance;
        _oldDist[_oldDistPtr++] = distance;
        _oldDistPtr &= 3;
        _lastLength = length;
        _destUnpSize -= length;
        CopyString(length, distance);
    }

    private void Unpack20(bool solid)
    {
        uint bits;
        UnpInitData(solid);
        if (!UnpReadBuf())
        {
            return;
        }

        if ((!solid || !_tablesRead2) && !ReadTables20())
        {
            return;
        }

        --_destUnpSize;

        var window = _window!;
        var inp = _inp;
        while (_destUnpSize >= 0)
        {
            _unpPtr &= MaxWinMask;
            _firstWinDone |= _prevPtr > _unpPtr;
            _prevPtr = _unpPtr;

            if (inp.InAddr > _readTop - 30 && !UnpReadBuf())
            {
                break;
            }

            if (((_wrPtr - _unpPtr) & MaxWinMask) < 270 && _wrPtr != _unpPtr)
            {
                UnpWriteBuf20();
            }

            if (_unpAudioBlock)
            {
                uint audioNumber = DecodeNumber(inp, _md[_unpCurChannel]);
                if (audioNumber == 256)
                {
                    if (!ReadTables20())
                    {
                        break;
                    }

                    continue;
                }

                window[_unpPtr++] = DecodeAudio((int)audioNumber);
                if (++_unpCurChannel == _unpChannels)
                {
                    _unpCurChannel = 0;
                }

                --_destUnpSize;
                continue;
            }

            uint number = DecodeNumber(inp, _blockTables.LD);
            if (number < 256)
            {
                window[_unpPtr++] = (byte)number;
                --_destUnpSize;
                continue;
            }

            if (number > 269)
            {
                number -= 270;
                uint length = LDecode20[number] + 3u;
                if ((bits = LBits20[number]) > 0)
                {
                    length += inp.GetBits() >> (int)(16 - bits);
                    inp.AddBits(bits);
                }

                uint distNumber = DecodeNumber(inp, _blockTables.DD);
                uint distance = DDecode20[distNumber] + 1;
                if ((bits = DBits20[distNumber]) > 0)
                {
                    distance += inp.GetBits() >> (int)(16 - bits);
                    inp.AddBits(bits);
                }

                if (distance >= 0x2000)
                {
                    length++;
                    if (distance >= 0x40000)
                    {
                        length++;
                    }
                }

                CopyString20(length, distance);
                continue;
            }

            if (number == 269)
            {
                if (!ReadTables20())
                {
                    break;
                }

                continue;
            }

            if (number == 256)
            {
                CopyString20(_lastLength, _lastDist);
                continue;
            }

            if (number < 261)
            {
                uint distance = (uint)_oldDist[(_oldDistPtr - (number - 256)) & 3];
                uint lengthNumber = DecodeNumber(inp, _blockTables.RD);
                uint length = LDecode20[lengthNumber] + 2u;
                if ((bits = LBits20[lengthNumber]) > 0)
                {
                    length += inp.GetBits() >> (int)(16 - bits);
                    inp.AddBits(bits);
                }

                if (distance >= 0x101)
                {
                    length++;
                    if (distance >= 0x2000)
                    {
                        length++;
                        if (distance >= 0x40000)
                        {
                            length++;
                        }
                    }
                }

                CopyString20(length, distance);
                continue;
            }

            if (number < 270)
            {
                number -= 261;
                uint distance = SDDecode20[number] + 1u;
                if ((bits = SDBits20[number]) > 0)
                {
                    distance += inp.GetBits() >> (int)(16 - bits);
                    inp.AddBits(bits);
                }

                CopyString20(2, distance);
                continue;
            }
        }

        ReadLastTables();
        UnpWriteBuf20();
    }

    private void UnpWriteBuf20()
    {
        var window = _window!;
        if (_unpPtr < _wrPtr)
        {
            UnpWriteOut(window.AsSpan((int)_wrPtr, (int)(MaxWinSize - _wrPtr)));
            UnpWriteOut(window.AsSpan(0, (int)_unpPtr));
        }
        else
        {
            UnpWriteOut(window.AsSpan((int)_wrPtr, (int)(_unpPtr - _wrPtr)));
        }

        _wrPtr = _unpPtr;
    }

    private bool ReadTables20()
    {
        Span<byte> bitLength = stackalloc byte[PackDef.BC20];
        Span<byte> table = stackalloc byte[PackDef.MC20 * 4];
        if (_inp.InAddr > _readTop - 25 && !UnpReadBuf())
        {
            return false;
        }

        uint bitField = _inp.GetBits();
        _unpAudioBlock = (bitField & 0x8000) != 0;

        if ((bitField & 0x4000) == 0)
        {
            Array.Clear(_unpOldTable20);
        }

        _inp.AddBits(2);

        int tableSize;
        if (_unpAudioBlock)
        {
            _unpChannels = ((bitField >> 12) & 3) + 1;
            if (_unpCurChannel >= _unpChannels)
            {
                _unpCurChannel = 0;
            }

            _inp.AddBits(2);
            tableSize = PackDef.MC20 * (int)_unpChannels;
        }
        else
        {
            tableSize = PackDef.NC20 + PackDef.DC20 + PackDef.RC20;
        }

        for (int i = 0; i < PackDef.BC20; i++)
        {
            bitLength[i] = (byte)(_inp.GetBits() >> 12);
            _inp.AddBits(4);
        }

        MakeDecodeTables(bitLength, _blockTables.BD, PackDef.BC20);
        for (int i = 0; i < tableSize;)
        {
            if (_inp.InAddr > _readTop - 5 && !UnpReadBuf())
            {
                return false;
            }

            uint number = DecodeNumber(_inp, _blockTables.BD);
            if (number < 16)
            {
                table[i] = (byte)((number + _unpOldTable20[i]) & 0xf);
                i++;
            }
            else if (number == 16)
            {
                uint n = (_inp.GetBits() >> 14) + 3;
                _inp.AddBits(2);
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
                if (number == 17)
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

        _tablesRead2 = true;
        if (_inp.InAddr > _readTop)
        {
            return true;
        }

        if (_unpAudioBlock)
        {
            for (int i = 0; i < _unpChannels; i++)
            {
                MakeDecodeTables(table[(i * PackDef.MC20)..], _md[i], PackDef.MC20);
            }
        }
        else
        {
            MakeDecodeTables(table, _blockTables.LD, PackDef.NC20);
            MakeDecodeTables(table[PackDef.NC20..], _blockTables.DD, PackDef.DC20);
            MakeDecodeTables(table[(PackDef.NC20 + PackDef.DC20)..], _blockTables.RD, PackDef.RC20);
        }

        table[..tableSize].CopyTo(_unpOldTable20);
        return true;
    }

    private void ReadLastTables()
    {
        if (_readTop >= _inp.InAddr + 5)
        {
            if (_unpAudioBlock)
            {
                if (DecodeNumber(_inp, _md[_unpCurChannel]) == 256)
                {
                    ReadTables20();
                }
            }
            else if (DecodeNumber(_inp, _blockTables.LD) == 269)
            {
                ReadTables20();
            }
        }
    }

    private void UnpInitData20(bool solid)
    {
        if (!solid)
        {
            _tablesRead2 = false;
            _unpAudioBlock = false;
            _unpChannelDelta = 0;
            _unpCurChannel = 0;
            _unpChannels = 1;
            foreach (var v in _audV)
            {
                v.Clear();
            }

            Array.Clear(_unpOldTable20);
            foreach (var t in _md)
            {
                t.Clear();
            }
        }
    }

    private byte DecodeAudio(int delta)
    {
        var v = _audV[_unpCurChannel];
        v.ByteCount++;
        v.D4 = v.D3;
        v.D3 = v.D2;
        v.D2 = v.LastDelta - v.D1;
        v.D1 = v.LastDelta;
        int pch = 8 * v.LastChar + v.K1 * v.D1 + v.K2 * v.D2 + v.K3 * v.D3 + v.K4 * v.D4 + v.K5 * _unpChannelDelta;
        pch = (pch >> 3) & 0xFF;

        uint ch = (uint)(pch - delta);

        int d = (sbyte)(byte)delta;
        d = (int)((uint)d << 3);

        v.Dif[0] += (uint)Math.Abs(d);
        v.Dif[1] += (uint)Math.Abs(d - v.D1);
        v.Dif[2] += (uint)Math.Abs(d + v.D1);
        v.Dif[3] += (uint)Math.Abs(d - v.D2);
        v.Dif[4] += (uint)Math.Abs(d + v.D2);
        v.Dif[5] += (uint)Math.Abs(d - v.D3);
        v.Dif[6] += (uint)Math.Abs(d + v.D3);
        v.Dif[7] += (uint)Math.Abs(d - v.D4);
        v.Dif[8] += (uint)Math.Abs(d + v.D4);
        v.Dif[9] += (uint)Math.Abs(d - _unpChannelDelta);
        v.Dif[10] += (uint)Math.Abs(d + _unpChannelDelta);

        _unpChannelDelta = v.LastDelta = (sbyte)(byte)(ch - (uint)v.LastChar);
        v.LastChar = (int)ch;

        if ((v.ByteCount & 0x1F) == 0)
        {
            uint minDif = v.Dif[0], numMinDif = 0;
            v.Dif[0] = 0;
            for (int i = 1; i < v.Dif.Length; i++)
            {
                if (v.Dif[i] < minDif)
                {
                    minDif = v.Dif[i];
                    numMinDif = (uint)i;
                }

                v.Dif[i] = 0;
            }

            switch (numMinDif)
            {
                case 1: if (v.K1 >= -16) v.K1--; break;
                case 2: if (v.K1 < 16) v.K1++; break;
                case 3: if (v.K2 >= -16) v.K2--; break;
                case 4: if (v.K2 < 16) v.K2++; break;
                case 5: if (v.K3 >= -16) v.K3--; break;
                case 6: if (v.K3 < 16) v.K3++; break;
                case 7: if (v.K4 >= -16) v.K4--; break;
                case 8: if (v.K4 < 16) v.K4++; break;
                case 9: if (v.K5 >= -16) v.K5--; break;
                case 10: if (v.K5 < 16) v.K5++; break;
            }
        }

        return (byte)ch;
    }
}
