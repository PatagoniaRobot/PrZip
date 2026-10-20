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

namespace PrZip.Codecs.Ppmd;

/// <summary>Fuente de bytes del decodificador de rango (en RAR, el buffer de bits del descompresor).</summary>
internal interface IPpmdByteSource
{
    byte ReadByte();
}

/// <summary>
/// PPMd var.H con el codificador de rango "sin acarreo" de Dmitry Subbotin: la variante que usa RAR 2.9–4.x.
/// Port de C/Ppmd7aDec.c del LZMA SDK (Igor Pavlov, dominio público; basado en PPMd var.H de Dmitry Shkarin
/// y en el codificador de Subbotin, ambos de dominio público). El modelo es el mismo <see cref="Ppmd7Model"/>
/// de 7z; solo cambia la decodificación aritmética.
/// </summary>
internal sealed unsafe class Ppmd7aDecoder : IDisposable
{
    public const int SymbolEnd = -1;
    public const int SymbolError = -2;

    private const uint Top = 1u << 24;
    private const uint Bot = 1u << 15;
    private const uint BinScale = 1u << 14;

    private Ppmd7Model? _model;
    private IPpmdByteSource _source = null!;
    private uint _range, _code, _low;

    /// <summary>Hay un modelo creado (RAR puede seguir con el modelo anterior sin reiniciarlo).</summary>
    public bool HasModel => _model != null;

    /// <summary>Crea (o recrea) el modelo con la memoria y el orden indicados.</summary>
    public void StartModel(uint memSize, uint maxOrder)
    {
        if (_model == null || _model.Size != memSize)
        {
            _model?.Dispose();
            _model = null;
            _model = new Ppmd7Model(memSize);
        }

        _model.Init(maxOrder);
    }

    /// <summary>Descarta el modelo (por ejemplo, después de datos dañados).</summary>
    public void Reset()
    {
        _model?.Dispose();
        _model = null;
    }

    public bool InitRangeDecoder(IPpmdByteSource source)
    {
        _source = source;
        _code = 0;
        _range = 0xFFFFFFFF;
        _low = 0;
        for (int i = 0; i < 4; i++)
        {
            _code = (_code << 8) | source.ReadByte();
        }

        return _code < 0xFFFFFFFF;
    }

    private void Normalize()
    {
        while ((_low ^ (_low + _range)) < Top || (_range < Bot && ((_range = (0 - _low) & (Bot - 1)) | 1) != 0))
        {
            _code = (_code << 8) | _source.ReadByte();
            _range <<= 8;
            _low <<= 8;
        }
    }

    private void Decode(uint start, uint size)
    {
        start *= _range;
        _low += start;
        _code -= start;
        _range *= size;
    }

    private uint GetThreshold(uint total) => _code / (_range /= total);

