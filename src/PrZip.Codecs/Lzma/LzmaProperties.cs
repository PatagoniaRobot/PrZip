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
using PrZip.Exceptions;

namespace PrZip.Codecs.Lzma;

/// <summary>
/// Representa las propiedades de configuración de un flujo LZMA (5 bytes).
/// </summary>
internal readonly struct LzmaProperties
{
    public int Lc { get; } // Literal Context bits (0..8)
    public int Lp { get; } // Literal Position bits (0..4)
    public int Pb { get; } // Position bits (0..4)
    public uint DictionarySize { get; } // Tamaño del diccionario en bytes

    public LzmaProperties(int lc, int lp, int pb, uint dictionarySize)
    {
        if (lc is < 0 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(lc), "lc debe estar entre 0 y 8.");
        }

        if (lp is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(lp), "lp debe estar entre 0 y 4.");
        }

        if (pb is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(pb), "pb debe estar entre 0 y 4.");
        }

        Lc = lc;
        Lp = lp;
        Pb = pb;
        DictionarySize = Math.Max(dictionarySize, 1u << 12); // Mínimo 4 KB
    }

    /// <summary>
    /// Decodifica las propiedades a partir del búfer estándar de 5 bytes de LZMA.
    /// </summary>
    public static LzmaProperties Decode(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 5)
        {
            throw new CorruptArchiveException("El búfer de propiedades de LZMA debe tener al menos 5 bytes.");
        }

        byte b0 = buffer[0];
        if (b0 >= 9 * 5 * 5)
        {
            throw new CorruptArchiveException($"El byte de propiedades de LZMA (0x{b0:X2}) es inválido.");
        }

        int lc = b0 % 9;
        int remainder = b0 / 9;
        int lp = remainder % 5;
        int pb = remainder / 5;

        uint dictSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(1, 4));

        return new LzmaProperties(lc, lp, pb, dictSize);
    }

    /// <summary>
    /// Serializa las propiedades a su representación binaria de 5 bytes.
    /// </summary>
    public void Encode(Span<byte> destination)
    {
        if (destination.Length < 5)
        {
            throw new ArgumentException("El destino requiere al menos 5 bytes.", nameof(destination));
        }

        byte b0 = (byte)((Pb * 5 + Lp) * 9 + Lc);
        destination[0] = b0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(1, 4), DictionarySize);
    }
}
