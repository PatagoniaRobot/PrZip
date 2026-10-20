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

namespace PrZip.Codecs.Filters;

/// <summary>
/// Conversores de saltos para ARM Thumb, PowerPC, SPARC, IA-64 y RISC-V.
/// Port fiel de C/Bra.c del LZMA SDK 24.x (Igor Pavlov, dominio público), con índices en lugar de punteros.
/// En el original BR_PC_GET vale (pc + desplazamiento actual de p); aquí se escribe explícito como ip + p.
/// Cada función convierte el búfer completo in-place y devuelve cuántos bytes quedaron procesados;
/// la cola no procesada (menos de una instrucción) se deja intacta, igual que 7-Zip al final del stream.
/// </summary>
internal static class BranchConverters
{
    public static int Ppc(Span<byte> data, uint ip, bool encoding)
    {
        int size = data.Length & ~3;
        for (int p = 0; p < size; p += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32BigEndian(data[p..]);
            if ((v & 0xFC000003u) != 0x48000001u)
            {
                continue;
            }

            uint c = ip + (uint)p;
            v = encoding ? v + c : v - c;
            v &= 0x03FFFFFFu;
            v |= 0x48000000u;
            BinaryPrimitives.WriteUInt32BigEndian(data[p..], v);
        }

        return size;
    }

    public static int Sparc(Span<byte> data, uint ip, bool encoding)
    {
        const uint flag = 1u << 22;
        int size = data.Length & ~3;
        for (int p = 0; p < size; p += 4)
        {
            uint v = BinaryPrimitives.ReadUInt32BigEndian(data[p..]);
            v += 5u << 29;
            v ^= 7u << 29;
            v += flag;
            if ((v & unchecked(0u - (flag << 1))) != 0)
            {
                continue;
            }

            v <<= 2;
            uint c = ip + (uint)p;
            v = encoding ? v + c : v - c;
            v &= (flag << 3) - 1;
            v -= flag << 2;
            v >>= 2;
            v |= 1u << 30;
            BinaryPrimitives.WriteUInt32BigEndian(data[p..], v);
        }

        return size;
    }

    public static int ArmThumb(Span<byte> data, uint ip, bool encoding)
    {
        int size = data.Length & ~1;
        if (size <= 2)
        {
            return 0;
        }

        int lim = size - 2;
        int p = 0;
        do
        {
            uint b1 = data[p + 1];
            while (true)
            {
                if (p >= lim)
                {
                    return p;
                }

                uint b3 = data[p + 3];
                p += 2;
                if ((b3 & (b1 ^ 8)) >= 0xF8)
                {
                    break;
                }

                if (p >= lim)
                {
                    return p;
                }

                b1 = data[p + 3];
                p += 2;
                if ((b1 & (b3 ^ 8)) >= 0xF8)
                {
                    break;
                }
            }

            uint v = ((uint)BinaryPrimitives.ReadUInt16LittleEndian(data[(p - 2)..]) << 11)
                   | (BinaryPrimitives.ReadUInt16LittleEndian(data[p..]) & 0x7FFu);
            p += 2;
            uint c = (ip + (uint)p) >> 1;
            v = encoding ? v + c : v - c;
            BinaryPrimitives.WriteUInt16LittleEndian(data[(p - 4)..], (ushort)(((v >> 11) & 0x7FF) | 0xF000));
            BinaryPrimitives.WriteUInt16LittleEndian(data[(p - 2)..], (ushort)(v | 0xF800));
        }
        while (p < lim);

        return p;
    }

    public static int Ia64(Span<byte> data, uint ip, bool encoding)
    {
        int size = data.Length & ~15;
        uint pc = ip;
        pc -= 1 << 4;
        pc >>= 4 - 1;
        int p = 0;

        while (true)
        {
            uint m;
            while (true)
            {
                if (p == size)
                {
                    return p;
                }

                m = 0x334B0000u >> (data[p] & 0x1E);
                p += 16;
                pc += 1 << 1;
                m &= 3;
                if (m != 0)
                {
                    break;
                }
            }

            p += (int)m * 5 - 20;
            do
            {
                uint t = BinaryPrimitives.ReadUInt16LittleEndian(data[p..]);
                uint z = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 1)..]) >> (int)m;
                p += 5;
                if (((t >> (int)m) & (0x70 << 1)) == 0
                    && ((z - (0x5000000u << 1)) & (0xF000000u << 1)) == 0)
                {
                    uint v = ((0x8FFFFFu << 1) | 1) & z;
                    z ^= v;
                    if (encoding)
                    {
                        pc &= (0x1FFFFFu << 1) | 1;
                        v += pc;
                    }
                    else
                    {
                        pc |= ~((0x1FFFFFu << 1) | 1);
                        v -= pc;
                    }

                    v &= ~(0x600000u << 1);
                    v += 0x700000u << 1;
                    v &= (0x8FFFFFu << 1) | 1;
                    z |= v;
                    z <<= (int)m;
                    BinaryPrimitives.WriteUInt32LittleEndian(data[(p + 1 - 5)..], z);
                }

