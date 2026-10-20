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
using PrZip.Primitives.Checksum;

namespace PrZip.Codecs.Lzma;

/// <summary>
/// Decodificador del algoritmo LZMA (Lempel-Ziv-Markov chain-Algorithm).
/// Basado en la especificación y matemática de referencia de Igor Pavlov (LzmaDec.c).
/// </summary>
internal sealed class LzmaDecoder
{
    private readonly RangeDecoder _rangeDecoder = new();

    private LzmaProperties _properties;
    private byte[] _dictionary = [];
    private uint _dictSize;
    private uint _dictPos;
    private uint _processedSize;

    // Historial de 4 distancias previas
    private uint _rep0;
    private uint _rep1;
    private uint _rep2;
    private uint _rep3;

    private readonly byte[] _outBuf = new byte[65536];
    private int _outBufPos;

    private uint _remainLen;
    private bool _rangeDecoderInitialized;
    private bool _isEos;

    private int _state;

    // Tablas de probabilidades (inicializadas a BitModelTotal / 2 = 1024)
    private readonly ushort[] _isMatch = new ushort[LzmaConstants.NumStates << LzmaConstants.NumPosSlotBits];
    private readonly ushort[] _isRep = new ushort[LzmaConstants.NumStates];
    private readonly ushort[] _isRepG0 = new ushort[LzmaConstants.NumStates];
    private readonly ushort[] _isRepG1 = new ushort[LzmaConstants.NumStates];
    private readonly ushort[] _isRepG2 = new ushort[LzmaConstants.NumStates];
    private readonly ushort[] _isRep0Long = new ushort[LzmaConstants.NumStates << LzmaConstants.NumPosSlotBits];

    private readonly ushort[] _posSlot = new ushort[LzmaConstants.NumLenToPosStates << LzmaConstants.NumPosSlotBits];
    private readonly ushort[] _specPos = new ushort[LzmaConstants.NumFullDistances];
    private readonly ushort[] _align = new ushort[LzmaConstants.AlignTableSize];

    // Decodificadores de longitud (Normal y Rep)
    private readonly LenDecoder _lenDecoder = new();
    private readonly LenDecoder _repLenDecoder = new();

    // Probabilidades de literales: (1 << (lc + lp)) * 0x300
    private ushort[] _litProbs = [];

    public LzmaDecoder()
    {
    }

    /// <summary>
    /// Configura las propiedades del decodificador y reserva la ventana de diccionario.
    /// </summary>
    public void SetProperties(LzmaProperties properties)
    {
        _properties = properties;

        if (_dictSize != properties.DictionarySize || _dictionary.Length == 0)
        {
            _dictSize = properties.DictionarySize;
            // Ventana circular del diccionario
            _dictionary = new byte[_dictSize];
        }

        int litProbsSize = 0x300 << (_properties.Lc + _properties.Lp);
        if (_litProbs.Length < litProbsSize)
        {
            _litProbs = new ushort[litProbsSize];
        }

        Reset();
    }

    /// <summary>
    /// Cambia lc/lp/pb sin tocar el diccionario (chunk LZMA2 0xC0/0xA0..: propiedades nuevas,
    /// historia conservada). El estado se reinicia aparte con <see cref="ResetState"/>.
    /// </summary>
    public void SetLiteralProperties(int lc, int lp, int pb)
    {
        _properties = new LzmaProperties(lc, lp, pb, _dictSize);
        int litProbsSize = 0x300 << (lc + lp);
        if (_litProbs.Length < litProbsSize)
        {
            _litProbs = new ushort[litProbsSize];
        }
    }

    /// <summary>
    /// Reinicia completamente el decodificador (estado del modelo y diccionario).
    /// </summary>
    public void Reset()
    {
        ResetDictionary();
        ResetState();
    }

