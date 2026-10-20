// =============================================================================
//  PrZip — Patagonia Robot Archive Engine
//  Lee y escribe 7z; abre RAR, TAR, GZip, BZip2, XZ e ISO 9660.
// =============================================================================
//
//  Copyright (c) 2026 Claudio Andino
//  Developed under the Patagonia Robot initiative
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at:
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
// =============================================================================

using System.Runtime.CompilerServices;

namespace PrZip.Codecs.Lzma;

/// <summary>
/// Buscador de coincidencias BT4 / HC4. Port de C/LzFind.c del LZMA SDK (Igor Pavlov, dominio público):
/// hash con tabla CRC (HASH4_CALC), buffer cíclico de tamaño diccionario + 1 recorrido con un contador
/// en vez de una división por nodo, GetMatchesSpec1 / Hc_GetMatchesSpec / SkipMatchesSpec.
///
/// Trabaja sobre un bloque completo en memoria: las posiciones son índices del bloque desplazados en 1
/// (el 0 de las tablas significa "vacío", como kEmptyHashValue). Devuelve pares (largo, distancia - 1)
/// de largo estrictamente creciente, igual que LzFind.
/// </summary>
internal sealed unsafe class LzMatchFinder
{
    private const uint Hash2Size = 1 << 10;
    private const uint Hash3Size = 1 << 16;
    private const uint Fix3HashSize = Hash2Size;
    private const uint Fix4HashSize = Hash2Size + Hash3Size;
    private const int CrcShift1 = 5;

    private static readonly uint[] Crc = CreateCrcTable();

    private readonly bool _btMode;
    private readonly uint _cyclicBufferSize;
    private readonly uint _hashMask;
    private readonly uint _cutValue;
    private readonly int _matchMaxLen;
    private readonly uint[] _hash;
    private readonly uint[] _son;

    private int _index;          // posición del byte actual en el bloque
    private uint _pos;           // _index + 1
    private uint _cyclicBufferPos;

    public LzMatchFinder(uint dictSize, bool btMode, int cutValue, int matchMaxLen)
    {
        _btMode = btMode;
        _cyclicBufferSize = dictSize + 1;
        _cutValue = (uint)Math.Max(cutValue, 1);
        _matchMaxLen = matchMaxLen;
        _hashMask = GetHashMask(dictSize);
        _hash = new uint[Fix4HashSize + _hashMask + 1];
        _son = new uint[(long)_cyclicBufferSize << (btMode ? 1 : 0)];
        Init();
    }

    /// <summary>MatchFinder_GetHashMask para numHashBytes = 4.</summary>
    private static uint GetHashMask(uint hs)
    {
        if (hs != 0)
        {
            hs--;
        }

        hs |= hs >> 1;
        hs |= hs >> 2;
        hs |= hs >> 4;
        hs |= hs >> 8;
        hs >>= 1;
        if (hs >= (1 << 24))
        {
            hs >>= 1;
        }

        hs |= (1 << 16) - 1;
        return hs;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i;
            for (int j = 0; j < 8; j++)
            {
                r = (r >> 1) ^ (0xEDB88320u & (0u - (r & 1)));
            }

            table[i] = r;
        }

