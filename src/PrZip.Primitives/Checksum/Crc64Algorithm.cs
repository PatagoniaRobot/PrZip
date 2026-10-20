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

namespace PrZip.Primitives.Checksum;

/// <summary>
/// CRC-64 de ECMA-182 en su forma reflejada (polinomio 0xC96C5795D7870F42), el que usa XZ para la
/// verificación "CRC64". Valor inicial y final invertidos.
/// </summary>
internal static class Crc64Algorithm
{
    private const ulong Polynomial = 0xC96C5795D7870F42UL;
    private static readonly ulong[] Table = BuildTable();

    /// <summary>CRC de un búfer completo.</summary>
    public static ulong Compute(ReadOnlySpan<byte> buffer) => Update(0, buffer);

    /// <summary>Continúa un CRC (0 para empezar) con más datos.</summary>
    public static ulong Update(ulong crc, ReadOnlySpan<byte> buffer)
    {
        crc = ~crc;
        foreach (byte b in buffer)
        {
            crc = Table[(byte)(crc ^ b)] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static ulong[] BuildTable()
    {
        var table = new ulong[256];
        for (uint i = 0; i < 256; i++)
        {
            ulong c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ Polynomial : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