    /// <summary>
    /// Reinicia únicamente el diccionario deslizante manteniendo o no el estado.
    /// </summary>
    public void ResetDictionary()
    {
        _dictPos = 0;
        _processedSize = 0;
        if (_dictionary.Length > 0)
        {
            _dictionary.AsSpan().Clear();
        }
    }

    /// <summary>
    /// Reinicia el estado de probabilidades y repeticiones, preservando el diccionario histórico.
    /// Esencial para chunks LZMA2 con resetMode = 1 o 2.
    /// </summary>
    public void ResetState()
    {
        _state = 0;
        _rep0 = 0;
        _rep1 = 0;
        _rep2 = 0;
        _rep3 = 0;
        _remainLen = 0;
        _rangeDecoderInitialized = false;
        _isEos = false;

        InitProbs();
    }

    /// <summary>
    /// Inserta bytes sin comprimir directamente al diccionario deslizante.
    /// Requerido por LZMA2 para chunks de datos sin comprimir (0x01 o 0x02).
    /// </summary>
    public void PutBytes(ReadOnlySpan<byte> data)
    {
        if (_dictionary.Length == 0) return;

        for (int i = 0; i < data.Length; i++)
        {
            _dictionary[_dictPos++] = data[i];
            if (_dictPos >= _dictSize)
            {
                _dictPos = 0;
            }
        }
        _processedSize += (uint)data.Length;
    }

    public void InitStream(Stream inStream)
    {
        ArgumentNullException.ThrowIfNull(inStream);
        _rangeDecoder.Init(inStream);
        _rangeDecoderInitialized = true;
        _remainLen = 0;
        _isEos = false;
    }

