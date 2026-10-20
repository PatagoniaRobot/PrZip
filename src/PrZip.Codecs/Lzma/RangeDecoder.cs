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

namespace PrZip.Codecs.Lzma;

/// <summary>
/// Decodificador aritmético por rangos (Range Decoder) para LZMA y LZMA2.
/// Implementa normalización aritmética con precisión de 11 bits.
/// </summary>
internal sealed class RangeDecoder
{
    private Stream? _stream;
    private readonly byte[] _buffer;
    private int _bufferPos;
    private int _bufferLimit;
    private bool _streamExhausted;

    public uint Range { get; private set; }
    public uint Code { get; private set; }

    private const int BufferSize = 16384;

    public RangeDecoder()
    {
        _buffer = new byte[BufferSize];
        _bufferPos = 0;
        _bufferLimit = 0;
        _streamExhausted = false;
        Range = 0;
        Code = 0;
    }

    /// <summary>
    /// Inicializa el decodificador leyendo los 5 bytes iniciales del flujo (1 byte de alineación + 4 bytes de código).
    /// </summary>
    public void Init(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
        _bufferPos = 0;
        _bufferLimit = 0;
        _streamExhausted = false;

        Range = 0xFFFFFFFFu;
        Code = 0;

        // El primer byte de un stream LZMA/LZMA2 es 0x00 y se descarta
        ReadByteCore();

        // Los siguientes 4 bytes inicializan el registro Code en Big-Endian
        for (int i = 0; i < 4; i++)
        {
            Code = (Code << 8) | ReadByteCore();
        }
    }

    /// <summary>
    /// Reinicializa el Range Decoder sin descartar el primer byte (usado en ciertos chunks de LZMA2).
    /// </summary>
    public void Reset()
    {
        Range = 0xFFFFFFFFu;
        Code = 0;
        for (int i = 0; i < 5; i++)
        {
            Code = (Code << 8) | ReadByteCore();
        }
    }

    /// <summary>
    /// Decodifica un bit modelado con una probabilidad adaptativa.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeBit(ref ushort prob)
    {
        uint bound = (Range >> LzmaConstants.NumBitModelTotalBits) * prob;

        if (Code < bound)
        {
            Range = bound;
            prob += (ushort)((LzmaConstants.BitModelTotal - prob) >> LzmaConstants.NumMoveBits);

            if (Range < LzmaConstants.TopValue)
            {
                Normalize();
            }

            return 0;
        }
        else
        {
            Range -= bound;
            Code -= bound;
            prob -= (ushort)(prob >> LzmaConstants.NumMoveBits);

            if (Range < LzmaConstants.TopValue)
            {
                Normalize();
            }

            return 1;
        }
    }

    /// <summary>
    /// Decodifica una secuencia de bits sin modelo de probabilidades (Direct Bits / probabilidades 50/50).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeDirectBits(int numBits)
    {
        uint res = 0;
        for (int i = numBits; i != 0; i--)
        {
            Range >>= 1;
            Code -= Range;
            uint t = 0 - ((Code >> 31) & 1);
            Code += Range & t;

            if (Range < LzmaConstants.TopValue)
            {
                Normalize();
            }

            res = (res << 1) | (1 + t);
        }

        return res;
    }

    /// <summary>
    /// Decodifica un símbolo usando un árbol binario de probabilidades.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeBitTree(Span<ushort> probs, int numBits)
    {
        uint m = 1;
        for (int i = 0; i < numBits; i++)
        {
            m = (m << 1) | DecodeBit(ref probs[(int)m]);
        }

        return m - (1u << numBits);
    }

    /// <summary>
    /// Decodifica un símbolo usando un árbol binario de probabilidades en orden inverso (LSB-first).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeReverseBitTree(Span<ushort> probs, int offset, int numBits)
    {
        uint m = 1;
        uint symbol = 0;

        for (int i = 0; i < numBits; i++)
        {
            uint bit = DecodeBit(ref probs[offset + (int)m]);
            m = (m << 1) | bit;
            symbol |= bit << i;
        }

        return symbol;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint DecodeReverseBitTree(Span<ushort> probs, int numBits) => DecodeReverseBitTree(probs, 0, numBits);


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Normalize()
    {
        Range <<= 8;
        Code = (Code << 8) | ReadByteCore();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadByteCore()
    {
        if (_bufferPos < _bufferLimit)
        {
            return _buffer[_bufferPos++];
        }

        return ReadByteSlow();
    }

    private byte ReadByteSlow()
    {
        if (_stream == null)
        {
            throw new InvalidOperationException("El RangeDecoder no ha sido inicializado con un Stream.");
        }

        if (_streamExhausted)
        {
            return 0; // Tratar fin de stream con ceros en normalización tardía
        }

        _bufferPos = 0;
        _bufferLimit = _stream.Read(_buffer, 0, _buffer.Length);

        if (_bufferLimit <= 0)
        {
            _streamExhausted = true;
            return 0;
        }

        return _buffer[_bufferPos++];
    }
}
