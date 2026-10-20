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

namespace PrZip.Codecs.Ppmd;

/// <summary>
/// Decodificador PPMd var.H con el codificador de rango de 7z (método 03 04 01 en .7z).
/// Port de C/Ppmd7Dec.c del LZMA SDK (Igor Pavlov, dominio público). El modelo está en <see cref="Ppmd7Model"/>.
///
/// Propiedades del coder en 7z: 5 bytes, orden (1 byte) y tamaño de memoria (UInt32 little-endian).
/// </summary>
internal static unsafe class Ppmd7Decoder
{
    private const uint TopValue = 1 << 24;

    public static (int Order, uint MemSize) ParseProperties(ReadOnlySpan<byte> props)
    {
        if (props.Length < 5)
        {
            throw new CorruptArchiveException("Faltan las propiedades de 5 bytes requeridas por PPMd.");
        }

        int order = props[0];
        uint mem = BinaryPrimitives.ReadUInt32LittleEndian(props[1..]);
        if (order < Ppmd7Model.MinOrder || order > Ppmd7Model.MaxOrderLimit
            || mem < Ppmd7Model.MinMemSize || mem > Ppmd7Model.MaxMemSize)
        {
            throw new CorruptArchiveException($"Propiedades PPMd inválidas: orden {order}, memoria {mem}.");
        }

        return (order, mem);
    }

    /// <summary>
    /// Decodifica exactamente unpackSize bytes. Un error de datos o un fin anticipado lanzan excepción.
    /// </summary>
    public static void Decode(Stream inStream, Stream outStream, long unpackSize, int order, uint memSize)
    {
        ArgumentNullException.ThrowIfNull(inStream);
        ArgumentNullException.ThrowIfNull(outStream);

        using var model = new Ppmd7Model(memSize);
        model.Init((uint)order);

        var rc = new RangeDecoder(inStream);
        if (!rc.Init())
        {
            throw new CorruptArchiveException("Flujo PPMd inválido: cabecera del codificador de rango incorrecta.");
        }

        byte[] buffer = new byte[1 << 16];
        int count = 0;
        long written = 0;
        while (unpackSize < 0 || written < unpackSize)
        {
            int sym = DecodeSymbol(model, ref rc);
            if (sym < 0)
            {
                if (sym == -1 && unpackSize < 0)
                {
                    break; // marcador de fin, solo válido con tamaño desconocido
                }

                throw new CorruptArchiveException(
                    sym == -1 ? $"PPMd: marca de fin a los {written} bytes de {unpackSize} esperados." : "PPMd: datos inválidos.");
            }

            buffer[count++] = (byte)sym;
            written++;
            if (count == buffer.Length)
            {
                outStream.Write(buffer, 0, count);
                count = 0;
            }
        }

        if (count > 0)
        {
            outStream.Write(buffer, 0, count);
        }
    }

    private struct RangeDecoder(Stream stream)
    {
        public uint Range;
        public uint Code;
        private readonly Stream _stream = stream;

        public bool Init()
        {
            Code = 0;
            Range = 0xFFFFFFFF;
            if (ReadByte() != 0)
            {
                return false;
            }

            for (int i = 0; i < 4; i++)
            {
                Code = (Code << 8) | ReadByte();
            }

            return Code < 0xFFFFFFFF;
        }

        public readonly uint ReadByte()
        {
            int b = _stream.ReadByte();
            if (b < 0)
            {
                throw new ArchiveTruncatedException("Fin inesperado del flujo PPMd.");
            }

            return (uint)b;
        }

        public void Decode(uint start, uint size)
        {
            Code -= start * Range;
            Range *= size;
        }

        public void Normalize()
        {
            if (Range < TopValue)
            {
                Code = (Code << 8) | ReadByte();
                Range <<= 8;
                if (Range < TopValue)
                {
                    Code = (Code << 8) | ReadByte();
                    Range <<= 8;
                }
            }
        }

        public void Normalize1()
        {
            if (Range < TopValue)
            {
                Code = (Code << 8) | ReadByte();
                Range <<= 8;
            }
        }

        public uint GetThreshold(uint total) => Code / (Range /= total);
    }

    private static int DecodeSymbol(Ppmd7Model p, ref RangeDecoder rc)
    {
        byte* charMask = stackalloc byte[256];

        if (p.MinContext->NumStats != 1)
        {
            Ppmd7Model.State* s = p.Stats(p.MinContext);
            uint summFreq = p.MinContext->SummFreq;
            uint count = rc.GetThreshold(summFreq);
            uint hiCnt = count;

            if ((int)(count -= s->Freq) < 0)
            {
                rc.Decode(0, s->Freq);
                rc.Normalize();
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
                    rc.Decode(hiCnt - count - s->Freq, s->Freq);
                    rc.Normalize();
                    p.FoundState = s;
                    byte sym = s->Symbol;
                    p.Update1();
                    return sym;
                }
            }
            while (--i != 0);

            if (hiCnt >= summFreq)
            {
                return -2;
            }

            hiCnt -= count;
            rc.Decode(hiCnt, summFreq - hiCnt);

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
            uint size0 = (rc.Range >> 14) * pr;
            pr = (uint)(pr - Ppmd7Model.GetMean(pr));

            if (rc.Code < size0)
            {
                p.BinSumm[probIndex] = (ushort)(pr + (1 << Ppmd7Model.ProbIntBits));
                rc.Range = size0;
                rc.Normalize1();

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
            rc.Code -= size0;
            rc.Range -= size0;

            new Span<byte>(charMask, 256).Fill(0xFF);
            charMask[Ppmd7Model.OneState(p.MinContext)->Symbol] = 0;
            p.PrevSuccess = 0;
        }

        while (true)
        {
            rc.Normalize();

            Ppmd7Model.Context* mc = p.MinContext;
            uint numMasked = mc->NumStats;
            do
            {
                p.OrderFall++;
                if (mc->Suffix == 0)
                {
                    return -1;
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
            uint count = rc.GetThreshold(freqSum);

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
                rc.Decode(hiCnt - count - s->Freq, s->Freq);
                rc.Normalize();
                Ppmd7Model.SeeUpdate(ref p.SeeTable[seeIndex]);
                p.FoundState = s;
                byte sym = s->Symbol;
                p.Update2();
                return sym;
            }

            if (count >= freqSum)
            {
                return -2;
            }

            rc.Decode(hiCnt, freqSum - hiCnt);
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
}