    /// <summary>
    /// Decodifica datos LZMA desde un Stream de entrada y los escribe en un Stream de salida.
    /// </summary>
    public void Decode(Stream inStream, Stream outStream, long unpackSize)
    {
        ArgumentNullException.ThrowIfNull(inStream);
        ArgumentNullException.ThrowIfNull(outStream);

        _rangeDecoder.Init(inStream);
        _outBufPos = 0;

        long written = 0;
        uint posMask = (1u << _properties.Pb) - 1;
        uint lpMask = (1u << _properties.Lp) - 1;

        while (unpackSize < 0 || written < unpackSize)
        {
            uint posState = _processedSize & posMask;
            int isMatchIndex = (_state << 4) + (int)posState;

            if (_rangeDecoder.DecodeBit(ref _isMatch[isMatchIndex]) == 0)
            {
                // 1. Literal
                byte prevByte = _dictPos > 0 ? _dictionary[_dictPos - 1] : (_dictPos == 0 && _processedSize > 0 ? _dictionary[_dictSize - 1] : (byte)0);
                byte literal = DecodeLiteral(prevByte, _processedSize, lpMask);

                PutByte(literal, outStream);
                written++;

                _state = LzmaConstants.LiteralNextStates[_state];
            }
            else
            {
                // 2. Coincidencia (Match o Rep)
                uint len;
                if (_rangeDecoder.DecodeBit(ref _isRep[_state]) == 1)
                {
                    // Rep-Match
                    if (_rangeDecoder.DecodeBit(ref _isRepG0[_state]) == 0)
                    {
                        if (_rangeDecoder.DecodeBit(ref _isRep0Long[isMatchIndex]) == 0)
                        {
                            // Short Rep Match (longitud 1)
                            _state = _state < 7 ? 9 : 11;
                            byte b = GetByteFromDistance(_rep0);
                            PutByte(b, outStream);
                            written++;
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (_rangeDecoder.DecodeBit(ref _isRepG1[_state]) == 0)
                        {
                            dist = _rep1;
                        }
                        else
                        {
                            if (_rangeDecoder.DecodeBit(ref _isRepG2[_state]) == 0)
                            {
                                dist = _rep2;
                            }
                            else
                            {
                                dist = _rep3;
                                _rep3 = _rep2;
                            }
                            _rep2 = _rep1;
                        }
                        _rep1 = _rep0;
                        _rep0 = dist;
                    }

                    len = _repLenDecoder.Decode(_rangeDecoder, posState);
                    _state = _state < 7 ? 8 : 11;
                }
                else
                {
                    // Match normal
                    _rep3 = _rep2;
                    _rep2 = _rep1;
                    _rep1 = _rep0;

                    len = _lenDecoder.Decode(_rangeDecoder, posState);
                    _state = _state < 7 ? 7 : 10;

                    uint lenToPosState = len < 4 ? len : 3;
                    uint posSlot = _rangeDecoder.DecodeBitTree(
                        _posSlot.AsSpan((int)(lenToPosState << LzmaConstants.NumPosSlotBits), 1 << LzmaConstants.NumPosSlotBits),
                        LzmaConstants.NumPosSlotBits);

                    if (posSlot >= 4)
                    {
                        int numDirectBits = (int)((posSlot >> 1) - 1);
                        _rep0 = (2 | (posSlot & 1)) << numDirectBits;

                        if (posSlot < LzmaConstants.EndPosModelIndex)
                        {
                            _rep0 += _rangeDecoder.DecodeReverseBitTree(
                                _specPos,
                                (int)(_rep0 - posSlot - 1),
                                numDirectBits);
                        }
                        else
                        {
                            _rep0 += _rangeDecoder.DecodeDirectBits(numDirectBits - LzmaConstants.NumAlignBits) << LzmaConstants.NumAlignBits;
                            _rep0 += _rangeDecoder.DecodeReverseBitTree(_align, LzmaConstants.NumAlignBits);

                            if (_rep0 == LzmaConstants.EndOfStreamDistance)
                            {
                                // Marcador de Fin de Flujo (EOS)
                                break;
                            }
                        }
                    }
                    else
                    {
                        _rep0 = posSlot;
                    }
                }

                if (_rep0 >= _dictSize || (_processedSize < _rep0 && _dictPos < _rep0))
                {
                    throw new CorruptArchiveException($"Distancia LZMA inválida: rep0 = {_rep0}, tamaño procesado = {_processedSize}.");
                }

                len += LzmaConstants.MatchMinLen;

                // Copiar la coincidencia en la salida
                for (uint i = 0; i < len; i++)
                {
                    byte b = GetByteFromDistance(_rep0);
                    PutByte(b, outStream);
                    written++;
                }
            }
        }

        if (_outBufPos > 0)
        {
            outStream.Write(_outBuf, 0, _outBufPos);
            _outBufPos = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte DecodeLiteral(byte prevByte, uint totalPos, uint lpMask)
    {
        int litContext = (int)(((totalPos & lpMask) << _properties.Lc) + (uint)(prevByte >> (8 - _properties.Lc)));
        int probOffset = litContext * 0x300;
        Span<ushort> probs = _litProbs.AsSpan(probOffset, 0x300);

        uint symbol = 1;
        if (_state >= 7)
        {
            // Matched literal
            byte matchByte = GetByteFromDistance(_rep0);
            do
            {
                uint matchBit = (uint)((matchByte >> 7) & 1);
                matchByte <<= 1;
                uint bit = _rangeDecoder.DecodeBit(ref probs[(int)(0x100 + (matchBit << 8) + symbol)]);
                symbol = (symbol << 1) | bit;
                if (matchBit != bit)
                {
                    break;
                }
            } while (symbol < 0x100);
        }

        while (symbol < 0x100)
        {
            symbol = (symbol << 1) | _rangeDecoder.DecodeBit(ref probs[(int)symbol]);
        }

        return (byte)symbol;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetByteFromDistance(uint dist)
    {
        uint pos = _dictPos - dist - 1;
        if (pos >= _dictSize)
        {
            pos += _dictSize;
        }

        return _dictionary[pos];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PutByte(byte b, Stream outStream)
    {
        _dictionary[_dictPos++] = b;
        if (_dictPos >= _dictSize)
        {
            _dictPos = 0;
        }

        _processedSize++;
        _outBuf[_outBufPos++] = b;
        if (_outBufPos >= _outBuf.Length)
        {
            outStream.Write(_outBuf, 0, _outBufPos);
            _outBufPos = 0;
        }
    }

    /// <summary>
    /// Decodifica datos incrementalmente hacia un búfer de destino. Devuelve la cantidad de bytes escritos.
    /// </summary>
    public int DecodeChunk(Stream inStream, Span<byte> destination)
    {
        if (_isEos || destination.IsEmpty)
        {
            return 0;
        }

        if (!_rangeDecoderInitialized)
        {
            InitStream(inStream);
        }

        int written = 0;
        uint posMask = (1u << _properties.Pb) - 1;
        uint lpMask = (1u << _properties.Lp) - 1;

        while (written < destination.Length && !_isEos)
        {
            if (_remainLen > 0)
            {
                byte b = GetByteFromDistance(_rep0);
                PutByteInternal(b);
                destination[written++] = b;
                _remainLen--;
                continue;
            }

            uint posState = _processedSize & posMask;
            int isMatchIndex = (_state << 4) + (int)posState;

            if (_rangeDecoder.DecodeBit(ref _isMatch[isMatchIndex]) == 0)
            {
                // Literal
                byte prevByte = _dictPos > 0 ? _dictionary[_dictPos - 1] : (_dictPos == 0 && _processedSize > 0 ? _dictionary[_dictSize - 1] : (byte)0);
                byte literal = DecodeLiteral(prevByte, _processedSize, lpMask);

                PutByteInternal(literal);
                destination[written++] = literal;

                _state = LzmaConstants.LiteralNextStates[_state];
            }
            else
            {
                // Match o Rep
                uint len;
                if (_rangeDecoder.DecodeBit(ref _isRep[_state]) == 1)
                {
                    if (_rangeDecoder.DecodeBit(ref _isRepG0[_state]) == 0)
                    {
                        if (_rangeDecoder.DecodeBit(ref _isRep0Long[isMatchIndex]) == 0)
                        {
                            _state = _state < 7 ? 9 : 11;
                            byte b = GetByteFromDistance(_rep0);
                            PutByteInternal(b);
                            destination[written++] = b;
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (_rangeDecoder.DecodeBit(ref _isRepG1[_state]) == 0)
                        {
                            dist = _rep1;
                        }
                        else
                        {
                            if (_rangeDecoder.DecodeBit(ref _isRepG2[_state]) == 0)
                            {
                                dist = _rep2;
                            }
                            else
                            {
                                dist = _rep3;
                                _rep3 = _rep2;
                            }
                            _rep2 = _rep1;
                        }
                        _rep1 = _rep0;
                        _rep0 = dist;
                    }

                    len = _repLenDecoder.Decode(_rangeDecoder, posState);
                    _state = _state < 7 ? 8 : 11;
                }
                else
                {
                    _rep3 = _rep2;
                    _rep2 = _rep1;
                    _rep1 = _rep0;

                    len = _lenDecoder.Decode(_rangeDecoder, posState);
                    _state = _state < 7 ? 7 : 10;

                    uint lenToPosState = len < 4 ? len : 3;
                    uint posSlot = _rangeDecoder.DecodeBitTree(
                        _posSlot.AsSpan((int)(lenToPosState << LzmaConstants.NumPosSlotBits), 1 << LzmaConstants.NumPosSlotBits),
                        LzmaConstants.NumPosSlotBits);

                    if (posSlot >= 4)
                    {
                        int numDirectBits = (int)((posSlot >> 1) - 1);
                        _rep0 = (2 | (posSlot & 1)) << numDirectBits;

                        if (posSlot < LzmaConstants.EndPosModelIndex)
                        {
                            _rep0 += _rangeDecoder.DecodeReverseBitTree(
                                _specPos,
                                (int)(_rep0 - posSlot - 1),
                                numDirectBits);
                        }
                        else
                        {
                            _rep0 += _rangeDecoder.DecodeDirectBits(numDirectBits - LzmaConstants.NumAlignBits) << LzmaConstants.NumAlignBits;
                            _rep0 += _rangeDecoder.DecodeReverseBitTree(_align, LzmaConstants.NumAlignBits);

                            if (_rep0 == LzmaConstants.EndOfStreamDistance)
                            {
                                _isEos = true;
                                break;
                            }
                        }
                    }
                    else
                    {
                        _rep0 = posSlot;
                    }
                }

                if (_rep0 >= _dictSize || (_processedSize < _rep0 && _dictPos < _rep0))
                {
                    throw new CorruptArchiveException($"Distancia LZMA inválida: rep0 = {_rep0}, tamaño procesado = {_processedSize}.");
                }

                _remainLen = len + LzmaConstants.MatchMinLen;
            }
        }

        return written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PutByteInternal(byte b)
    {
        _dictionary[_dictPos++] = b;
        if (_dictPos >= _dictSize)
        {
            _dictPos = 0;
        }

        _processedSize++;
    }

    private void InitProbs()
    {
        const ushort initVal = (ushort)(LzmaConstants.BitModelTotal >> 1); // 1024

        _isMatch.AsSpan().Fill(initVal);
        _isRep.AsSpan().Fill(initVal);
        _isRepG0.AsSpan().Fill(initVal);
        _isRepG1.AsSpan().Fill(initVal);
        _isRepG2.AsSpan().Fill(initVal);
        _isRep0Long.AsSpan().Fill(initVal);
        _posSlot.AsSpan().Fill(initVal);
        _specPos.AsSpan().Fill(initVal);
        _align.AsSpan().Fill(initVal);

        _lenDecoder.Init();
        _repLenDecoder.Init();

        _litProbs.AsSpan().Fill(initVal);
    }

    /// <summary>
    /// Decodificador de longitudes de coincidencia de LZMA.
    /// </summary>
    private sealed class LenDecoder
    {
        private ushort _choice1 = (ushort)(LzmaConstants.BitModelTotal >> 1);
        private ushort _choice2 = (ushort)(LzmaConstants.BitModelTotal >> 1);
        private readonly ushort[] _low = new ushort[1 << (LzmaConstants.NumPosSlotBits + LzmaConstants.NumLowLenBits)];
        private readonly ushort[] _mid = new ushort[1 << (LzmaConstants.NumPosSlotBits + LzmaConstants.NumMidLenBits)];
        private readonly ushort[] _high = new ushort[LzmaConstants.NumHighLenSymbols];

        public void Init()
        {
            const ushort initVal = (ushort)(LzmaConstants.BitModelTotal >> 1);
            _choice1 = initVal;
            _choice2 = initVal;
            _low.AsSpan().Fill(initVal);
            _mid.AsSpan().Fill(initVal);
            _high.AsSpan().Fill(initVal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Decode(RangeDecoder rc, uint posState)
        {
            if (rc.DecodeBit(ref _choice1) == 0)
            {
                int offset = (int)(posState << LzmaConstants.NumLowLenBits);
                return rc.DecodeBitTree(_low.AsSpan(offset, LzmaConstants.NumLowLenSymbols), LzmaConstants.NumLowLenBits);
            }

            if (rc.DecodeBit(ref _choice2) == 0)
            {
                int offset = (int)(posState << LzmaConstants.NumMidLenBits);
                return LzmaConstants.NumLowLenSymbols + rc.DecodeBitTree(_mid.AsSpan(offset, LzmaConstants.NumMidLenSymbols), LzmaConstants.NumMidLenBits);
            }

            return LzmaConstants.NumLowLenSymbols + LzmaConstants.NumMidLenSymbols + rc.DecodeBitTree(_high, LzmaConstants.NumHighLenBits);
        }
    }
}
