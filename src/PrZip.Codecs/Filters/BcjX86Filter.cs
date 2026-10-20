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

namespace PrZip.Codecs.Filters;

/// <summary>
/// Filtro BCJ para ejecutables x86 (conversión de saltos CALL 0xE8 y JMP 0xE9).
/// Basado en la implementación canónica de Igor Pavlov (Bra86.c).
/// Mantiene el estado 'prevMask' entre bloques para permitir procesamiento continuo en streaming.
/// </summary>
internal sealed class BcjX86Filter
{
    private static ReadOnlySpan<byte> MaskToAllowedStatus => [1, 1, 1, 0, 1, 0, 0, 0];
    private static ReadOnlySpan<byte> MaskToBitNumber => [0, 1, 2, 2, 3, 3, 3, 3];

    private uint _state;
    private uint _ip;

    public BcjX86Filter(uint initialIp = 0)
    {
        _ip = initialIp;
        _state = 0;
    }

    public uint Ip => _ip;
    public uint State => _state;

    public void Reset(uint initialIp = 0)
    {
        _ip = initialIp;
        _state = 0;
    }

    /// <summary>
    /// Descodifica (reconvierte a saltos relativos) el búfer in-place.
    /// Retorna la cantidad de bytes consumidos/procesados.
    /// </summary>
    public int Decode(Span<byte> data)
    {
        int processed = Convert(data, _ip, ref _state, encoding: false);
        _ip += (uint)processed;
        return processed;
    }

    /// <summary>
    /// Codifica (convierte saltos relativos a absolutos) el búfer in-place.
    /// </summary>
    public int Encode(Span<byte> data)
    {
        int processed = Convert(data, _ip, ref _state, encoding: true);
        _ip += (uint)processed;
        return processed;
    }

    /// <summary>
    /// Port fiel de x86_Convert (C/Bra86.c del LZMA SDK, dominio público).
    /// prevMask registra en sus 3 bits bajos los opcodes E8/E9 vistos en los 3 bytes previos,
    /// de modo que un E8/E9 que cae dentro del operando de otro salto no se convierta.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int Convert(Span<byte> data, uint ip, ref uint state, bool encoding)
    {
        int size = data.Length;
        if (size < 5)
        {
            return 0;
        }

        ip += 5;
        int bufferPos = 0;
        int prevPosT = -1;
        uint prevMask = state & 7;
        int limit = size - 4;

        while (true)
        {
            int p = bufferPos;
            while (p < limit && (data[p] & 0xFE) != 0xE8)
            {
                p++;
            }

            bufferPos = p;
            if (p >= limit)
            {
                break;
            }

            prevPosT = bufferPos - prevPosT;
            if (prevPosT > 3)
            {
                prevMask = 0;
            }
            else
            {
                prevMask = (prevMask << (prevPosT - 1)) & 7;
                if (prevMask != 0)
                {
                    byte b = data[p + 4 - MaskToBitNumber[(int)prevMask]];
                    if (MaskToAllowedStatus[(int)prevMask] == 0 || Test86MsByte(b))
                    {
                        prevPosT = bufferPos;
                        prevMask = ((prevMask << 1) & 7) | 1;
                        bufferPos++;
                        continue;
                    }
                }
            }

            prevPosT = bufferPos;

            if (Test86MsByte(data[p + 4]))
            {
                uint src = ((uint)data[p + 4] << 24) | ((uint)data[p + 3] << 16) | ((uint)data[p + 2] << 8) | data[p + 1];
                uint dest;
                while (true)
                {
                    dest = encoding
                        ? (ip + (uint)bufferPos) + src
                        : src - (ip + (uint)bufferPos);

                    if (prevMask == 0)
                    {
                        break;
                    }

                    int index = MaskToBitNumber[(int)prevMask] * 8;
                    byte b = (byte)(dest >> (24 - index));
                    if (!Test86MsByte(b))
                    {
                        break;
                    }

                    src = dest ^ ((1u << (32 - index)) - 1);
                }

                data[p + 4] = (byte)~(((dest >> 24) & 1) - 1);
                data[p + 3] = (byte)(dest >> 16);
                data[p + 2] = (byte)(dest >> 8);
                data[p + 1] = (byte)dest;
                bufferPos += 5;
            }
            else
            {
                prevMask = ((prevMask << 1) & 7) | 1;
                bufferPos++;
            }
        }

        prevPosT = bufferPos - prevPosT;
        state = prevPosT > 3 ? 0 : ((prevMask << (prevPosT - 1)) & 7);
        return bufferPos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Test86MsByte(byte b)
    {
        return (((b + 1) & 0xFE) == 0); // true para 0x00 y 0xFF
    }
}
