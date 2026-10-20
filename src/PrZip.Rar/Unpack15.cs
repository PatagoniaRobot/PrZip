// Port a C# de unrar 7.3.1 (RARLAB), de unpack15.cpp (algoritmo de RAR 1.3–1.5). Copyright (c)
// Alexander L. Roshal.
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
    private const uint StartL1 = 2;
    private static readonly uint[] DecL1 = [0x8000, 0xa000, 0xc000, 0xd000, 0xe000, 0xea00, 0xee00, 0xf000, 0xf200, 0xf200, 0xffff];
    private static readonly uint[] PosL1 = [0, 0, 0, 2, 3, 5, 7, 11, 16, 20, 24, 32, 32];

    private const uint StartL2 = 3;
    private static readonly uint[] DecL2 = [0xa000, 0xc000, 0xd000, 0xe000, 0xea00, 0xee00, 0xf000, 0xf200, 0xf240, 0xffff];
    private static readonly uint[] PosL2 = [0, 0, 0, 0, 5, 7, 9, 13, 18, 22, 26, 34, 36];

    private const uint StartHf0 = 4;
    private static readonly uint[] DecHf0 = [0x8000, 0xc000, 0xe000, 0xf200, 0xf200, 0xf200, 0xf200, 0xf200, 0xffff];
    private static readonly uint[] PosHf0 = [0, 0, 0, 0, 0, 8, 16, 24, 33, 33, 33, 33, 33];

    private const uint StartHf1 = 5;
    private static readonly uint[] DecHf1 = [0x2000, 0xc000, 0xe000, 0xf000, 0xf200, 0xf200, 0xf7e0, 0xffff];
    private static readonly uint[] PosHf1 = [0, 0, 0, 0, 0, 0, 4, 44, 60, 76, 80, 80, 127];

    private const uint StartHf2 = 5;
    private static readonly uint[] DecHf2 = [0x1000, 0x2400, 0x8000, 0xc000, 0xfa00, 0xffff, 0xffff, 0xffff];
    private static readonly uint[] PosHf2 = [0, 0, 0, 0, 0, 0, 2, 7, 53, 117, 233, 0, 0];

    private const uint StartHf3 = 6;
    private static readonly uint[] DecHf3 = [0x800, 0x2400, 0xee00, 0xfe80, 0xffff, 0xffff, 0xffff];
    private static readonly uint[] PosHf3 = [0, 0, 0, 0, 0, 0, 0, 2, 16, 218, 251, 0, 0];

    private const uint StartHf4 = 8;
    private static readonly uint[] DecHf4 = [0xff00, 0xffff, 0xffff, 0xffff, 0xffff, 0xffff];
    private static readonly uint[] PosHf4 = [0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 0, 0, 0];

    private static readonly uint[] ShortLen1 = [1, 3, 4, 4, 5, 6, 7, 8, 8, 4, 4, 5, 6, 6, 4, 0];

    // unrar declara 15 elementos y en el caso 16 lee fuera del arreglo; con largo 0 la máscara deja pasar
    // cualquier valor, así que un cero de más reproduce ese caso sin leer memoria ajena.
    private static readonly uint[] ShortXor1 = [0, 0xa0, 0xd0, 0xe0, 0xf0, 0xf8, 0xfc, 0xfe, 0xff, 0xc0, 0x80, 0x90, 0x98, 0x9c, 0xb0, 0];
    private static readonly uint[] ShortLen2 = [2, 3, 3, 3, 4, 4, 5, 6, 6, 4, 4, 5, 6, 6, 4, 0];
    private static readonly uint[] ShortXor2 = [0, 0x40, 0x60, 0xa0, 0xd0, 0xe0, 0xf0, 0xf8, 0xfc, 0xc0, 0x80, 0x90, 0x98, 0x9c, 0xb0, 0];

    private readonly ushort[] _chSet = new ushort[256], _chSetA = new ushort[256], _chSetB = new ushort[256], _chSetC = new ushort[256];
    private readonly byte[] _nToPl = new byte[256], _nToPlB = new byte[256], _nToPlC = new byte[256];
    private uint _flagBuf, _avrPlc, _avrPlcB, _avrLn1, _avrLn2, _avrLn3;
    private int _buf60, _numHuf, _stMode, _lCount, _flagsCnt;
    private uint _nhfb, _nlzb, _maxDist3;

    private void Unpack15(bool solid)
    {
        UnpInitData(solid);
        UnpInitData15(solid);
        UnpReadBuf();
        if (!solid)
        {
            InitHuff();
            _unpPtr = 0;
        }
        else
        {
            _unpPtr = _wrPtr;
        }

        --_destUnpSize;
        if (_destUnpSize >= 0)
        {
            GetFlagsBuf();
            _flagsCnt = 8;
        }

        while (_destUnpSize >= 0)
        {
            _unpPtr &= MaxWinMask;
            _firstWinDone |= _prevPtr > _unpPtr;
            _prevPtr = _unpPtr;

            if (_inp.InAddr > _readTop - 30 && !UnpReadBuf())
            {
                break;
            }

            if (((_wrPtr - _unpPtr) & MaxWinMask) < 270 && _wrPtr != _unpPtr)
            {
                UnpWriteBuf20();
            }

            if (_stMode != 0)
            {
                HuffDecode();
                continue;
            }

            if (--_flagsCnt < 0)
            {
                GetFlagsBuf();
                _flagsCnt = 7;
            }

            if ((_flagBuf & 0x80) != 0)
            {
                _flagBuf <<= 1;
                if (_nlzb > _nhfb)
                {
                    LongLZ();
                }
                else
                {
                    HuffDecode();
                }
            }
            else
            {
                _flagBuf <<= 1;
                if (--_flagsCnt < 0)
                {
                    GetFlagsBuf();
                    _flagsCnt = 7;
                }

                if ((_flagBuf & 0x80) != 0)
                {
                    _flagBuf <<= 1;
                    if (_nlzb > _nhfb)
                    {
                        HuffDecode();
                    }
                    else
                    {
                        LongLZ();
                    }
                }
                else
                {
                    _flagBuf <<= 1;
                    ShortLZ();
                }
            }
        }

        UnpWriteBuf20();
    }

    private uint GetShortLen1(uint pos) => pos == 1 ? (uint)(_buf60 + 3) : ShortLen1[pos];

    private uint GetShortLen2(uint pos) => pos == 3 ? (uint)(_buf60 + 3) : ShortLen2[pos];

    private void ShortLZ()
    {
        uint length, saveLength, lastDistance, distance;
        int distancePlace;
        _numHuf = 0;

        uint bitField = _inp.GetBits();
        if (_lCount == 2)
        {
            _inp.AddBits(1);
            if (bitField >= 0x8000)
            {
                CopyString15(_lastDist, _lastLength);
                return;
            }

            bitField <<= 1;
            _lCount = 0;
        }

        bitField >>= 8;

        if (_avrLn1 < 37)
        {
            for (length = 0; ; length++)
            {
                if (((bitField ^ ShortXor1[length]) & ~(0xffu >> (int)GetShortLen1(length))) == 0)
                {
                    break;
                }
            }

            _inp.AddBits(GetShortLen1(length));
        }
        else
        {
            for (length = 0; ; length++)
            {
                if (((bitField ^ ShortXor2[length]) & ~(0xffu >> (int)GetShortLen2(length))) == 0)
                {
                    break;
                }
            }

            _inp.AddBits(GetShortLen2(length));
        }

        if (length >= 9)
        {
            if (length == 9)
            {
                _lCount++;
                CopyString15(_lastDist, _lastLength);
                return;
            }

            if (length == 14)
            {
                _lCount = 0;
                length = DecodeNum(_inp.GetBits(), StartL2, DecL2, PosL2) + 5;
                distance = (_inp.GetBits() >> 1) | 0x8000;
                _inp.AddBits(15);
                _lastLength = length;
                _lastDist = distance;
                CopyString15(distance, length);
                return;
            }

            _lCount = 0;
            saveLength = length;
            distance = (uint)_oldDist[(_oldDistPtr - (length - 9)) & 3];
            length = DecodeNum(_inp.GetBits(), StartL1, DecL1, PosL1) + 2;
            if (length == 0x101 && saveLength == 10)
            {
                _buf60 ^= 1;
                return;
            }

            if (distance > 256)
            {
                length++;
            }

            if (distance >= _maxDist3)
            {
                length++;
            }

            _oldDist[_oldDistPtr++] = distance;
            _oldDistPtr &= 3;
            _lastLength = length;
            _lastDist = distance;
            CopyString15(distance, length);
            return;
        }

        _lCount = 0;
        _avrLn1 += length;
        _avrLn1 -= _avrLn1 >> 4;

        distancePlace = (int)(DecodeNum(_inp.GetBits(), StartHf2, DecHf2, PosHf2) & 0xff);
        distance = _chSetA[distancePlace];
        if (--distancePlace != -1)
        {
            lastDistance = _chSetA[distancePlace];
            _chSetA[distancePlace + 1] = (ushort)lastDistance;
            _chSetA[distancePlace] = (ushort)distance;
        }

        length += 2;
        _oldDist[_oldDistPtr++] = ++distance;
        _oldDistPtr &= 3;
        _lastLength = length;
        _lastDist = distance;
        CopyString15(distance, length);
    }

    private void LongLZ()
    {
        uint length, distance, distancePlace, newDistancePlace, oldAvr2, oldAvr3;

        _numHuf = 0;
        _nlzb += 16;
        if (_nlzb > 0xff)
        {
            _nlzb = 0x90;
            _nhfb >>= 1;
        }

        oldAvr2 = _avrLn2;

        uint bitField = _inp.GetBits();
        if (_avrLn2 >= 122)
        {
            length = DecodeNum(bitField, StartL2, DecL2, PosL2);
        }
        else if (_avrLn2 >= 64)
        {
            length = DecodeNum(bitField, StartL1, DecL1, PosL1);
        }
        else if (bitField < 0x100)
        {
            length = bitField;
            _inp.AddBits(16);
        }
        else
        {
            for (length = 0; ((bitField << (int)length) & 0x8000) == 0; length++)
            {
            }

            _inp.AddBits(length + 1);
        }

        _avrLn2 += length;
        _avrLn2 -= _avrLn2 >> 5;

        bitField = _inp.GetBits();
        if (_avrPlcB > 0x28ff)
        {
            distancePlace = DecodeNum(bitField, StartHf2, DecHf2, PosHf2);
        }
        else if (_avrPlcB > 0x6ff)
        {
            distancePlace = DecodeNum(bitField, StartHf1, DecHf1, PosHf1);
        }
        else
        {
            distancePlace = DecodeNum(bitField, StartHf0, DecHf0, PosHf0);
        }

        _avrPlcB += distancePlace;
        _avrPlcB -= _avrPlcB >> 8;
        while (true)
        {
            distance = _chSetB[distancePlace & 0xff];
            newDistancePlace = _nToPlB[distance++ & 0xff]++;
            if ((distance & 0xff) == 0)
            {
                CorrHuff(_chSetB, _nToPlB);
            }
            else
            {
                break;
            }
        }

        _chSetB[distancePlace & 0xff] = _chSetB[newDistancePlace];
        _chSetB[newDistancePlace] = (ushort)distance;

        distance = ((distance & 0xff00) | (_inp.GetBits() >> 8)) >> 1;
        _inp.AddBits(7);

        oldAvr3 = _avrLn3;
        if (length != 1 && length != 4)
        {
            if (length == 0 && distance <= _maxDist3)
            {
                _avrLn3++;
                _avrLn3 -= _avrLn3 >> 8;
            }
            else if (_avrLn3 > 0)
            {
                _avrLn3--;
            }
        }

        length += 3;
        if (distance >= _maxDist3)
        {
            length++;
        }

        if (distance <= 256)
        {
            length += 8;
        }

        _maxDist3 = oldAvr3 > 0xb0 || (_avrPlc >= 0x2a00 && oldAvr2 < 0x40) ? 0x7f00u : 0x2001u;
        _oldDist[_oldDistPtr++] = distance;
        _oldDistPtr &= 3;
        _lastLength = length;
        _lastDist = distance;
        CopyString15(distance, length);
    }

    private void HuffDecode()
    {
        uint curByte, newBytePlace, length, distance;
        int bytePlace;

        uint bitField = _inp.GetBits();

        if (_avrPlc > 0x75ff)
        {
            bytePlace = (int)DecodeNum(bitField, StartHf4, DecHf4, PosHf4);
        }
        else if (_avrPlc > 0x5dff)
        {
            bytePlace = (int)DecodeNum(bitField, StartHf3, DecHf3, PosHf3);
        }
        else if (_avrPlc > 0x35ff)
        {
            bytePlace = (int)DecodeNum(bitField, StartHf2, DecHf2, PosHf2);
        }
        else if (_avrPlc > 0x0dff)
        {
            bytePlace = (int)DecodeNum(bitField, StartHf1, DecHf1, PosHf1);
        }
        else
        {
            bytePlace = (int)DecodeNum(bitField, StartHf0, DecHf0, PosHf0);
        }

        bytePlace &= 0xff;
        if (_stMode != 0)
        {
            if (bytePlace == 0 && bitField > 0xfff)
            {
                bytePlace = 0x100;
            }

            if (--bytePlace == -1)
            {
                bitField = _inp.GetBits();
                _inp.AddBits(1);
                if ((bitField & 0x8000) != 0)
                {
                    _numHuf = _stMode = 0;
                    return;
                }

                length = (bitField & 0x4000) != 0 ? 4u : 3u;
                _inp.AddBits(1);
                distance = DecodeNum(_inp.GetBits(), StartHf2, DecHf2, PosHf2);
                distance = (distance << 5) | (_inp.GetBits() >> 11);
                _inp.AddBits(5);
                CopyString15(distance, length);
                return;
            }
        }
        else if (_numHuf++ >= 16 && _flagsCnt == 0)
        {
            _stMode = 1;
        }

        _avrPlc += (uint)bytePlace;
        _avrPlc -= _avrPlc >> 8;
        _nhfb += 16;
        if (_nhfb > 0xff)
        {
            _nhfb = 0x90;
            _nlzb >>= 1;
        }

        _window![_unpPtr++] = (byte)(_chSet[bytePlace] >> 8);
        --_destUnpSize;

        while (true)
        {
            curByte = _chSet[bytePlace];
            newBytePlace = _nToPl[curByte++ & 0xff]++;
            if ((curByte & 0xff) > 0xa1)
            {
                CorrHuff(_chSet, _nToPl);
            }
            else
            {
                break;
            }
        }

        _chSet[bytePlace] = _chSet[newBytePlace];
        _chSet[newBytePlace] = (ushort)curByte;
    }

    private void GetFlagsBuf()
    {
        uint flags, newFlagsPlace;
        uint flagsPlace = DecodeNum(_inp.GetBits(), StartHf2, DecHf2, PosHf2);

        // La tabla tiene 257 elementos; el 256 solo aparece en un archivo dañado.
        if (flagsPlace >= _chSetC.Length)
        {
            return;
        }

        while (true)
        {
            flags = _chSetC[flagsPlace];
            _flagBuf = flags >> 8;
            newFlagsPlace = _nToPlC[flags++ & 0xff]++;
            if ((flags & 0xff) != 0)
            {
                break;
            }

            CorrHuff(_chSetC, _nToPlC);
        }

        _chSetC[flagsPlace] = _chSetC[newFlagsPlace];
        _chSetC[newFlagsPlace] = (ushort)flags;
    }

    private void UnpInitData15(bool solid)
    {
        if (!solid)
        {
            _avrPlcB = _avrLn1 = _avrLn2 = _avrLn3 = 0;
            _numHuf = _buf60 = 0;
            _avrPlc = 0x3500;
            _maxDist3 = 0x2001;
            _nhfb = _nlzb = 0x80;
        }

        _flagsCnt = 0;
        _flagBuf = 0;
        _stMode = 0;
        _lCount = 0;
        _readTop = 0;
    }

    private void InitHuff()
    {
        for (int i = 0; i < 256; i++)
        {
            _chSet[i] = _chSetB[i] = (ushort)(i << 8);
            _chSetA[i] = (ushort)i;
            _chSetC[i] = (ushort)(((~i + 1) & 0xff) << 8);
        }

        Array.Clear(_nToPl);
        Array.Clear(_nToPlB);
        Array.Clear(_nToPlC);
        CorrHuff(_chSetB, _nToPlB);
    }

    private static void CorrHuff(ushort[] charSet, byte[] numToPlace)
    {
        int p = 0;
        for (int i = 7; i >= 0; i--)
        {
            for (int j = 0; j < 32; j++, p++)
            {
                charSet[p] = (ushort)((charSet[p] & ~0xff) | i);
            }
        }

        Array.Clear(numToPlace);
        for (int i = 6; i >= 0; i--)
        {
            numToPlace[i] = (byte)((7 - i) * 32);
        }
    }

    private void CopyString15(uint distance, uint length)
    {
        var window = _window!;
        _destUnpSize -= length;
        if ((!_firstWinDone && distance > _unpPtr) || distance > MaxWinSize || distance == 0)
        {
            while (length-- > 0)
            {
                window[_unpPtr] = 0;
                _unpPtr = (_unpPtr + 1) & MaxWinMask;
            }
        }
        else
        {
            while (length-- > 0)
            {
                window[_unpPtr] = window[(_unpPtr - distance) & MaxWinMask];
                _unpPtr = (_unpPtr + 1) & MaxWinMask;
            }
        }
    }

    private uint DecodeNum(uint num, uint startPos, uint[] decTab, uint[] posTab)
    {
        int i;
        for (num &= 0xfff0, i = 0; decTab[i] <= num; i++)
        {
            startPos++;
        }

        _inp.AddBits(startPos);
        return ((num - (i != 0 ? decTab[i - 1] : 0)) >> (int)(16 - startPos)) + posTab[startPos];
    }
}
