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

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace PrZip.Codecs.Filters;

/// <summary>
/// Filtro BCJ para código máquina ARM (32 bits).
/// Convierte instrucciones BL (Branch with Link) con prefijo 0xEB.
/// </summary>
internal sealed class BcjArmFilter
{
    private uint _ip;

    public BcjArmFilter(uint initialIp = 0)
    {
        _ip = initialIp;
    }

    public void Reset(uint initialIp = 0) => _ip = initialIp;

    public int Decode(Span<byte> data) => Process(data, encoding: false);
    public int Encode(Span<byte> data) => Process(data, encoding: true);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int Process(Span<byte> data, bool encoding)
    {
        int limit = data.Length - 4;
        int i = 0;

        for (; i <= limit; i += 4)
        {
            if (data[i + 3] == 0xEB) // Instrucción BL
            {
                uint v = ((uint)data[i + 2] << 16) | ((uint)data[i + 1] << 8) | data[i];
                v <<= 2;
                uint cur = _ip + (uint)i;

                if (encoding)
                {
                    v += cur + 8;
                }
                else
                {
                    v -= cur + 8;
                }

                v >>= 2;
                data[i] = (byte)v;
                data[i + 1] = (byte)(v >> 8);
                data[i + 2] = (byte)(v >> 16);
            }
        }

        _ip += (uint)i;
        return i;
    }
}

/// <summary>
/// Filtro BCJ para código máquina ARM64 (AArch64). Port fiel de z7_BranchConv_ARM64
/// (C/Bra.c del LZMA SDK 24.x, dominio público): convierte BL (imm26) y ADRP (página de 4 KB, imm21).
/// </summary>
internal sealed class BcjArm64Filter
{
    private uint _ip;

    public BcjArm64Filter(uint initialIp = 0)
    {
        _ip = initialIp;
    }

    public void Reset(uint initialIp = 0) => _ip = initialIp;

    public int Decode(Span<byte> data) => Process(data, encoding: false);
    public int Encode(Span<byte> data) => Process(data, encoding: true);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int Process(Span<byte> data, bool encoding)
    {
        const uint flag = 1u << (24 - 4);
        const uint mask = (1u << 24) - (flag << 1);

        int size = data.Length & ~3;
        for (int i = 0; i < size; i += 4)
        {
            uint pc = _ip + (uint)i;
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);

            if (((v - 0x94000000u) & 0xFC000000u) == 0)
            {
                // BL: el inmediato de 26 bits cuenta instrucciones (pc / 4).
                uint c = pc >> 2;
                v = encoding ? v + c : v - c;
                v &= 0x03FFFFFFu;
                v |= 0x94000000u;
                BinaryPrimitives.WriteUInt32LittleEndian(data[i..], v);
                continue;
            }

            v -= 0x90000000u;
            if ((v & 0x9F000000u) == 0)
            {
                // ADRP: solo se convierte si el inmediato cae en el rango que el encoder acepta.
                v += flag;
                if ((v & mask) != 0)
                {
                    continue;
                }

                uint z = (v & 0xFFFFFFE0u) | (v >> 26);
                uint c = (pc >> (12 - 3)) & ~7u;
                z = encoding ? z + c : z - c;
                v &= 0x1F;
                v |= 0x90000000u;
                v |= z << 26;
                v |= 0x00FFFFE0u & ((z & ((flag << 1) - 1)) - flag);
                BinaryPrimitives.WriteUInt32LittleEndian(data[i..], v);
            }
        }

        _ip += (uint)size;
        return size;
    }
}