    /// <summary>Decodifica un símbolo: 0–255, <see cref="SymbolEnd"/> o <see cref="SymbolError"/>.</summary>
    public int DecodeSymbol()
    {
        var p = _model ?? throw new InvalidOperationException("PPMd: no hay modelo.");
        byte* charMask = stackalloc byte[256];

        if (p.MinContext->NumStats != 1)
        {
            Ppmd7Model.State* s = p.Stats(p.MinContext);
            uint summFreq = p.MinContext->SummFreq;
            if (summFreq > _range)
            {
                return SymbolError;
            }

            uint count = GetThreshold(summFreq);
            uint hiCnt = count;

            if ((int)(count -= s->Freq) < 0)
            {
                Decode(0, s->Freq);
                Normalize();
                p.FoundState = s;
                byte sym = s->Symbol;
                p.Update1_0();
                return sym;
            }

            p.PrevSuccess = 0;
            uint i = (uint)p.MinContext->NumStats - 1;
            do
            {
                if ((int)(count -= (++s)->Freq) < 0)
                {
                    Decode(hiCnt - count - s->Freq, s->Freq);
                    Normalize();
                    p.FoundState = s;
                    byte sym = s->Symbol;
                    p.Update1();
                    return sym;
                }
            }
            while (--i != 0);

            if (hiCnt >= summFreq)
            {
                return SymbolError;
            }

            hiCnt -= count;
            Decode(hiCnt, summFreq - hiCnt);

            p.HiBitsFlag = Ppmd7Model.HiBitsFlag3(p.FoundState->Symbol);
            new Span<byte>(charMask, 256).Fill(0xFF);
            {
                Ppmd7Model.State* s2 = p.Stats(p.MinContext);
                charMask[s->Symbol] = 0;
                do
                {
                    charMask[s2[0].Symbol] = 0;
                    charMask[s2[1].Symbol] = 0;
                    s2 += 2;
                }
                while (s2 < s);
            }
        }
        else
        {
            Ppmd7Model.State* s = Ppmd7Model.OneState(p.MinContext);
            int probIndex = p.BinSummIndex();
            uint pr = p.BinSumm[probIndex];
            uint size0 = (_range >> 14) * pr;
            pr = (uint)(pr - Ppmd7Model.GetMean(pr));

            if (_code < size0)
            {
                p.BinSumm[probIndex] = (ushort)(pr + (1 << Ppmd7Model.ProbIntBits));
                _range = size0;
                Normalize();

                uint freq = s->Freq;
                Ppmd7Model.Context* c = p.Ctx(Ppmd7Model.GetSuccessor(s));
                byte sym = s->Symbol;
                p.FoundState = s;
                p.PrevSuccess = 1;
                p.RunLength++;
                s->Freq = (byte)(freq + (freq < 128 ? 1u : 0u));
                if (p.OrderFall == 0 && (byte*)c > p.Text)
                {
                    p.MaxContext = p.MinContext = c;
                }
                else
                {
                    p.UpdateModel();
                }

                return sym;
            }

            p.BinSumm[probIndex] = (ushort)pr;
            p.InitEsc = p.ExpEscape[pr >> 10];
            _low += size0;
            _code -= size0;
            _range = (_range & ~(BinScale - 1)) - size0;

            new Span<byte>(charMask, 256).Fill(0xFF);
            charMask[Ppmd7Model.OneState(p.MinContext)->Symbol] = 0;
            p.PrevSuccess = 0;
        }

        while (true)
        {
            Normalize();

            Ppmd7Model.Context* mc = p.MinContext;
            uint numMasked = mc->NumStats;
            do
            {
                p.OrderFall++;
                if (mc->Suffix == 0)
                {
                    return SymbolEnd;
                }

                mc = p.Ctx(mc->Suffix);
            }
            while (mc->NumStats == numMasked);

            Ppmd7Model.State* s = p.Stats(mc);
            uint hiCnt;
            {
                uint num = mc->NumStats;
                uint num2 = num / 2;
                num &= 1;
                hiCnt = (s->Freq & (uint)charMask[s->Symbol]) & (0 - num);
                s += num;
                p.MinContext = mc;
                do
                {
                    hiCnt += s[0].Freq & (uint)charMask[s[0].Symbol];
                    hiCnt += s[1].Freq & (uint)charMask[s[1].Symbol];
                    s += 2;
                }
                while (--num2 != 0);
            }

            int seeIndex = p.MakeEscFreq(numMasked, out uint freqSum);
            freqSum += hiCnt;
            if (freqSum > _range)
            {
                return SymbolError;
            }

            uint count = GetThreshold(freqSum);

            if (count < hiCnt)
            {
                s = p.Stats(p.MinContext);
                hiCnt = count;
                while (true)
                {
                    count -= s->Freq & (uint)charMask[s->Symbol];
                    s++;
                    if ((int)count < 0)
                    {
                        break;
                    }
                }

                s--;
                Decode(hiCnt - count - s->Freq, s->Freq);
                Normalize();
                Ppmd7Model.SeeUpdate(ref p.SeeTable[seeIndex]);
                p.FoundState = s;
                byte sym = s->Symbol;
                p.Update2();
                return sym;
            }

            if (count >= freqSum)
            {
                return SymbolError;
            }

            Decode(hiCnt, freqSum - hiCnt);
            p.SeeTable[seeIndex].Summ = (ushort)(p.SeeTable[seeIndex].Summ + freqSum);

            s = p.Stats(p.MinContext);
            Ppmd7Model.State* end = s + p.MinContext->NumStats;
            do
            {
                charMask[s->Symbol] = 0;
                s++;
            }
            while (s != end);
        }
    }

    public void Dispose()
    {
        _model?.Dispose();
        _model = null;
    }
}
