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

using System.Numerics;
using System.Runtime.CompilerServices;

namespace PrZip.Codecs.Lzma;

internal enum LzmaMode
{
    /// <summary>Algoritmo rápido de 7-Zip (GetOptimumFast) con HC4: niveles 1 a 4.</summary>
    Fast = 0,

    /// <summary>Parsing óptimo por precios (GetOptimum) con BT4: niveles 5 a 9.</summary>
    Optimal = 1
}

/// <summary>
/// Codificador LZMA. Port de C/LzmaEnc.c del LZMA SDK (Igor Pavlov, dominio público):
/// tablas de precios, parsing óptimo (GetOptimum / Backward), algoritmo rápido (GetOptimumFast) y
/// el bucle de emisión de LzmaEnc_CodeOneBlock, incluido el corte de chunks para LZMA2.
///
/// Diferencias con el original: los buscadores de coincidencias son los propios (HC4 / BT4), detrás
/// de un cursor que imita la interfaz de LzFind; el modelo de probabilidades usa el layout clásico,
/// idéntico en bits al de LzmaDec.c.
///
/// Las posiciones son relativas al bloque en curso (BeginBlock reinicia el diccionario): determinan
/// posState y el contexto de literales igual que processedPos en el decodificador.
/// </summary>
internal sealed class LzmaEncoder
{
    private const int NumReps = 4;
    private const int MatchLenMin = 2;
    private const int MatchLenMax = 273;
    private const int NumOpts = 1 << 11;
    private const int PackReserve = NumOpts * 8;
    private const uint InfinityPrice = 1u << 30;
    private const uint MarkLit = 0xFFFFFFFFu;
    private const int RepLenCount = 64;
    private const int NumMoveReducingBits = 4;
    private const int NumBitPriceShiftBits = 4;
    private const int LenSymbolsTotal = LzmaConstants.NumLenSymbols; // 272

    private const int StateLitAfterMatch = 4;
    private const int StateLitAfterRep = 5;
    private const int StateMatchAfterLit = 7;
    private const int StateRepAfterLit = 8;

    private static readonly byte[] MatchNextStates = [7, 7, 7, 7, 7, 7, 7, 10, 10, 10, 10, 10];
    private static readonly byte[] RepNextStates = [8, 8, 8, 8, 8, 8, 8, 11, 11, 11, 11, 11];
    private static readonly byte[] ShortRepNextStates = [9, 9, 9, 9, 9, 9, 9, 11, 11, 11, 11, 11];

    private static readonly uint[] ProbPrices = CreateProbPrices();

    private readonly RangeEncoder _rc = new();
    private LzmaProperties _properties;

    // ---- Modelo (lo que SaveState/RestoreState conservan) ----
    private int _state;
    private readonly uint[] _reps = new uint[NumReps]; // distancias en base 1, como en LzmaEnc.c

    private readonly ushort[] _isMatch = new ushort[LzmaConstants.NumStates << 4];
    private readonly ushort[] _isRep = new ushort[LzmaConstants.NumStates];
    private readonly ushort[] _isRepG0 = new ushort[LzmaConstants.NumStates];
    private readonly ushort[] _isRepG1 = new ushort[LzmaConstants.NumStates];
    private readonly ushort[] _isRepG2 = new ushort[LzmaConstants.NumStates];
    private readonly ushort[] _isRep0Long = new ushort[LzmaConstants.NumStates << 4];
    private readonly ushort[] _posSlot = new ushort[LzmaConstants.NumLenToPosStates << LzmaConstants.NumPosSlotBits];
    private readonly ushort[] _specPos = new ushort[LzmaConstants.NumFullDistances];
    private readonly ushort[] _align = new ushort[LzmaConstants.AlignTableSize];
    private readonly LenEncoder _lenEncoder = new();
    private readonly LenEncoder _repLenEncoder = new();
    private ushort[] _litProbs = [];

    // ---- Precios ----
    private readonly uint[] _alignPrices = new uint[LzmaConstants.AlignTableSize];
    private readonly uint[] _posSlotPrices = new uint[LzmaConstants.NumLenToPosStates * 64];
    private readonly uint[] _distancesPrices = new uint[LzmaConstants.NumLenToPosStates * LzmaConstants.NumFullDistances];
    private readonly uint[] _tempPrices = new uint[LzmaConstants.NumFullDistances];
    private readonly uint[] _lenPrices = new uint[16 * LenSymbolsTotal];
    private readonly uint[] _repLenPrices = new uint[16 * LenSymbolsTotal];
    private int _matchPriceCount;
    private int _repLenEncCounter;
    private int _distTableSize;

    // ---- Parsing óptimo ----
    private readonly Optimal[] _opt = new Optimal[NumOpts];
    private int _optCur;
    private int _optEnd;
    private uint _backRes;
    private int _longestMatchLen;
    private int _numPairs;
    private int _numAvail;
    private int _additionalOffset;
    private readonly uint[] _matches = new uint[MatchLenMax * 2 + 2];

    // ---- Cursor del buscador de coincidencias (equivalente al puntero de LzFind) ----
    private int _mfPos;
    private int _nowPos;

    private LzMatchFinder? _mf;
    private Snapshot? _snapshot;
    private LzmaMode _mfMode;
    private int _mfFastBytes;
    private int _mfCycles;

    private struct Optimal
    {
        public uint Price;
        public ushort State;
        public ushort Extra; // 0: normal; 1: LIT : MATCH; >1: MATCH (extra-1) : LIT : REP0 (len)
        public uint Len;
        public uint Dist;
        public uint Rep0, Rep1, Rep2, Rep3;
    }

    public LzmaMode Mode { get; set; } = LzmaMode.Fast;

    /// <summary>"fb" de 7-Zip: largo a partir del cual una coincidencia se toma sin evaluar alternativas.</summary>
    public int NumFastBytes { get; set; } = 32;

    /// <summary>"mc" de 7-Zip: ciclos máximos del buscador por posición. 0 = valor por defecto de 7-Zip.</summary>
    public int MatchCycles { get; set; }

    public LzmaEncoder() : this(new LzmaProperties(3, 0, 2, 1048576))
    {
    }

    public LzmaEncoder(LzmaProperties properties)
    {
        SetProperties(properties);
    }

    public LzmaProperties Properties => _properties;

    public void SetProperties(LzmaProperties properties)
    {
        _properties = properties;
        int litProbsSize = 0x300 << (_properties.Lc + _properties.Lp);
        if (_litProbs.Length != litProbsSize)
        {
            _litProbs = new ushort[litProbsSize];
        }

        int i = 7;
        while (i < 32 && properties.DictionarySize > (1u << i))
        {
            i++;
        }

        _distTableSize = i * 2;
        _snapshot = null;
        _mf = null;
        ResetState();
    }

    private int FastBytes => Math.Clamp(NumFastBytes, 5, MatchLenMax);

