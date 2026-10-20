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
using System.Runtime.InteropServices;

namespace PrZip.Primitives.Checksum;

/// <summary>
/// Implementación de CRC-32 IEEE (polinomio 0xEDB88320) de alto rendimiento.
/// Utiliza Slicing-by-16 como motor universal e instrucciones nativas en ARM cuando están disponibles.
/// Cero dependencias externas y compatible con Native AOT.
/// </summary>
internal static unsafe class Crc32Algorithm
{
    private const uint InitValue = 0xFFFFFFFFu;
    private const uint XorOut = 0xFFFFFFFFu;

    /// <summary>
    /// Calcula el CRC-32 IEEE para un búfer completo.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> buffer)
    {
        return Update(0, buffer);
    }

    /// <summary>
    /// Actualiza un CRC-32 existente con un nuevo bloque de datos (cálculo incremental).
    /// </summary>
    public static uint Update(uint currentCrc, ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return currentCrc;
        }

#if NET8_0_OR_GREATER
        // Aceleración ARMv8 nativa
        if (System.Runtime.Intrinsics.Arm.Crc32.IsSupported)
        {
            return ComputeArm(currentCrc, buffer);
        }
#endif

        return ComputeSlicingBy16(currentCrc, buffer);
    }

    /// <summary>
    /// Motor universal Slicing-by-16 (procesa 16 bytes por iteración en registros escalares).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint ComputeSlicingBy16(uint currentCrc, ReadOnlySpan<byte> buffer)
    {
        uint crc = currentCrc ^ InitValue;

        fixed (uint* table = Crc32Tables.SlicingTable)
        fixed (byte* src = buffer)
        {
            byte* ptr = src;
            int remaining = buffer.Length;

            // Procesar bloques de 16 bytes
            while (remaining >= 16)
            {
                uint one = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ptr, 4)) ^ crc;
                uint two = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ptr + 4, 4));
                uint three = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ptr + 8, 4));
                uint four = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ptr + 12, 4));

                crc = table[15 * 256 + (byte)one]
                    ^ table[14 * 256 + (byte)(one >> 8)]
                    ^ table[13 * 256 + (byte)(one >> 16)]
                    ^ table[12 * 256 + (byte)(one >> 24)]
                    ^ table[11 * 256 + (byte)two]
                    ^ table[10 * 256 + (byte)(two >> 8)]
                    ^ table[9 * 256 + (byte)(two >> 16)]
                    ^ table[8 * 256 + (byte)(two >> 24)]
                    ^ table[7 * 256 + (byte)three]
                    ^ table[6 * 256 + (byte)(three >> 8)]
                    ^ table[5 * 256 + (byte)(three >> 16)]
                    ^ table[4 * 256 + (byte)(three >> 24)]
                    ^ table[3 * 256 + (byte)four]
                    ^ table[2 * 256 + (byte)(four >> 8)]
                    ^ table[1 * 256 + (byte)(four >> 16)]
                    ^ table[0 * 256 + (byte)(four >> 24)];

                ptr += 16;
                remaining -= 16;
            }

            // Procesar los bytes restantes (0 a 15)
            while (remaining > 0)
            {
                crc = (crc >> 8) ^ table[(byte)crc ^ *ptr];
                ptr++;
                remaining--;
            }
        }

        return crc ^ XorOut;
    }

#if NET8_0_OR_GREATER
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint ComputeArm(uint currentCrc, ReadOnlySpan<byte> buffer)
    {
        uint crc = currentCrc ^ InitValue;

        fixed (byte* src = buffer)
        {
            byte* ptr = src;
            int remaining = buffer.Length;

            while (remaining >= 8)
            {
                ulong val = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(ptr, 8));
                crc = System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32(crc, val);
                ptr += 8;
                remaining -= 8;
            }

            if (remaining >= 4)
            {
                uint val = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(ptr, 4));
                crc = System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32(crc, val);
                ptr += 4;
                remaining -= 4;
            }

            while (remaining > 0)
            {
                crc = System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32(crc, *ptr);
                ptr++;
                remaining--;
            }
        }

        return crc ^ XorOut;
    }
#endif

    /// <summary>
    /// Combina dos valores de CRC-32 correspondientes a datos adyacentes de longitudes conocidas en GF(2).
    /// Permite calcular CRC32(A + B) conociendo CRC32(A), CRC32(B) y la longitud de B.
    /// </summary>
    public static uint Combine(uint crc1, uint crc2, long length2)
    {
        if (length2 <= 0)
        {
            return crc1;
        }

        uint[] even = new uint[32];
        uint[] odd = new uint[32];

        // Operador fila por fila para x^1
        odd[0] = Crc32Tables.Poly;
        uint row = 1;
        for (int n = 1; n < 32; n++)
        {
            odd[n] = row;
            row <<= 1;
        }

        Gf2MatrixSquare(even, odd);
        Gf2MatrixSquare(odd, even);

        ulong len = (ulong)length2;
        do
        {
            Gf2MatrixSquare(even, odd);
            if ((len & 1) != 0)
            {
                crc1 = Gf2MatrixTimes(even, crc1);
            }
            len >>= 1;

            if (len == 0)
            {
                break;
            }

            Gf2MatrixSquare(odd, even);
            if ((len & 1) != 0)
            {
                crc1 = Gf2MatrixTimes(odd, crc1);
            }
            len >>= 1;
        } while (len != 0);

        return crc1 ^ crc2;
    }

    private static uint Gf2MatrixTimes(uint[] mat, uint vec)
    {
        uint sum = 0;
        int idx = 0;
        while (vec != 0)
        {
            if ((vec & 1) != 0)
            {
                sum ^= mat[idx];
            }
            vec >>= 1;
            idx++;
        }
        return sum;
    }

    private static void Gf2MatrixSquare(uint[] square, uint[] mat)
    {
        for (int n = 0; n < 32; n++)
        {
            square[n] = Gf2MatrixTimes(mat, mat[n]);
        }
    }
}
