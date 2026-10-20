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
using System.Text;
using PrZip.Exceptions;

namespace PrZip.Primitives.IO;

/// <summary>
/// Lector binario seguro basado en ref struct para ReadOnlySpan&lt;byte&gt;.
/// Garantiza cero asignaciones en el heap y lanza excepciones tipadas de truncamiento ante desbordes.
/// </summary>
internal ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ByteReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public readonly int Position
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _position;
    }

    public readonly int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer.Length;
    }

    public readonly int Remaining
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer.Length - _position;
    }

    public readonly bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _position >= _buffer.Length;
    }

    public readonly ReadOnlySpan<byte> RemainingSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer[_position..];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        if (_position >= _buffer.Length)
        {
            ThrowTruncated(1);
        }

        return _buffer[_position++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (_position + count > _buffer.Length)
        {
            ThrowTruncated(count);
        }

        ReadOnlySpan<byte> slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadBytes(Span<byte> destination)
    {
        if (_position + destination.Length > _buffer.Length)
        {
            ThrowTruncated(destination.Length);
        }

        _buffer.Slice(_position, destination.Length).CopyTo(destination);
        _position += destination.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16LittleEndian()
    {
        const int size = sizeof(ushort);
        if (_position + size > _buffer.Length)
        {
            ThrowTruncated(size);
        }

        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position, size));
        _position += size;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32LittleEndian()
    {
        const int size = sizeof(uint);
        if (_position + size > _buffer.Length)
        {
            ThrowTruncated(size);
        }

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_position, size));
        _position += size;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadUInt64LittleEndian()
    {
        const int size = sizeof(ulong);
        if (_position + size > _buffer.Length)
        {
            ThrowTruncated(size);
        }

        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_position, size));
        _position += size;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (_position + count > _buffer.Length)
        {
            ThrowTruncated(count);
        }

        _position += count;
    }

    /// <summary>
    /// Lee una cadena UTF-16LE de longitud conocida en caracteres (charCount * 2 bytes).
    /// </summary>
    public string ReadUtf16String(int charCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(charCount);
        if (charCount == 0)
        {
            return string.Empty;
        }

        int byteCount = checked(charCount * sizeof(char));
        ReadOnlySpan<byte> bytes = ReadBytes(byteCount);
        return Encoding.Unicode.GetString(bytes);
    }

    /// <summary>
    /// Lee una cadena UTF-16LE terminada en carácter nulo (0x0000).
    /// </summary>
    public string ReadNullTerminatedUtf16String()
    {
        ReadOnlySpan<byte> remaining = _buffer[_position..];
        int nullIndex = -1;

        // Buscar el carácter nulo alineado a 2 bytes (0x00, 0x00)
        for (int i = 0; i + 1 < remaining.Length; i += 2)
        {
            if (remaining[i] == 0 && remaining[i + 1] == 0)
            {
                nullIndex = i;
                break;
            }
        }

        if (nullIndex < 0)
        {
            throw new ArchiveTruncatedException("No se encontró el terminador nulo para la cadena UTF-16.");
        }

        string result = Encoding.Unicode.GetString(remaining[..nullIndex]);
        _position += nullIndex + 2; // Avanzar pasado el terminador nulo
        return result;
    }

    private readonly void ThrowTruncated(int requiredBytes)
    {
        throw new ArchiveTruncatedException(
            $"El archivo está truncado: se requerían {requiredBytes} bytes en la posición {_position}, " +
            $"pero solo quedan {Remaining} bytes disponibles.");
    }
}
