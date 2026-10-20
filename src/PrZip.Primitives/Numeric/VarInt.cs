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
using PrZip.Exceptions;
using PrZip.Primitives.IO;

namespace PrZip.Primitives.Numeric;

/// <summary>
/// Codificador y decodificador de enteros de longitud variable de 64 bits (VarInt de 7z).
/// Formato de 1 a 9 bytes según la especificación SzReadNumber de 7z.
/// </summary>
internal static class VarInt
{
    /// <summary>
    /// Lee un número de longitud variable desde un ByteReader.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read(ref ByteReader reader)
    {
        byte firstByte = reader.ReadByte();
        byte mask = 0x80;
        ulong value = 0;

        for (int i = 0; i < 8; i++)
        {
            if ((firstByte & mask) == 0)
            {
                ulong high = (ulong)(firstByte & (mask - 1));
                value |= high << (i * 8);
                return value;
            }

            byte nextByte = reader.ReadByte();
            value |= ((ulong)nextByte) << (i * 8);
            mask >>= 1;
        }

        return value;
    }

    /// <summary>
    /// Lee un número de longitud variable desde un ReadOnlySpan&lt;byte&gt;.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read(ReadOnlySpan<byte> buffer, out int bytesRead)
    {
        if (buffer.IsEmpty)
        {
            throw new ArchiveTruncatedException("No hay datos para leer el número VarInt de 7z.");
        }

        byte firstByte = buffer[0];
        byte mask = 0x80;
        ulong value = 0;
        int pos = 1;

        for (int i = 0; i < 8; i++)
        {
            if ((firstByte & mask) == 0)
            {
                ulong high = (ulong)(firstByte & (mask - 1));
                value |= high << (i * 8);
                bytesRead = pos;
                return value;
            }

            if (pos >= buffer.Length)
            {
                throw new ArchiveTruncatedException(
                    $"Buffer truncado al leer VarInt: se requerían bytes adicionales para la máscara 0x{mask:X2}.");
            }

            byte nextByte = buffer[pos++];
            value |= ((ulong)nextByte) << (i * 8);
            mask >>= 1;
        }

        bytesRead = pos;
        return value;
    }

    /// <summary>
    /// Intenta leer un VarInt sin lanzar excepciones si el buffer está truncado.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryRead(ReadOnlySpan<byte> buffer, out ulong value, out int bytesRead)
    {
        value = 0;
        bytesRead = 0;

        if (buffer.IsEmpty)
        {
            return false;
        }

        byte firstByte = buffer[0];
        byte mask = 0x80;
        int pos = 1;

        for (int i = 0; i < 8; i++)
        {
            if ((firstByte & mask) == 0)
            {
                ulong high = (ulong)(firstByte & (mask - 1));
                value |= high << (i * 8);
                bytesRead = pos;
                return true;
            }

            if (pos >= buffer.Length)
            {
                return false;
            }

            byte nextByte = buffer[pos++];
            value |= ((ulong)nextByte) << (i * 8);
            mask >>= 1;
        }

        bytesRead = pos;
        return true;
    }

    /// <summary>
    /// Calcula el tamaño en bytes que ocupará un valor codificado en formato VarInt (1 a 9 bytes).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetEncodedSize(ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            // Capacidad para i bytes adicionales: (7 - i) bits en primer byte + i * 8 bits
            int totalBits = 7 + (i * 7); // (7 - i) + 8*i = 7 + 7*i
            if (i < 8 && value < (1UL << totalBits))
            {
                return i + 1;
            }
        }

        return 9;
    }

    /// <summary>
    /// Escribe un valor en formato VarInt en el buffer de destino. Devuelve la cantidad de bytes escritos (1 a 9).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(Span<byte> destination, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            int totalBits = 7 + (i * 7);
            if (value < (1UL << totalBits))
            {
                if (destination.Length < i + 1)
                {
                    throw new ArgumentException("El buffer de destino es demasiado pequeño para escribir el VarInt.", nameof(destination));
                }

                // Generar los i bits en '1' más significativos
                byte firstByteMask = (byte)(0xFF << (8 - i));
                byte highBits = (byte)((value >> (i * 8)) & 0xFF);
                destination[0] = (byte)(firstByteMask | highBits);

                // Escribir los i bytes inferiores en little-endian
                ulong remaining = value;
                for (int j = 1; j <= i; j++)
                {
                    destination[j] = (byte)(remaining & 0xFF);
                    remaining >>= 8;
                }

                return i + 1;
            }
        }

        // Si el valor requiere los 64 bits completos (9 bytes)
        if (destination.Length < 9)
        {
            throw new ArgumentException("El buffer de destino es demasiado pequeño para escribir el VarInt de 9 bytes.", nameof(destination));
        }

        destination[0] = 0xFF;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[1..9], value);
        return 9;
    }
}