        return table;
    }

    /// <summary>Bloque nuevo: tablas vacías y posición al comienzo.</summary>
    public void Init()
    {
        Array.Clear(_hash);
        _index = 0;
        _pos = 1;
        _cyclicBufferPos = 1;
    }

    public int Position => _index;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MovePos()
    {
        _index++;
        _pos++;
        if (++_cyclicBufferPos == _cyclicBufferSize)
        {
            _cyclicBufferPos = 0;
        }
    }

    /// <summary>
    /// Coincidencias para la posición actual (y avanza una). Escribe pares (largo, distancia - 1) en
    /// distances y devuelve cuántos valores escribió (2 por coincidencia).
    /// </summary>
    public int GetMatches(ReadOnlySpan<byte> window, uint[] distances)
    {
        int avail = window.Length - _index;
        uint lenLimit = (uint)Math.Min(_matchMaxLen, avail);
        if (lenLimit < 4)
        {
            MovePos();
            return 0;
        }

        fixed (byte* basePtr = window)
        fixed (uint* hash = _hash)
        fixed (uint* son = _son)
        fixed (uint* d0 = distances)
        {
            byte* cur = basePtr + _index;
            uint temp = Crc[cur[0]] ^ cur[1];
            uint h2 = temp & (Hash2Size - 1);
            temp ^= (uint)cur[2] << 8;
            uint h3 = temp & (Hash3Size - 1);
            uint hv = (temp ^ (Crc[cur[3]] << CrcShift1)) & _hashMask;

            uint pos = _pos;
            uint d2 = pos - hash[h2];
            uint d3 = pos - hash[Fix3HashSize + h3];
            uint curMatch = hash[Fix4HashSize + hv];

            hash[h2] = pos;
            hash[Fix3HashSize + h3] = pos;
            hash[Fix4HashSize + hv] = pos;

            uint mmm = _cyclicBufferSize;
            if (pos < mmm)
            {
                mmm = pos;
            }

            uint maxLen = 3;
            uint* d = d0;

            while (true)
            {
                if (d2 < mmm && *(cur - d2) == *cur)
                {
                    d[0] = 2;
                    d[1] = d2 - 1;
                    d += 2;
                    if (*(cur - d2 + 2) == cur[2])
                    {
                        // la coincidencia de 2 bytes ya es de 3: UPDATE_maxLen la extiende
                    }
                    else if (d3 < mmm && *(cur - d3) == *cur)
                    {
                        d2 = d3;
                        d[1] = d3 - 1;
                        d += 2;
                    }
                    else
                    {
                        break;
                    }
                }
                else if (d3 < mmm && *(cur - d3) == *cur)
                {
                    d2 = d3;
                    d[1] = d3 - 1;
                    d += 2;
                }
                else
                {
                    break;
                }

                // UPDATE_maxLen
                byte* c = cur + maxLen;
                byte* lim = cur + lenLimit;
                while (c != lim && *(c - d2) == *c)
                {
                    c++;
                }

                maxLen = (uint)(c - cur);
                d[-2] = maxLen;
                if (maxLen == lenLimit)
                {
                    if (_btMode)
                    {
                        SkipMatchesSpec(lenLimit, curMatch, pos, cur, son);
                    }
                    else
                    {
                        son[_cyclicBufferPos] = curMatch;
                    }

                    MovePos();
                    return (int)(d - d0);
                }

                break;
            }

            d = _btMode
                ? GetMatchesSpec1(lenLimit, curMatch, pos, cur, son, d, maxLen)
                : HcGetMatchesSpec(lenLimit, curMatch, pos, cur, son, d, maxLen);

            MovePos();
            return (int)(d - d0);
        }
    }

    /// <summary>Inserta num posiciones sin informar coincidencias.</summary>
    public void Skip(ReadOnlySpan<byte> window, int num)
    {
        fixed (byte* basePtr = window)
        fixed (uint* hash = _hash)
        fixed (uint* son = _son)
        {
            for (; num > 0; num--)
            {
                int avail = window.Length - _index;
                uint lenLimit = (uint)Math.Min(_matchMaxLen, avail);
                if (lenLimit < 4)
                {
                    MovePos();
                    continue;
                }

                byte* cur = basePtr + _index;
                uint temp = Crc[cur[0]] ^ cur[1];
                uint h2 = temp & (Hash2Size - 1);
                temp ^= (uint)cur[2] << 8;
                uint h3 = temp & (Hash3Size - 1);
                uint hv = (temp ^ (Crc[cur[3]] << CrcShift1)) & _hashMask;

                uint pos = _pos;
                uint curMatch = hash[Fix4HashSize + hv];
                hash[h2] = pos;
                hash[Fix3HashSize + h3] = pos;
                hash[Fix4HashSize + hv] = pos;

                if (_btMode)
                {
                    SkipMatchesSpec(lenLimit, curMatch, pos, cur, son);
                }
                else
                {
                    son[_cyclicBufferPos] = curMatch;
                }

                MovePos();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint CyclicIndex(uint delta) =>
        _cyclicBufferPos - delta + (_cyclicBufferPos < delta ? _cyclicBufferSize : 0);

    private uint* HcGetMatchesSpec(uint lenLimit, uint curMatch, uint pos, byte* cur, uint* son, uint* d, uint maxLen)
    {
        byte* lim = cur + lenLimit;
        son[_cyclicBufferPos] = curMatch;
        uint cutValue = _cutValue;

        do
        {
            if (curMatch == 0)
            {
                break;
            }

            uint delta = pos - curMatch;
            if (delta >= _cyclicBufferSize)
            {
                break;
            }

            curMatch = son[CyclicIndex(delta)];
            nint diff = -(nint)delta;
            if (cur[maxLen] == cur[(nint)maxLen + diff])
            {
                byte* c = cur;
                while (*c == c[diff])
                {
                    if (++c == lim)
                    {
                        d[0] = (uint)(lim - cur);
                        d[1] = delta - 1;
                        return d + 2;
                    }
                }

                uint len = (uint)(c - cur);
                if (maxLen < len)
                {
                    maxLen = len;
                    d[0] = len;
                    d[1] = delta - 1;
                    d += 2;
                }
            }
        }
        while (--cutValue != 0);

        return d;
    }

    private uint* GetMatchesSpec1(uint lenLimit, uint curMatch, uint pos, byte* cur, uint* son, uint* d, uint maxLen)
    {
        uint* ptr0 = son + ((nuint)_cyclicBufferPos << 1) + 1;
        uint* ptr1 = son + ((nuint)_cyclicBufferPos << 1);
        uint len0 = 0;
        uint len1 = 0;
        uint cutValue = _cutValue;

        uint cmCheck = pos < _cyclicBufferSize ? 0 : pos - _cyclicBufferSize;

        if (cmCheck < curMatch)
        {
            do
            {
                uint delta = pos - curMatch;
                uint* pair = son + ((nuint)CyclicIndex(delta) << 1);
                byte* pb = cur - delta;
                uint len = len0 < len1 ? len0 : len1;
                uint pair0 = pair[0];
                if (pb[len] == cur[len])
                {
                    if (++len != lenLimit && pb[len] == cur[len])
                    {
                        while (++len != lenLimit)
                        {
                            if (pb[len] != cur[len])
                            {
                                break;
                            }
                        }
                    }

                    if (maxLen < len)
                    {
                        maxLen = len;
                        d[0] = len;
                        d[1] = delta - 1;
                        d += 2;
                        if (len == lenLimit)
                        {
                            *ptr1 = pair0;
                            *ptr0 = pair[1];
                            return d;
                        }
                    }
                }

                if (pb[len] < cur[len])
                {
                    *ptr1 = curMatch;
                    curMatch = pair[1];
                    ptr1 = pair + 1;
                    len1 = len;
                }
                else
                {
                    *ptr0 = curMatch;
                    curMatch = pair[0];
                    ptr0 = pair;
                    len0 = len;
                }
            }
            while (--cutValue != 0 && cmCheck < curMatch);
        }

        *ptr0 = 0;
        *ptr1 = 0;
        return d;
    }

    private void SkipMatchesSpec(uint lenLimit, uint curMatch, uint pos, byte* cur, uint* son)
    {
        uint* ptr0 = son + ((nuint)_cyclicBufferPos << 1) + 1;
        uint* ptr1 = son + ((nuint)_cyclicBufferPos << 1);
        uint len0 = 0;
        uint len1 = 0;
        uint cutValue = _cutValue;

        uint cmCheck = pos < _cyclicBufferSize ? 0 : pos - _cyclicBufferSize;

        if (cmCheck < curMatch)
        {
            do
            {
                uint delta = pos - curMatch;
                uint* pair = son + ((nuint)CyclicIndex(delta) << 1);
                byte* pb = cur - delta;
                uint len = len0 < len1 ? len0 : len1;
                if (pb[len] == cur[len])
                {
                    while (++len != lenLimit)
                    {
                        if (pb[len] != cur[len])
                        {
                            break;
                        }
                    }

                    if (len == lenLimit)
                    {
                        *ptr1 = pair[0];
                        *ptr0 = pair[1];
                        return;
                    }
                }

                if (pb[len] < cur[len])
                {
                    *ptr1 = curMatch;
                    curMatch = pair[1];
                    ptr1 = pair + 1;
                    len1 = len;
                }
                else
                {
                    *ptr0 = curMatch;
                    curMatch = pair[0];
                    ptr0 = pair;
                    len0 = len;
                }
            }
            while (--cutValue != 0 && cmCheck < curMatch);
        }

        *ptr0 = 0;
        *ptr1 = 0;
    }
}