    /// <summary>
    /// Reinicia el modelo (probabilidades, estado y distancias rep); no toca el buscador.
    /// </summary>
    public void ResetState()
    {
        _state = 0;
        _reps[0] = _reps[1] = _reps[2] = _reps[3] = 1;

        const ushort init = (ushort)(LzmaConstants.BitModelTotal >> 1);
        _isMatch.AsSpan().Fill(init);
        _isRep.AsSpan().Fill(init);
        _isRepG0.AsSpan().Fill(init);
        _isRepG1.AsSpan().Fill(init);
        _isRepG2.AsSpan().Fill(init);
        _isRep0Long.AsSpan().Fill(init);
        _posSlot.AsSpan().Fill(init);
        _specPos.AsSpan().Fill(init);
        _align.AsSpan().Fill(init);
        _lenEncoder.Init();
        _repLenEncoder.Init();
        _litProbs.AsSpan().Fill(init);
    }

    public void Reset() => BeginBlock();

    /// <summary>
    /// Bloque nuevo: diccionario vacío, modelo inicial y precios recalculados.
    /// </summary>
    public void BeginBlock()
    {
        ResetState();

        for (int i = 0; i < NumOpts; i++)
        {
            _opt[i].Price = InfinityPrice;
        }

        _optCur = _optEnd = 0;
        _additionalOffset = 0;
        _mfPos = 0;
        _nowPos = 0;

        // Como LzmaEncProps_Normalize: BT4 para el modo óptimo, HC4 para el rápido; mc = (16 + fb/2) >> (hc ? 1 : 0).
        bool bt = Mode == LzmaMode.Optimal;
        int cycles = MatchCycles > 0 ? MatchCycles : (16 + (FastBytes >> 1)) >> (bt ? 0 : 1);
        if (_mf == null || _mfMode != Mode || _mfFastBytes != FastBytes || _mfCycles != cycles)
        {
            _mf = new LzMatchFinder(_properties.DictionarySize, bt, cycles, FastBytes);
            _mfMode = Mode;
            _mfFastBytes = FastBytes;
            _mfCycles = cycles;
        }
        else
        {
            _mf.Init();
        }

        InitPrices();
    }

    private void InitPrices()
    {
        if (Mode == LzmaMode.Optimal)
        {
            FillDistancesPrices();
            FillAlignPrices();
        }

        int tableSize = FastBytes + 1 - MatchLenMin;
        _repLenEncCounter = RepLenCount;
        _lenEncoder.UpdatePrices(_lenPrices, 1 << _properties.Pb, tableSize);
        _repLenEncoder.UpdatePrices(_repLenPrices, 1 << _properties.Pb, tableSize);
    }

    /// <summary>
    /// Codifica un bloque en memoria como un stream LZMA completo (LZMA1).
    /// </summary>
    public void Encode(ReadOnlySpan<byte> input, Stream outStream, bool writeEos = false)
    {
        ArgumentNullException.ThrowIfNull(outStream);

        BeginBlock();
        _rc.Init(outStream);
        CodeOneBlock(input, maxUnpack: 0, maxPack: 0);

        if (writeEos)
        {
            EncodeEndMarker((uint)_nowPos & ((1u << _properties.Pb) - 1));
        }

        _rc.FlushData();
    }

    public void Encode(Stream inStream, Stream outStream, long unpackSize = -1, bool writeEos = false)
    {
        ArgumentNullException.ThrowIfNull(inStream);
        ArgumentNullException.ThrowIfNull(outStream);

        using var ms = new MemoryStream();
        if (unpackSize >= 0)
        {
            byte[] temp = new byte[65536];
            long rem = unpackSize;
            while (rem > 0)
            {
                int r = inStream.Read(temp, 0, (int)Math.Min(temp.Length, rem));
                if (r == 0) break;
                ms.Write(temp, 0, r);
                rem -= r;
            }
        }
        else
        {
            inStream.CopyTo(ms);
        }

        Encode(ms.GetBuffer().AsSpan(0, (int)ms.Length), outStream, writeEos);
    }

    /// <summary>
    /// Codifica un chunk LZMA2 a partir de donde quedó el anterior: inicia el range coder, emite
    /// símbolos hasta acercarse a maxUnpack bytes de entrada o maxPack de salida (con las reservas de
    /// LzmaEnc.c) y lo vacía. El modelo y el diccionario continúan. Devuelve la posición alcanzada.
    /// </summary>
    public int EncodeChunk(ReadOnlySpan<byte> block, Stream chunkOut, int maxUnpack, int maxPack)
    {
        ArgumentNullException.ThrowIfNull(chunkOut);
        _rc.Init(chunkOut);
        CodeOneBlock(block, maxUnpack, maxPack);
        _rc.FlushData();
        return _nowPos;
    }

    public int Position => _nowPos;

    // ------------------------------------------------------------------------------------------
    // Cursor del buscador
    // ------------------------------------------------------------------------------------------

    private int ReadMatchDistances(ReadOnlySpan<byte> w, out int numPairs)
    {
        _additionalOffset++;
        int avail = w.Length - _mfPos;
        _numAvail = avail;

        numPairs = _mf!.GetMatches(w, _matches);
        int pos = _mfPos;
        _mfPos++;
        if (numPairs == 0)
        {
            return 0;
        }

        int len = (int)_matches[numPairs - 2];
        if (len != FastBytes)
        {
            return len;
        }

        // Extender la coincidencia más larga hasta donde dé (el buscador puede cortar en numFastBytes).
        int numAvail = Math.Min(avail, MatchLenMax);
        int dist = (int)_matches[numPairs - 1] + 1;
        while (len < numAvail && w[pos + len] == w[pos + len - dist])
        {
            len++;
        }

        return len;
    }

    private void MovePos(ReadOnlySpan<byte> w, int num)
    {
        if (num <= 0)
        {
            return;
        }

        _additionalOffset += num;
        _mf!.Skip(w, num);
        _mfPos += num;
    }

    // ------------------------------------------------------------------------------------------
    // Precios
    // ------------------------------------------------------------------------------------------

