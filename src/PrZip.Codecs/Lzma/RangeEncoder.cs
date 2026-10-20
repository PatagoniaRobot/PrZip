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
using PrZip.Codecs.Lzma;

namespace PrZip.Codecs.Lzma;

/// <summary>
/// Codificador aritmético por rangos (Range Encoder) para LZMA y LZMA2.
/// Implementa la contraparte canónica exacta del Range Decoder con precisión de 11 bits.
/// Basado en el algoritmo de dominio público de Igor Pavlov (RangeEnc.c).
/// </summary>
internal sealed class RangeEncoder
{
    private Stream? _stream;
    private ulong _low;
    private uint _range;
    private uint _cacheSize;
    private byte _cache;
    private long _written;

    /// <summary>
    /// Cota superior de los bytes que habrá emitido el codificador tras FlushData: lo ya escrito,
    /// la caché de acarreo pendiente y los 4 bytes de low. LZMA2 la usa para cerrar un chunk
    /// antes de pasar los 64 KB comprimidos.
    /// </summary>
    public long PendingBytes => _written + _cacheSize + 4;

    public RangeEncoder()
    {
    }

    /// <summary>
    /// Inicializa el codificador para escribir en el flujo destino especificado.
    /// </summary>
    public void Init(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
        _low = 0;
        _range = 0xFFFFFFFFu;
        _cacheSize = 1;
        _cache = 0;
        _written = 0;
    }

    /// <summary>
    /// Codifica un bit modelado con una probabilidad adaptativa.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeBit(ref ushort prob, uint bit)
    {
        uint bound = (_range >> LzmaConstants.NumBitModelTotalBits) * prob;

        if (bit == 0)
        {
            _range = bound;
            prob += (ushort)((LzmaConstants.BitModelTotal - prob) >> LzmaConstants.NumMoveBits);
        }
        else
        {
            _low += bound;
            _range -= bound;
            prob -= (ushort)(prob >> LzmaConstants.NumMoveBits);
        }

        if (_range < LzmaConstants.TopValue)
        {
            _range <<= 8;
            ShiftLow();
        }
    }

    /// <summary>
    /// Codifica una secuencia de bits sin modelo de probabilidades (Direct Bits / 50-50).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeDirectBits(uint val, int numBits)
    {
        for (int i = numBits - 1; i >= 0; i--)
        {
            _range >>= 1;
            if (((val >> i) & 1) == 1)
            {
                _low += _range;
            }

            if (_range < LzmaConstants.TopValue)
            {
                _range <<= 8;
                ShiftLow();
            }
        }
    }

    /// <summary>
    /// Codifica un símbolo usando un árbol binario de probabilidades (MSB a LSB).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeBitTree(Span<ushort> probs, int numBits, uint symbol)
    {
        uint m = 1;
        for (int i = numBits - 1; i >= 0; i--)
        {
            uint bit = (symbol >> i) & 1;
            EncodeBit(ref probs[(int)m], bit);
            m = (m << 1) | bit;
        }
    }

    /// <summary>
    /// Codifica un símbolo usando un árbol binario de probabilidades en orden inverso (LSB-first).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeReverseBitTree(Span<ushort> probs, int offset, int numBits, uint symbol)
    {
        uint m = 1;
        for (int i = 0; i < numBits; i++)
        {
            uint bit = (symbol >> i) & 1;
            EncodeBit(ref probs[offset + (int)m], bit);
            m = (m << 1) | bit;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeReverseBitTree(Span<ushort> probs, int numBits, uint symbol)
        => EncodeReverseBitTree(probs, 0, numBits, symbol);

    /// <summary>
    /// Normaliza el rango desplazando un byte de low hacia el flujo o la caché pendiente.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ShiftLow()
    {
        uint lowLow = (uint)_low;
        uint high = (uint)(_low >> 32);

        if (lowLow < 0xFF000000u || high != 0)
        {
            byte b = _cache;
            do
            {
                _stream!.WriteByte((byte)(b + high));
                _written++;
                b = 0xFF;
            } while (--_cacheSize != 0);

            _cache = (byte)(_low >> 24);
        }

        _cacheSize++;
        _low = (ulong)(lowLow << 8);
    }

    /// <summary>
    /// Descarga los 5 bytes remanentes del Range Encoder al finalizar el flujo.
    /// </summary>
    public void FlushData()
    {
        for (int i = 0; i < 5; i++)
        {
            ShiftLow();
        }
    }
}
