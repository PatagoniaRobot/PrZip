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
/// Tablas precomputadas de Slicing-by-16 para CRC-32 IEEE (polinomio 0xEDB88320).
/// </summary>
internal static class Crc32Tables
{
    public const uint Poly = 0xEDB88320u;

    /// <summary>
    /// Tablas slicing-by-16: 16 tablas de 256 elementos (4096 uints = 16 KB de memoria).
    /// Cabe holgadamente en la caché L1 (típicamente 32 KB o 48 KB).
    /// </summary>
    public static readonly uint[] SlicingTable = GenerateSlicingTables();

    private static uint[] GenerateSlicingTables()
    {
        uint[] table = new uint[16 * 256];

        // Tabla 0: CRC estándar byte a byte
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) * Poly);
            }
            table[i] = crc;
        }

        // Tablas 1 a 15: Slicing sucesivo
        for (int slice = 1; slice < 16; slice++)
        {
            int prevOffset = (slice - 1) * 256;
            int currOffset = slice * 256;

            for (int i = 0; i < 256; i++)
            {
                uint prev = table[prevOffset + i];
                table[currOffset + i] = (prev >> 8) ^ table[(byte)prev];
            }
        }

        return table;
    }
}