    private static uint[] CreateProbPrices()
    {
        var prices = new uint[LzmaConstants.BitModelTotal >> NumMoveReducingBits];
        for (uint i = 0; i < prices.Length; i++)
        {
            uint w = (i << NumMoveReducingBits) + (1u << (NumMoveReducingBits - 1));
            uint bitCount = 0;
            for (int j = 0; j < NumBitPriceShiftBits; j++)
            {
                w *= w;
                bitCount <<= 1;
                while (w >= (1u << 16))
                {
                    w >>= 1;
                    bitCount++;
                }
            }

            prices[i] = (uint)(LzmaConstants.NumBitModelTotalBits << NumBitPriceShiftBits) - 15 - bitCount;
        }

        return prices;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Price(uint prob, uint bit) =>
        ProbPrices[(prob ^ ((0u - bit) & (LzmaConstants.BitModelTotal - 1))) >> NumMoveReducingBits];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Price0(uint prob) => ProbPrices[prob >> NumMoveReducingBits];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Price1(uint prob) => ProbPrices[(prob ^ (LzmaConstants.BitModelTotal - 1)) >> NumMoveReducingBits];

    private static uint BitTreePrice(ReadOnlySpan<ushort> probs, int numBits, uint symbol)
    {
        uint price = 0;
        uint m = 1;
        for (int i = numBits - 1; i >= 0; i--)
        {
            uint bit = (symbol >> i) & 1;
            price += Price(probs[(int)m], bit);
            m = (m << 1) | bit;
        }

        return price;
    }

    private static uint ReverseBitTreePrice(ReadOnlySpan<ushort> probs, int offset, int numBits, uint symbol)
    {
        uint price = 0;
        uint m = 1;
        for (int i = 0; i < numBits; i++)
        {
            uint bit = symbol & 1;
            symbol >>= 1;
            price += Price(probs[offset + (int)m], bit);
            m = (m << 1) | bit;
        }

        return price;
    }

    private uint LiteralPrice(int litOffset, uint symbol)
    {
        uint price = 0;
        symbol |= 0x100;
        do
        {
            uint bit = symbol & 1;
            symbol >>= 1;
            price += Price(_litProbs[litOffset + (int)symbol], bit);
        }
        while (symbol >= 2);
        return price;
    }

    private uint LiteralMatchedPrice(int litOffset, uint symbol, uint matchByte)
    {
        uint price = 0;
        uint offs = 0x100;
        symbol |= 0x100;
        do
        {
            matchByte <<= 1;
            price += Price(_litProbs[litOffset + (int)(offs + (matchByte & offs) + (symbol >> 8))], (symbol >> 7) & 1);
            symbol <<= 1;
            offs &= ~(matchByte ^ symbol);
        }
        while (symbol < 0x10000);
        return price;
    }

    private void FillAlignPrices()
    {
        for (uint i = 0; i < LzmaConstants.AlignTableSize; i++)
        {
            _alignPrices[i] = ReverseBitTreePrice(_align, 0, LzmaConstants.NumAlignBits, i);
        }
    }

    private void FillDistancesPrices()
    {
        _matchPriceCount = 0;

        for (uint i = LzmaConstants.StartPosModelIndex; i < LzmaConstants.NumFullDistances; i++)
        {
            uint posSlot = GetPosSlot(i);
            int footerBits = (int)((posSlot >> 1) - 1);
            uint baseVal = (2u | (posSlot & 1)) << footerBits;
            _tempPrices[i] = ReverseBitTreePrice(_specPos, (int)(baseVal - posSlot - 1), footerBits, i - baseVal);
        }

        for (int lps = 0; lps < LzmaConstants.NumLenToPosStates; lps++)
        {
            ReadOnlySpan<ushort> probs = _posSlot.AsSpan(lps << LzmaConstants.NumPosSlotBits, 1 << LzmaConstants.NumPosSlotBits);
            int slotBase = lps * 64;
            for (uint slot = 0; slot < _distTableSize; slot++)
            {
                uint price = BitTreePrice(probs, LzmaConstants.NumPosSlotBits, slot);
                if (slot >= LzmaConstants.EndPosModelIndex)
                {
                    // Bits directos (sin modelo): uno por cada bit fuera de los 4 de alineación.
                    price += ((slot >> 1) - 1 - LzmaConstants.NumAlignBits) << NumBitPriceShiftBits;
                }

                _posSlotPrices[slotBase + slot] = price;
            }

            int distBase = lps * LzmaConstants.NumFullDistances;
            for (uint i = 0; i < LzmaConstants.StartPosModelIndex; i++)
            {
                _distancesPrices[distBase + i] = _posSlotPrices[slotBase + i];
            }

            for (uint i = LzmaConstants.StartPosModelIndex; i < LzmaConstants.NumFullDistances; i++)
            {
                _distancesPrices[distBase + i] = _posSlotPrices[slotBase + GetPosSlot(i)] + _tempPrices[i];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LenPrice(uint[] table, int posState, int len) => table[posState * LenSymbolsTotal + len - MatchLenMin];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ShortRepPrice(int state, int posState) =>
        Price0(_isRepG0[state]) + Price0(_isRep0Long[(state << 4) + posState]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Rep0Price(int state, int posState) =>
        Price1(_isMatch[(state << 4) + posState])
        + Price1(_isRep0Long[(state << 4) + posState])
        + Price1(_isRep[state])
        + Price0(_isRepG0[state]);

    private uint PureRepPrice(int repIndex, int state, int posState)
    {
        uint price;
        if (repIndex == 0)
        {
            price = Price0(_isRepG0[state]);
            price += Price1(_isRep0Long[(state << 4) + posState]);
        }
        else
        {
            price = Price1(_isRepG0[state]);
            if (repIndex == 1)
            {
                price += Price0(_isRepG1[state]);
            }
            else
            {
                price += Price1(_isRepG1[state]);
                price += Price(_isRepG2[state], (uint)(repIndex - 2));
            }
        }

        return price;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint MatchDistPrice(uint dist, int lenToPosState)
    {
        if (dist < LzmaConstants.NumFullDistances)
        {
            return _distancesPrices[lenToPosState * LzmaConstants.NumFullDistances + (int)dist];
        }

        return _posSlotPrices[lenToPosState * 64 + (int)GetPosSlot(dist)] + _alignPrices[dist & (LzmaConstants.AlignTableSize - 1)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LenToPosState(int len) => len < MatchLenMin + 4 ? len - MatchLenMin : 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLitState(int state) => state < 7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LitOffset(int position, byte prevByte)
    {
        int lpMask = (1 << _properties.Lp) - 1;
        return 0x300 * (((position & lpMask) << _properties.Lc) + (prevByte >> (8 - _properties.Lc)));
    }

    // ------------------------------------------------------------------------------------------
    // GetOptimum / Backward (parsing óptimo)
    // ------------------------------------------------------------------------------------------

    private int Backward(int cur)
    {
        int wr = cur + 1;
        _optEnd = wr;

        while (true)
        {
            uint dist = _opt[cur].Dist;
            int len = (int)_opt[cur].Len;
            int extra = _opt[cur].Extra;
            cur -= len;

            if (extra != 0)
            {
                wr--;
                _opt[wr].Len = (uint)len;
                cur -= extra;
                len = extra;
                if (extra == 1)
                {
                    _opt[wr].Dist = dist;
                    dist = MarkLit;
                }
                else
                {
                    _opt[wr].Dist = 0;
                    len--;
                    wr--;
                    _opt[wr].Dist = MarkLit;
                    _opt[wr].Len = 1;
                }
            }

            if (cur == 0)
            {
                _backRes = dist;
                _optCur = wr;
                return len;
            }

            wr--;
            _opt[wr].Dist = dist;
            _opt[wr].Len = (uint)len;
        }
    }

    private int GetOptimum(ReadOnlySpan<byte> w, int position)
    {
        Span<uint> reps = stackalloc uint[NumReps];
        Span<int> repLens = stackalloc int[NumReps];
        int pbMask = (1 << _properties.Pb) - 1;
        int fastBytes = FastBytes;
        int last, cur;

        {
            int mainLen, numPairs;
            _optCur = _optEnd = 0;

            if (_additionalOffset == 0)
            {
                mainLen = ReadMatchDistances(w, out numPairs);
            }
            else
            {
                mainLen = _longestMatchLen;
                numPairs = _numPairs;
            }

            int numAvail = _numAvail;
            if (numAvail < 2)
            {
                _backRes = MarkLit;
                return 1;
            }

            if (numAvail > MatchLenMax)
            {
                numAvail = MatchLenMax;
            }

            int data = _mfPos - 1;
            int repMaxIndex = 0;

            for (int i = 0; i < NumReps; i++)
            {
                reps[i] = _reps[i];
                int data2 = data - (int)reps[i];
                if (data2 < 0 || w[data] != w[data2] || w[data + 1] != w[data2 + 1])
                {
                    repLens[i] = 0;
                    continue;
                }

                int len = 2;
                while (len < numAvail && w[data + len] == w[data2 + len])
                {
                    len++;
                }

                repLens[i] = len;
                if (len > repLens[repMaxIndex])
                {
                    repMaxIndex = i;
                }

                if (len == MatchLenMax)
                {
                    break;
                }
            }

            if (repLens[repMaxIndex] >= fastBytes)
            {
                _backRes = (uint)repMaxIndex;
                int len = repLens[repMaxIndex];
                MovePos(w, len - 1);
                return len;
            }

            if (mainLen >= fastBytes)
            {
                _backRes = _matches[numPairs - 1] + NumReps;
                MovePos(w, mainLen - 1);
                return mainLen;
            }

            byte curByte = w[data];
            int rep0Pos = data - (int)reps[0];
            byte matchByte = rep0Pos >= 0 ? w[rep0Pos] : (byte)0;

            last = Math.Max(repLens[repMaxIndex], mainLen);

            if (last < 2 && curByte != matchByte)
            {
                _backRes = MarkLit;
                return 1;
            }

            _opt[0].State = (ushort)_state;
            int posState = position & pbMask;

            {
                int litOffset = LitOffset(position, data > 0 ? w[data - 1] : (byte)0);
                _opt[1].Price = Price0(_isMatch[(_state << 4) + posState]) +
                    (!IsLitState(_state)
                        ? LiteralMatchedPrice(litOffset, curByte, matchByte)
                        : LiteralPrice(litOffset, curByte));
            }

            _opt[1].Dist = MarkLit;
            _opt[1].Extra = 0;

            uint matchPrice = Price1(_isMatch[(_state << 4) + posState]);
            uint repMatchPrice = matchPrice + Price1(_isRep[_state]);

            if (matchByte == curByte && repLens[0] == 0)
            {
                uint shortRepPrice = repMatchPrice + ShortRepPrice(_state, posState);
                if (shortRepPrice < _opt[1].Price)
                {
                    _opt[1].Price = shortRepPrice;
                    _opt[1].Dist = 0;
                    _opt[1].Extra = 0;
                }

                if (last < 2)
                {
                    _backRes = _opt[1].Dist;
                    return 1;
                }
            }

            _opt[1].Len = 1;
            _opt[0].Rep0 = reps[0];
            _opt[0].Rep1 = reps[1];
            _opt[0].Rep2 = reps[2];
            _opt[0].Rep3 = reps[3];

            // ---------- REP ----------
            for (int i = 0; i < NumReps; i++)
            {
                int repLen = repLens[i];
                if (repLen < 2)
                {
                    continue;
                }

                uint price = repMatchPrice + PureRepPrice(i, _state, posState);
                do
                {
                    uint price2 = price + LenPrice(_repLenPrices, posState, repLen);
                    ref Optimal opt = ref _opt[repLen];
                    if (price2 < opt.Price)
                    {
                        opt.Price = price2;
                        opt.Len = (uint)repLen;
                        opt.Dist = (uint)i;
                        opt.Extra = 0;
                    }
                }
                while (--repLen >= 2);
            }

            // ---------- MATCH ----------
            {
                int len = repLens[0] + 1;
                if (len <= mainLen)
                {
                    int offs = 0;
                    uint normalMatchPrice = matchPrice + Price0(_isRep[_state]);

                    if (len < 2)
                    {
                        len = 2;
                    }
                    else
                    {
                        while (len > _matches[offs])
                        {
                            offs += 2;
                        }
                    }

                    for (; ; len++)
                    {
                        uint dist = _matches[offs + 1];
                        uint price = normalMatchPrice + LenPrice(_lenPrices, posState, len)
                                     + MatchDistPrice(dist, LenToPosState(len));

                        ref Optimal opt = ref _opt[len];
                        if (price < opt.Price)
                        {
                            opt.Price = price;
                            opt.Len = (uint)len;
                            opt.Dist = dist + NumReps;
                            opt.Extra = 0;
                        }

                        if (len == _matches[offs])
                        {
                            offs += 2;
                            if (offs == numPairs)
                            {
                                break;
                            }
                        }
                    }
                }
            }

            cur = 0;
        }

        // ---------- Optimal Parsing ----------
        while (true)
        {
            if (++cur == last)
            {
                break;
            }

            if (cur >= NumOpts - 64)
            {
                uint bestPrice = _opt[cur].Price;
                int best = cur;
                for (int j = cur + 1; j <= last; j++)
                {
                    uint price2 = _opt[j].Price;
                    if (bestPrice >= price2)
                    {
                        bestPrice = price2;
                        best = j;
                    }
                }

                int delta = best - cur;
                if (delta != 0)
                {
                    MovePos(w, delta);
                }

                cur = best;
                break;
            }

            int newLen = ReadMatchDistances(w, out int numPairs);

            if (newLen >= fastBytes)
            {
                _numPairs = numPairs;
                _longestMatchLen = newLen;
                break;
            }

            ref Optimal curOpt = ref _opt[cur];
            position++;

            int prev = cur - (int)curOpt.Len;
            int state;

            if (curOpt.Len == 1)
            {
                state = _opt[prev].State;
                state = curOpt.Dist == 0 ? ShortRepNextStates[state] : LzmaConstants.LiteralNextStates[state];
            }
            else
            {
                uint dist = curOpt.Dist;

                if (curOpt.Extra != 0)
                {
                    prev -= curOpt.Extra;
                    state = StateRepAfterLit;
                    if (curOpt.Extra == 1)
                    {
                        state = dist < NumReps ? StateRepAfterLit : StateMatchAfterLit;
                    }
                }
                else
                {
                    state = _opt[prev].State;
                    state = dist < NumReps ? RepNextStates[state] : MatchNextStates[state];
                }

                ref Optimal prevOpt = ref _opt[prev];
                uint b0 = prevOpt.Rep0;

                if (dist < NumReps)
                {
                    if (dist == 0)
                    {
                        reps[0] = b0;
                        reps[1] = prevOpt.Rep1;
                        reps[2] = prevOpt.Rep2;
                        reps[3] = prevOpt.Rep3;
                    }
                    else
                    {
                        reps[1] = b0;
                        b0 = prevOpt.Rep1;
                        if (dist == 1)
                        {
                            reps[0] = b0;
                            reps[2] = prevOpt.Rep2;
                            reps[3] = prevOpt.Rep3;
                        }
                        else
                        {
                            reps[2] = b0;
                            reps[0] = dist == 2 ? prevOpt.Rep2 : prevOpt.Rep3;
                            reps[3] = dist == 2 ? prevOpt.Rep3 : prevOpt.Rep2;
                        }
                    }
                }
                else
                {
                    reps[0] = dist - NumReps + 1;
                    reps[1] = b0;
                    reps[2] = prevOpt.Rep1;
                    reps[3] = prevOpt.Rep2;
                }
            }

            curOpt.State = (ushort)state;
            curOpt.Rep0 = reps[0];
            curOpt.Rep1 = reps[1];
            curOpt.Rep2 = reps[2];
            curOpt.Rep3 = reps[3];

            int data = _mfPos - 1;
            byte curByte = w[data];
            int rep0Pos = data - (int)reps[0];
            byte matchByte = rep0Pos >= 0 ? w[rep0Pos] : (byte)0;
            int posState = position & pbMask;

            uint curPrice = curOpt.Price;
            uint litPrice;
            uint matchPrice;
            {
                ushort prob = _isMatch[(state << 4) + posState];
                matchPrice = curPrice + Price1(prob);
                litPrice = curPrice + Price0(prob);
            }

            ref Optimal nextOpt = ref _opt[cur + 1];
            bool nextIsLit = false;

            if ((nextOpt.Price < InfinityPrice && matchByte == curByte) || litPrice > nextOpt.Price)
            {
                litPrice = 0;
            }
            else
            {
                int litOffset = LitOffset(position, data > 0 ? w[data - 1] : (byte)0);
                litPrice += !IsLitState(state)
                    ? LiteralMatchedPrice(litOffset, curByte, matchByte)
                    : LiteralPrice(litOffset, curByte);

                if (litPrice < nextOpt.Price)
                {
                    nextOpt.Price = litPrice;
                    nextOpt.Len = 1;
                    nextOpt.Dist = MarkLit;
                    nextOpt.Extra = 0;
                    nextIsLit = true;
                }
            }

            uint repMatchPrice = matchPrice + Price1(_isRep[state]);

            int numAvailFull = _numAvail;
            {
                int temp = NumOpts - 1 - cur;
                if (numAvailFull > temp)
                {
                    numAvailFull = temp;
                }
            }

            // ---------- SHORT_REP ----------
            if (IsLitState(state) && matchByte == curByte && repMatchPrice < nextOpt.Price
                && (nextOpt.Len < 2 || nextOpt.Dist != 0))
            {
                uint shortRepPrice = repMatchPrice + ShortRepPrice(state, posState);
                if (shortRepPrice < nextOpt.Price)
                {
                    nextOpt.Price = shortRepPrice;
                    nextOpt.Len = 1;
                    nextOpt.Dist = 0;
                    nextOpt.Extra = 0;
                    nextIsLit = false;
                }
            }

            if (numAvailFull < 2)
            {
                continue;
            }

            int numAvail = numAvailFull <= fastBytes ? numAvailFull : fastBytes;

            // ---------- LIT : REP_0 ----------
            if (!nextIsLit && litPrice != 0 && matchByte != curByte && numAvailFull > 2)
            {
                int data2 = data - (int)reps[0];
                if (data2 >= 0 && w[data + 1] == w[data2 + 1] && w[data + 2] == w[data2 + 2])
                {
                    int limit = Math.Min(fastBytes + 1, numAvailFull);
                    int len = 3;
                    while (len < limit && w[data + len] == w[data2 + len])
                    {
                        len++;
                    }

                    int state2 = LzmaConstants.LiteralNextStates[state];
                    int posState2 = (position + 1) & pbMask;
                    uint price = litPrice + Rep0Price(state2, posState2);
                    int offset = cur + len;
                    if (last < offset)
                    {
                        last = offset;
                    }

                    len--;
                    uint price2 = price + LenPrice(_repLenPrices, posState2, len);
                    ref Optimal opt = ref _opt[offset];
                    if (price2 < opt.Price)
                    {
                        opt.Price = price2;
                        opt.Len = (uint)len;
                        opt.Dist = 0;
                        opt.Extra = 1;
                    }
                }
            }

            int startLen = 2;

            // ---------- REP ----------
            for (int repIndex = 0; repIndex < NumReps; repIndex++)
            {
                int data2 = data - (int)reps[repIndex];
                if (data2 < 0 || w[data] != w[data2] || w[data + 1] != w[data2 + 1])
                {
                    continue;
                }

                int len = 2;
                while (len < numAvail && w[data + len] == w[data2 + len])
                {
                    len++;
                }

                if (last < cur + len)
                {
                    last = cur + len;
                }

                uint price = repMatchPrice + PureRepPrice(repIndex, state, posState);
                {
                    int len2 = len;
                    do
                    {
                        uint price2 = price + LenPrice(_repLenPrices, posState, len2);
                        ref Optimal opt = ref _opt[cur + len2];
                        if (price2 < opt.Price)
                        {
                            opt.Price = price2;
                            opt.Len = (uint)len2;
                            opt.Dist = (uint)repIndex;
                            opt.Extra = 0;
                        }
                    }
                    while (--len2 >= 2);
                }

                if (repIndex == 0)
                {
                    startLen = len + 1;
                }

                // ---------- REP : LIT : REP_0 ----------
                {
                    int len2 = len + 1;
                    int limit = Math.Min(len2 + fastBytes, numAvailFull);
                    len2 += 2;
                    if (len2 <= limit && w[data + len2 - 2] == w[data2 + len2 - 2] && w[data + len2 - 1] == w[data2 + len2 - 1])
                    {
                        int state2 = RepNextStates[state];
                        int posState2 = (position + len) & pbMask;
                        price += LenPrice(_repLenPrices, posState, len)
                                 + Price0(_isMatch[(state2 << 4) + posState2])
                                 + LiteralMatchedPrice(LitOffset(position + len, w[data + len - 1]), w[data + len], w[data2 + len]);

                        state2 = StateLitAfterRep;
                        posState2 = (posState2 + 1) & pbMask;
                        price += Rep0Price(state2, posState2);

                        while (len2 < limit && w[data + len2] == w[data2 + len2])
                        {
                            len2++;
                        }

                        len2 -= len;
                        int offset = cur + len + len2;
                        if (last < offset)
                        {
                            last = offset;
                        }

                        len2--;
                        uint price2 = price + LenPrice(_repLenPrices, posState2, len2);
                        ref Optimal opt = ref _opt[offset];
                        if (price2 < opt.Price)
                        {
                            opt.Price = price2;
                            opt.Len = (uint)len2;
                            opt.Extra = (ushort)(len + 1);
                            opt.Dist = (uint)repIndex;
                        }
                    }
                }
            }

            // ---------- MATCH ----------
            if (newLen > numAvail)
            {
                newLen = numAvail;
                numPairs = 0;
                while (newLen > _matches[numPairs])
                {
                    numPairs += 2;
                }

                _matches[numPairs] = (uint)newLen;
                numPairs += 2;
            }

            if (newLen >= startLen)
            {
                uint normalMatchPrice = matchPrice + Price0(_isRep[state]);

                if (last < cur + newLen)
                {
                    last = cur + newLen;
                }

                int offs = 0;
                while (startLen > _matches[offs])
                {
                    offs += 2;
                }

                uint dist = _matches[offs + 1];

                for (int len = startLen; ; len++)
                {
                    uint price = normalMatchPrice + LenPrice(_lenPrices, posState, len)
                                 + MatchDistPrice(dist, LenToPosState(len));

                    ref Optimal opt = ref _opt[cur + len];
                    if (price < opt.Price)
                    {
                        opt.Price = price;
                        opt.Len = (uint)len;
                        opt.Dist = dist + NumReps;
                        opt.Extra = 0;
                    }

                    if (len == _matches[offs])
                    {
                        // ---------- MATCH : LIT : REP_0 ----------
                        int data2 = data - (int)dist - 1;
                        int len2 = len + 1;
                        int limit = Math.Min(len2 + fastBytes, numAvailFull);
                        len2 += 2;
                        if (len2 <= limit && w[data + len2 - 2] == w[data2 + len2 - 2] && w[data + len2 - 1] == w[data2 + len2 - 1])
                        {
                            while (len2 < limit && w[data + len2] == w[data2 + len2])
                            {
                                len2++;
                            }

                            len2 -= len;

                            int state2 = MatchNextStates[state];
                            int posState2 = (position + len) & pbMask;
                            price += Price0(_isMatch[(state2 << 4) + posState2]);
                            price += LiteralMatchedPrice(LitOffset(position + len, w[data + len - 1]), w[data + len], w[data2 + len]);

                            state2 = StateLitAfterMatch;
                            posState2 = (posState2 + 1) & pbMask;
                            price += Rep0Price(state2, posState2);

                            int offset = cur + len + len2;
                            if (last < offset)
                            {
                                last = offset;
                            }

                            len2--;
                            uint price2 = price + LenPrice(_repLenPrices, posState2, len2);
                            ref Optimal opt2 = ref _opt[offset];
                            if (price2 < opt2.Price)
                            {
                                opt2.Price = price2;
                                opt2.Len = (uint)len2;
                                opt2.Extra = (ushort)(len + 1);
                                opt2.Dist = dist + NumReps;
                            }
                        }

                        offs += 2;
                        if (offs == numPairs)
                        {
                            break;
                        }

                        dist = _matches[offs + 1];
                    }
                }
            }
        }

        do
        {
            _opt[last].Price = InfinityPrice;
        }
        while (--last != 0);

        return Backward(cur);
    }

    // ------------------------------------------------------------------------------------------
    // GetOptimumFast (niveles rápidos)
    // ------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ChangePair(uint smallDist, uint bigDist) => (bigDist >> 7) > smallDist;

    private int GetOptimumFast(ReadOnlySpan<byte> w)
    {
        int mainLen, numPairs;
        if (_additionalOffset == 0)
        {
            mainLen = ReadMatchDistances(w, out numPairs);
        }
        else
        {
            mainLen = _longestMatchLen;
            numPairs = _numPairs;
        }

        int fastBytes = FastBytes;
        int numAvail = _numAvail;
        _backRes = MarkLit;
        if (numAvail < 2)
        {
            return 1;
        }

        if (numAvail > MatchLenMax)
        {
            numAvail = MatchLenMax;
        }

        int data = _mfPos - 1;
        int repLen = 0;
        int repIndex = 0;

        for (int i = 0; i < NumReps; i++)
        {
            int data2 = data - (int)_reps[i];
            if (data2 < 0 || w[data] != w[data2] || w[data + 1] != w[data2 + 1])
            {
                continue;
            }

            int len = 2;
            while (len < numAvail && w[data + len] == w[data2 + len])
            {
                len++;
            }

            if (len >= fastBytes)
            {
                _backRes = (uint)i;
                MovePos(w, len - 1);
                return len;
            }

            if (len > repLen)
            {
                repIndex = i;
                repLen = len;
            }
        }

        if (mainLen >= fastBytes)
        {
            _backRes = _matches[numPairs - 1] + NumReps;
            MovePos(w, mainLen - 1);
            return mainLen;
        }

        uint mainDist = 0;
        if (mainLen >= 2)
        {
            mainDist = _matches[numPairs - 1];
            while (numPairs > 2)
            {
                if (mainLen != _matches[numPairs - 4] + 1)
                {
                    break;
                }

                uint dist2 = _matches[numPairs - 3];
                if (!ChangePair(dist2, mainDist))
                {
                    break;
                }

                numPairs -= 2;
                mainLen--;
                mainDist = dist2;
            }

            if (mainLen == 2 && mainDist >= 0x80)
            {
                mainLen = 1;
            }
        }

        if (repLen >= 2
            && (repLen + 1 >= mainLen
                || (repLen + 2 >= mainLen && mainDist >= (1 << 9))
                || (repLen + 3 >= mainLen && mainDist >= (1 << 15))))
        {
            _backRes = (uint)repIndex;
            MovePos(w, repLen - 1);
            return repLen;
        }

        if (mainLen < 2 || numAvail <= 2)
        {
            return 1;
        }

        {
            int len1 = ReadMatchDistances(w, out _numPairs);
            _longestMatchLen = len1;

            if (len1 >= 2)
            {
                uint newDist = _matches[_numPairs - 1];
                if ((len1 >= mainLen && newDist < mainDist)
                    || (len1 == mainLen + 1 && !ChangePair(mainDist, newDist))
                    || len1 > mainLen + 1
                    || (len1 + 1 >= mainLen && mainLen >= 3 && ChangePair(newDist, mainDist)))
                {
                    return 1;
                }
            }
        }

        data = _mfPos - 1;
        for (int i = 0; i < NumReps; i++)
        {
            int data2 = data - (int)_reps[i];
            if (data2 < 0 || w[data] != w[data2] || w[data + 1] != w[data2 + 1])
            {
                continue;
            }

            int limit = mainLen - 1;
            for (int len = 2; ; len++)
            {
                if (len >= limit)
                {
                    return 1;
                }

                if (w[data + len] != w[data2 + len])
                {
                    break;
                }
            }
        }

        _backRes = mainDist + NumReps;
        if (mainLen != 2)
        {
            MovePos(w, mainLen - 2);
        }

        return mainLen;
    }

    // ------------------------------------------------------------------------------------------
    // Bucle de emisión (LzmaEnc_CodeOneBlock)
    // ------------------------------------------------------------------------------------------

    private void CodeOneBlock(ReadOnlySpan<byte> w, int maxUnpack, int maxPack)
    {
        int pbMask = (1 << _properties.Pb) - 1;
        bool fast = Mode == LzmaMode.Fast;
        int startPos = _nowPos;

        if (_nowPos == 0)
        {
            if (w.Length == 0)
            {
                return;
            }

            // El primer byte de un bloque siempre es un literal sin contexto.
            ReadMatchDistances(w, out _);
            _rc.EncodeBit(ref _isMatch[0], 0);
            EncodeLiteralPlain(0, w[0]);
            _additionalOffset--;
            _nowPos++;
        }

        if (_mfPos >= w.Length && _additionalOffset == 0)
        {
            return;
        }

        while (true)
        {
            int len;
            if (fast)
            {
                len = GetOptimumFast(w);
            }
            else if (_optEnd == _optCur)
            {
                len = GetOptimum(w, _nowPos);
            }
            else
            {
                len = (int)_opt[_optCur].Len;
                _backRes = _opt[_optCur].Dist;
                _optCur++;
            }

            int posState = _nowPos & pbMask;
            uint dist = _backRes;
            int isMatchIndex = (_state << 4) + posState;

            if (dist == MarkLit)
            {
                _rc.EncodeBit(ref _isMatch[isMatchIndex], 0);
                int data = _nowPos;
                int litOffset = LitOffset(_nowPos, data > 0 ? w[data - 1] : (byte)0);
                int state = _state;
                _state = LzmaConstants.LiteralNextStates[state];
                if (IsLitState(state))
                {
                    EncodeLiteralPlain(litOffset, w[data]);
                }
                else
                {
                    EncodeLiteralMatched(litOffset, w[data], w[data - (int)_reps[0]]);
                }
            }
            else
            {
                _rc.EncodeBit(ref _isMatch[isMatchIndex], 1);

                if (dist < NumReps)
                {
                    _rc.EncodeBit(ref _isRep[_state], 1);
                    if (dist == 0)
                    {
                        _rc.EncodeBit(ref _isRepG0[_state], 0);
                        if (len != 1)
                        {
                            _rc.EncodeBit(ref _isRep0Long[isMatchIndex], 1);
                        }
                        else
                        {
                            _rc.EncodeBit(ref _isRep0Long[isMatchIndex], 0);
                            _state = ShortRepNextStates[_state];
                        }
                    }
                    else
                    {
                        _rc.EncodeBit(ref _isRepG0[_state], 1);
                        uint d;
                        if (dist == 1)
                        {
                            _rc.EncodeBit(ref _isRepG1[_state], 0);
                            d = _reps[1];
                        }
                        else
                        {
                            _rc.EncodeBit(ref _isRepG1[_state], 1);
                            if (dist == 2)
                            {
                                _rc.EncodeBit(ref _isRepG2[_state], 0);
                                d = _reps[2];
                            }
                            else
                            {
                                _rc.EncodeBit(ref _isRepG2[_state], 1);
                                d = _reps[3];
                                _reps[3] = _reps[2];
                            }

                            _reps[2] = _reps[1];
                        }

                        _reps[1] = _reps[0];
                        _reps[0] = d;
                    }

                    if (len != 1)
                    {
                        _repLenEncoder.Encode(_rc, (uint)(len - MatchLenMin), (uint)posState);
                        --_repLenEncCounter;
                        _state = RepNextStates[_state];
                    }
                }
                else
                {
                    _rc.EncodeBit(ref _isRep[_state], 0);
                    _state = MatchNextStates[_state];
                    _lenEncoder.Encode(_rc, (uint)(len - MatchLenMin), (uint)posState);

                    dist -= NumReps;
                    _reps[3] = _reps[2];
                    _reps[2] = _reps[1];
                    _reps[1] = _reps[0];
                    _reps[0] = dist + 1;
                    _matchPriceCount++;
                    EncodeDistance(dist, LenToPosState(len));
                }
            }

            _nowPos += len;
            _additionalOffset -= len;

            if (_additionalOffset == 0)
            {
                if (!fast)
                {
                    if (_matchPriceCount >= 64)
                    {
                        FillAlignPrices();
                        FillDistancesPrices();
                        _lenEncoder.UpdatePrices(_lenPrices, 1 << _properties.Pb, FastBytes + 1 - MatchLenMin);
                    }

                    if (_repLenEncCounter <= 0)
                    {
                        _repLenEncCounter = RepLenCount;
                        _repLenEncoder.UpdatePrices(_repLenPrices, 1 << _properties.Pb, FastBytes + 1 - MatchLenMin);
                    }
                }

                if (_mfPos >= w.Length)
                {
                    break;
                }

                if (maxPack > 0)
                {
                    int processed = _nowPos - startPos;
                    if (processed + NumOpts + 300 >= maxUnpack || _rc.PendingBytes + PackReserve >= maxPack)
                    {
                        break;
                    }
                }
            }
        }
    }

    private void EncodeLiteralPlain(int litOffset, uint symbol)
    {
        symbol |= 0x100;
        do
        {
            _rc.EncodeBit(ref _litProbs[litOffset + (int)(symbol >> 8)], (symbol >> 7) & 1);
            symbol <<= 1;
        }
        while (symbol < 0x10000);
    }

    private void EncodeLiteralMatched(int litOffset, uint symbol, uint matchByte)
    {
        uint offs = 0x100;
        symbol |= 0x100;
        do
        {
            matchByte <<= 1;
            int index = litOffset + (int)(offs + (matchByte & offs) + (symbol >> 8));
            uint bit = (symbol >> 7) & 1;
            symbol <<= 1;
            offs &= ~(matchByte ^ symbol);
            _rc.EncodeBit(ref _litProbs[index], bit);
        }
        while (symbol < 0x10000);
    }

    private void EncodeDistance(uint dist, int lenToPosState)
    {
        uint posSlot = GetPosSlot(dist);
        _rc.EncodeBitTree(_posSlot.AsSpan(lenToPosState << LzmaConstants.NumPosSlotBits, 1 << LzmaConstants.NumPosSlotBits),
            LzmaConstants.NumPosSlotBits, posSlot);

        if (posSlot >= LzmaConstants.StartPosModelIndex)
        {
            int footerBits = (int)((posSlot >> 1) - 1);
            uint baseVal = (2u | (posSlot & 1)) << footerBits;
            uint posReduced = dist - baseVal;

            if (posSlot < LzmaConstants.EndPosModelIndex)
            {
                _rc.EncodeReverseBitTree(_specPos, (int)(baseVal - posSlot - 1), footerBits, posReduced);
            }
            else
            {
                _rc.EncodeDirectBits(posReduced >> LzmaConstants.NumAlignBits, footerBits - LzmaConstants.NumAlignBits);
                _rc.EncodeReverseBitTree(_align, LzmaConstants.NumAlignBits, posReduced & 0x0F);
            }
        }
    }

    private void EncodeEndMarker(uint posState)
    {
        int isMatchIndex = (_state << 4) + (int)posState;
        _rc.EncodeBit(ref _isMatch[isMatchIndex], 1);
        _rc.EncodeBit(ref _isRep[_state], 0);
        _state = MatchNextStates[_state];
        _lenEncoder.Encode(_rc, 0, posState);
        EncodeDistance(0xFFFFFFFFu, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetPosSlot(uint dist)
    {
        if (dist < 4) return dist;
        int n = 31 - BitOperations.LeadingZeroCount(dist);
        return (uint)((n << 1) | (int)((dist >> (n - 1)) & 1));
    }

    // ------------------------------------------------------------------------------------------
    // Estado para LZMA2 (chunks sin comprimir)
    // ------------------------------------------------------------------------------------------

    public void SaveState()
    {
        _snapshot ??= new Snapshot(_litProbs.Length);
        var s = _snapshot;
        s.State = _state;
        _reps.CopyTo(s.Reps, 0);
        _isMatch.CopyTo(s.IsMatch, 0);
        _isRep.CopyTo(s.IsRep, 0);
        _isRepG0.CopyTo(s.IsRepG0, 0);
        _isRepG1.CopyTo(s.IsRepG1, 0);
        _isRepG2.CopyTo(s.IsRepG2, 0);
        _isRep0Long.CopyTo(s.IsRep0Long, 0);
        _posSlot.CopyTo(s.PosSlot, 0);
        _specPos.CopyTo(s.SpecPos, 0);
        _align.CopyTo(s.Align, 0);
        _litProbs.CopyTo(s.LitProbs, 0);
        _lenEncoder.CopyTo(s.Len);
        _repLenEncoder.CopyTo(s.RepLen);
    }

    public void RestoreState()
    {
        var s = _snapshot ?? throw new InvalidOperationException("RestoreState sin SaveState previo.");
        _state = s.State;
        s.Reps.CopyTo(_reps, 0);
        s.IsMatch.CopyTo(_isMatch, 0);
        s.IsRep.CopyTo(_isRep, 0);
        s.IsRepG0.CopyTo(_isRepG0, 0);
        s.IsRepG1.CopyTo(_isRepG1, 0);
        s.IsRepG2.CopyTo(_isRepG2, 0);
        s.IsRep0Long.CopyTo(_isRep0Long, 0);
        s.PosSlot.CopyTo(_posSlot, 0);
        s.SpecPos.CopyTo(_specPos, 0);
        s.Align.CopyTo(_align, 0);
        s.LitProbs.CopyTo(_litProbs, 0);
        s.Len.CopyTo(_lenEncoder);
        s.RepLen.CopyTo(_repLenEncoder);
    }

    private sealed class Snapshot(int litProbsLength)
    {
        public int State;
        public readonly uint[] Reps = new uint[NumReps];
        public readonly ushort[] IsMatch = new ushort[LzmaConstants.NumStates << 4];
        public readonly ushort[] IsRep = new ushort[LzmaConstants.NumStates];
        public readonly ushort[] IsRepG0 = new ushort[LzmaConstants.NumStates];
        public readonly ushort[] IsRepG1 = new ushort[LzmaConstants.NumStates];
        public readonly ushort[] IsRepG2 = new ushort[LzmaConstants.NumStates];
        public readonly ushort[] IsRep0Long = new ushort[LzmaConstants.NumStates << 4];
        public readonly ushort[] PosSlot = new ushort[LzmaConstants.NumLenToPosStates << LzmaConstants.NumPosSlotBits];
        public readonly ushort[] SpecPos = new ushort[LzmaConstants.NumFullDistances];
        public readonly ushort[] Align = new ushort[LzmaConstants.AlignTableSize];
        public readonly ushort[] LitProbs = new ushort[litProbsLength];
        public readonly LenEncoder Len = new();
        public readonly LenEncoder RepLen = new();
    }

    /// <summary>
    /// Codificador de largos (layout clásico: choice, choice2, low[16][8], mid[16][8], high[256]).
    /// </summary>
    private sealed class LenEncoder
    {
        private ushort _choice1;
        private ushort _choice2;
        private readonly ushort[] _low = new ushort[16 << LzmaConstants.NumLowLenBits];
        private readonly ushort[] _mid = new ushort[16 << LzmaConstants.NumMidLenBits];
        private readonly ushort[] _high = new ushort[LzmaConstants.NumHighLenSymbols];

        public void Init()
        {
            const ushort init = (ushort)(LzmaConstants.BitModelTotal >> 1);
            _choice1 = init;
            _choice2 = init;
            _low.AsSpan().Fill(init);
            _mid.AsSpan().Fill(init);
            _high.AsSpan().Fill(init);
        }

        public void CopyTo(LenEncoder other)
        {
            other._choice1 = _choice1;
            other._choice2 = _choice2;
            _low.CopyTo(other._low, 0);
            _mid.CopyTo(other._mid, 0);
            _high.CopyTo(other._high, 0);
        }

        public void Encode(RangeEncoder rc, uint symbol, uint posState)
        {
            if (symbol < LzmaConstants.NumLowLenSymbols)
            {
                rc.EncodeBit(ref _choice1, 0);
                rc.EncodeBitTree(_low.AsSpan((int)(posState << LzmaConstants.NumLowLenBits), LzmaConstants.NumLowLenSymbols),
                    LzmaConstants.NumLowLenBits, symbol);
            }
            else
            {
                rc.EncodeBit(ref _choice1, 1);
                if (symbol < LzmaConstants.NumLowLenSymbols + LzmaConstants.NumMidLenSymbols)
                {
                    rc.EncodeBit(ref _choice2, 0);
                    rc.EncodeBitTree(_mid.AsSpan((int)(posState << LzmaConstants.NumMidLenBits), LzmaConstants.NumMidLenSymbols),
                        LzmaConstants.NumMidLenBits, symbol - LzmaConstants.NumLowLenSymbols);
                }
                else
                {
                    rc.EncodeBit(ref _choice2, 1);
                    rc.EncodeBitTree(_high, LzmaConstants.NumHighLenBits,
                        symbol - LzmaConstants.NumLowLenSymbols - LzmaConstants.NumMidLenSymbols);
                }
            }
        }

        /// <summary>
        /// prices[posState * 272 + sym] para sym en [0, tableSize) (LenPriceEnc_UpdateTables).
        /// </summary>
        public void UpdatePrices(uint[] prices, int numPosStates, int tableSize)
        {
            uint a0 = Price0(_choice1);
            uint a1 = Price1(_choice1);
            uint b0 = a1 + Price0(_choice2);
            uint b1 = a1 + Price1(_choice2);

            for (int posState = 0; posState < numPosStates; posState++)
            {
                int row = posState * LenSymbolsTotal;
                ReadOnlySpan<ushort> low = _low.AsSpan(posState << LzmaConstants.NumLowLenBits, LzmaConstants.NumLowLenSymbols);
                ReadOnlySpan<ushort> mid = _mid.AsSpan(posState << LzmaConstants.NumMidLenBits, LzmaConstants.NumMidLenSymbols);

                for (int sym = 0; sym < tableSize; sym++)
                {
                    uint price;
                    if (sym < LzmaConstants.NumLowLenSymbols)
                    {
                        price = a0 + BitTreePrice(low, LzmaConstants.NumLowLenBits, (uint)sym);
                    }
                    else if (sym < LzmaConstants.NumLowLenSymbols + LzmaConstants.NumMidLenSymbols)
                    {
                        price = b0 + BitTreePrice(mid, LzmaConstants.NumMidLenBits, (uint)(sym - LzmaConstants.NumLowLenSymbols));
                    }
                    else
                    {
                        price = b1 + BitTreePrice(_high, LzmaConstants.NumHighLenBits,
                            (uint)(sym - LzmaConstants.NumLowLenSymbols - LzmaConstants.NumMidLenSymbols));
                    }

                    prices[row + sym] = price;
                }
            }
        }
    }
}
