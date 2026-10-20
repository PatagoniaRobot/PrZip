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
using PrZip.Exceptions;
using PrZip.Primitives.IO;

namespace PrZip.Primitives.Numeric;

/// <summary>
/// Representa un vector de bits utilizado en las cabeceras 7z (orden MSB-first).
/// Soporta la bandera 'AllAreDefined' y permite consulta indexada y conteo sin asignaciones.
/// </summary>
internal readonly struct BitVector
{
    private readonly byte[]? _bytes;
    private readonly int _length;
    private readonly bool _allAreDefined;

    public BitVector(int length, bool allAreDefined)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _length = length;
        _allAreDefined = allAreDefined;
        _bytes = null;
    }

    public BitVector(byte[] bytes, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        int requiredBytes = GetByteCount(length);
        if (bytes.Length < requiredBytes)
        {
            throw new ArgumentException(
                $"El array de bytes tiene {bytes.Length} elementos, pero se requieren al menos {requiredBytes} para {length} bits.",
                nameof(bytes));
        }

        _bytes = bytes;
        _length = length;
        _allAreDefined = false;
    }

    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _length;
    }

    public bool AllAreDefined
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _allAreDefined;
    }

    /// <summary>
    /// Acceso indexado al bit en la posición especificada (0 a Length - 1).
    /// </summary>
    public bool this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)_length)
            {
                ThrowIndexOutOfRange(index);
            }

            if (_allAreDefined)
            {
                return true;
            }

            if (_bytes == null)
            {
                return false;
            }

            int byteIndex = index >> 3;
            int bitOffset = 7 - (index & 7); // MSB-first: bit 0 está en 0x80
            return (_bytes[byteIndex] & (1 << bitOffset)) != 0;
        }
    }

    /// <summary>
    /// Cuenta cuántos bits están en estado 'true'.
    /// </summary>
    public int CountTrue()
    {
        if (_length == 0)
        {
            return 0;
        }

        if (_allAreDefined)
        {
            return _length;
        }

        if (_bytes == null)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < _length; i++)
        {
            if (this[i])
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Calcula la cantidad de bytes necesarios para almacenar una cantidad de bits dada.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetByteCount(int bitCount)
    {
        return (bitCount + 7) >> 3;
    }

    /// <summary>
    /// Lee un vector de bits desde ByteReader precedido por el byte AllAreDefined.
    /// Si AllAreDefined != 0, no se leen bytes adicionales y todos los bits se consideran 'true'.
    /// </summary>
    public static BitVector ReadWithAllDefined(ref ByteReader reader, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return new BitVector(0, allAreDefined: true);
        }

        byte allAreDefined = reader.ReadByte();
        if (allAreDefined != 0)
        {
            return new BitVector(count, allAreDefined: true);
        }

        return ReadRaw(ref reader, count);
    }

    /// <summary>
    /// Lee un vector de bits puro de 'count' bits (sin byte AllAreDefined).
    /// </summary>
    public static BitVector ReadRaw(ref ByteReader reader, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return new BitVector(0, allAreDefined: true);
        }

        int byteCount = GetByteCount(count);
        byte[] bytes = new byte[byteCount];
        reader.ReadBytes(bytes);

        return new BitVector(bytes, count);
    }

    /// <summary>
    /// Escribe un vector de bits con o sin el byte inicial 'AllAreDefined'.
    /// </summary>
    public static int Write(Span<byte> destination, ReadOnlySpan<bool> values, bool includeAllDefinedByte)
    {
        if (values.IsEmpty)
        {
            if (includeAllDefinedByte)
            {
                destination[0] = 1;
                return 1;
            }
            return 0;
        }

        // Verificar si todos son true
        bool allTrue = true;
        for (int i = 0; i < values.Length; i++)
        {
            if (!values[i])
            {
                allTrue = false;
                break;
            }
        }

        int pos = 0;
        if (includeAllDefinedByte)
        {
            if (allTrue)
            {
                destination[0] = 1;
                return 1;
            }

            destination[pos++] = 0;
        }

        int byteCount = GetByteCount(values.Length);
        if (destination.Length < pos + byteCount)
        {
            throw new ArgumentException("Buffer insuficiente para escribir el BitVector.", nameof(destination));
        }

        destination.Slice(pos, byteCount).Clear();

        for (int i = 0; i < values.Length; i++)
        {
            if (values[i])
            {
                int byteIdx = pos + (i >> 3);
                int bitOffset = 7 - (i & 7);
                destination[byteIdx] |= (byte)(1 << bitOffset);
            }
        }

        return pos + byteCount;
    }

    private readonly void ThrowIndexOutOfRange(int index)
    {
        throw new ArgumentOutOfRangeException(nameof(index), index, $"Índice de bit fuera de rango (longitud: {_length}).");
    }
}