                m++;
                m &= 3;
            }
            while (m != 0);
        }
    }

    private const uint RiscvRegVal = 2 << 7;
    private const uint RiscvCmdVal = 3;

    private static bool RiscvCheck1(uint v, uint b) =>
        (((b - RiscvCmdVal) ^ (v << 8)) & (0xF8000 + RiscvCmdVal)) == 0;

    private static bool RiscvCheck2(uint v, uint r) =>
        ((v - ((RiscvCmdVal << 12) | RiscvRegVal | 8)) << 18) < (r & 0x1D);

    public static int RiscV(Span<byte> data, uint ip, bool encoding) =>
        encoding ? RiscVEncode(data, ip) : RiscVDecode(data, ip);

    /// <summary>
    /// Bucle de búsqueda común (RISCV_SCAN_LOOP): avanza de a 2 bytes hasta un JAL o AUIPC candidato.
    /// Devuelve false si llegó al límite; en 'a' deja (halfword ^ 0x10) + 1.
    /// </summary>
    private static bool RiscvScan(ReadOnlySpan<byte> data, ref int p, int lim, out uint a)
    {
        while (true)
        {
            a = 0;
            if (p >= lim)
            {
                return false;
            }

            a = (BinaryPrimitives.ReadUInt16LittleEndian(data[p..]) ^ 0x10u) + 1;
            if ((a & 0x77) == 0)
            {
                return true;
            }

            a = (BinaryPrimitives.ReadUInt16LittleEndian(data[(p + 2)..]) ^ 0x10u) + 1;
            p += 4;
            if ((a & 0x77) == 0)
            {
                p -= 2;
                return p < lim;
            }
        }
    }

    private static int RiscVEncode(Span<byte> data, uint ip)
    {
        int size = data.Length & ~1;
        if (size <= 6)
        {
            return 0;
        }

        int lim = size - 6;
        int p = 0;
        while (RiscvScan(data, ref p, lim, out uint a))
        {
            uint v = a;
            a = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);

            if ((v & 8) == 0)
            {
                // JAL
                if (((v - 0x100) & 0xD80) != 0)
                {
                    p += 2;
                    continue;
                }

                v = ((a & (1u << 31)) >> 11)
                  | ((a & (0x3FFu << 21)) >> 20)
                  | ((a & (1u << 20)) >> 9)
                  | (a & (0xFFu << 12));
                v += ip + (uint)p;
                data[p + 1] = (byte)(((v >> 13) & 0xF0) | ((a >> 8) & 0xF));
                data[p + 2] = (byte)(v >> 9);
                data[p + 3] = (byte)(v >> 1);
                p += 4;
                continue;
            }

            // AUIPC
            if ((v & 0xE80) != 0)
            {
                uint b = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 4)..]);
                if (RiscvCheck1(v, b))
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(data[p..], (b << 12) | (0x17 + RiscvRegVal));
                    a &= 0xFFFFF000u;
                    a += (uint)((int)b >> 20);
                    a += ip + (uint)p;
                    BinaryPrimitives.WriteUInt32BigEndian(data[(p + 4)..], a);
                    p += 8;
                }
                else
                {
                    p += 4 + 2;
                }
            }
            else
            {
                uint r = a >> 27;
                if (RiscvCheck2(v, r))
                {
                    v = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 4)..]);
                    r = (r << 7) + 0x17 + (v & 0xFFFFF000u);
                    a = (a >> 12) | (v << 20);
                    BinaryPrimitives.WriteUInt32LittleEndian(data[p..], r);
                    BinaryPrimitives.WriteUInt32LittleEndian(data[(p + 4)..], a);
                    p += 8;
                }
                else
                {
                    p += 4;
                }
            }
        }

        return p;
    }

    private static int RiscVDecode(Span<byte> data, uint ip)
    {
        int size = data.Length & ~1;
        if (size <= 6)
        {
            return 0;
        }

        int lim = size - 6;
        int p = 0;
        while (RiscvScan(data, ref p, lim, out uint a))
        {
            if ((a & 8) == 0)
            {
                // JAL
                a -= 0x100 - 0x7F;
                if ((a & 0xD80) != 0)
                {
                    p += 2;
                    continue;
                }

                uint aOld = (a + (0xEF - 0x7F)) & 0xFFF;
                uint v = ((uint)data[p + 3] << 1)
                       | ((uint)data[p + 2] << 9)
                       | ((a & 0xF000) << 5);
                v -= ip + (uint)p;
                a = aOld
                  | ((v << 11) & (1u << 31))
                  | ((v << 20) & (0x3FFu << 21))
                  | ((v << 9) & (1u << 20))
                  | (v & (0xFFu << 12));
                BinaryPrimitives.WriteUInt32LittleEndian(data[p..], a);
                p += 4;
                continue;
            }

            // AUIPC
            uint vv = a;
            a = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
            if ((vv & 0xE80) == 0)
            {
                uint r = a >> 27;
                if (RiscvCheck2(vv, r))
                {
                    uint b = BinaryPrimitives.ReadUInt32BigEndian(data[(p + 4)..]);
                    uint v = a >> 12;
                    b -= ip + (uint)p;
                    a = (r << 7) + 0x17;
                    a += (b + 0x800) & 0xFFFFF000u;
                    v |= b << 20;
                    BinaryPrimitives.WriteUInt32LittleEndian(data[p..], a);
                    BinaryPrimitives.WriteUInt32LittleEndian(data[(p + 4)..], v);
                    p += 8;
                }
                else
                {
                    p += 4;
                }
            }
            else
            {
                uint b = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 4)..]);
                if (!RiscvCheck1(vv, b))
                {
                    p += 4 + 2;
                }
                else
                {
                    uint v = (a & 0xFFFFF000u) | (b >> 20);
                    a = (b << 12) | (0x17 + RiscvRegVal);
                    BinaryPrimitives.WriteUInt32LittleEndian(data[p..], a);
                    BinaryPrimitives.WriteUInt32LittleEndian(data[(p + 4)..], v);
                    p += 8;
                }
            }
        }

        return p;
    }
}
