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

/// <summary>
/// Codificador PPMd var.H con el codificador de rango de 7z. Port de C/Ppmd7Enc.c del LZMA SDK
/// (Igor Pavlov, dominio público). Se usa como stream de escritura; Finish vacía el codificador de rango.
/// No escribe marca de fin: en 7z el tamaño descomprimido va en la cabecera.
/// </summary>
internal sealed unsafe class Ppmd7EncoderStream : Stream
{
    private const uint TopValue = 1 << 24;

    private static ReadOnlySpan<byte> OrdersByLevel => [3, 4, 4, 5, 5, 6, 8, 16, 24, 32];

    private readonly Ppmd7Model _model;
    private readonly Stream _output;
    private ulong _low;
    private uint _range = 0xFFFFFFFF;
    private byte _cache;
    private ulong _cacheSize = 1;
    private bool _finished;

    public int Order { get; }
    public uint MemorySize { get; }

    /// <summary>
    /// Parámetros por nivel. El orden es el que escribe 7z.exe 24.08 en las propiedades para -mx=1..9
    /// (docs/FORMAT.md §6, BlackBoxFormatTests). La memoria, 1 &lt;&lt; (19 + nivel), es elección propia;
    /// coincide con lo observado en 7z.exe hasta el nivel 5 con un archivo chico.
    /// </summary>
    public static (int Order, uint MemorySize) DefaultsForLevel(int level)
    {
        level = Math.Clamp(level, 1, 9);
        return (OrdersByLevel[level], 1u << (19 + level));
    }

    public Ppmd7EncoderStream(Stream output, int order, uint memorySize)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (order < Ppmd7Model.MinOrder || order > Ppmd7Model.MaxOrderLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        _output = output;
        Order = order;
        MemorySize = memorySize;
        _model = new Ppmd7Model(memorySize);
        _model.Init((uint)order);
    }

    /// <summary>Propiedades del coder para la cabecera 7z: orden y memoria (UInt32 LE).</summary>
    public byte[] GetProperties()
    {
        byte[] props = new byte[5];
        props[0] = (byte)Order;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(props.AsSpan(1), MemorySize);
        return props;
    }

    private void ShiftLow()
    {
        if ((uint)_low < 0xFF000000u || (uint)(_low >> 32) != 0)
        {
            byte temp = _cache;
            do
            {
                _output.WriteByte((byte)(temp + (byte)(_low >> 32)));
                temp = 0xFF;
            }
            while (--_cacheSize != 0);

            _cache = (byte)((uint)_low >> 24);
        }

        _cacheSize++;
        _low = (uint)_low << 8;
    }

    private void Normalize()
    {
        if (_range < TopValue)
        {
            _range <<= 8;
            ShiftLow();
            if (_range < TopValue)
            {
                _range <<= 8;
                ShiftLow();
            }
        }
    }

    private void Normalize1()
    {
        if (_range < TopValue)
        {
            _range <<= 8;
            ShiftLow();
        }
    }

    private void RcEncode(uint start, uint size)
    {
        _low += start * _range;
        _range *= size;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        foreach (byte b in buffer)
        {
            EncodeSymbol(b);
        }
    }

    public override void WriteByte(byte value)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        EncodeSymbol(value);
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        for (int i = 0; i < 5; i++)
        {
            ShiftLow();
        }

        _finished = true;
    }

    private void EncodeSymbol(int symbol)
    {
        Ppmd7Model p = _model;
        byte* charMask = stackalloc byte[256];

        if (p.MinContext->NumStats != 1)
        {
            Ppmd7Model.State* s = p.Stats(p.MinContext);
            _range /= p.MinContext->SummFreq;
            if (s->Symbol == symbol)
            {
                RcEncode(0, s->Freq);
                Normalize();
                p.FoundState = s;
                p.Update1_0();
                return;
            }

            p.PrevSuccess = 0;
            uint sum = s->Freq;
            uint i = (uint)p.MinContext->NumStats - 1;
            do
            {
                if ((++s)->Symbol == symbol)
                {
                    RcEncode(sum, s->Freq);
                    Normalize();
                    p.FoundState = s;
                    p.Update1();
                    return;
                }

                sum += s->Freq;
            }
            while (--i != 0);

            RcEncode(sum, p.MinContext->SummFreq - sum);

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
            int probIndex = p.BinSummIndex();
            Ppmd7Model.State* s = Ppmd7Model.OneState(p.MinContext);
            uint pr = p.BinSumm[probIndex];
            uint bound = (_range >> 14) * pr;
            pr = (uint)(pr - Ppmd7Model.GetMean(pr));
            if (s->Symbol == symbol)
            {
                p.BinSumm[probIndex] = (ushort)(pr + (1 << Ppmd7Model.ProbIntBits));
                _range = bound;
                Normalize1();

                uint freq = s->Freq;
                Ppmd7Model.Context* c = p.Ctx(Ppmd7Model.GetSuccessor(s));
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

                return;
            }

            p.BinSumm[probIndex] = (ushort)pr;
            p.InitEsc = p.ExpEscape[pr >> 10];
            _low += bound;
            _range -= bound;

            new Span<byte>(charMask, 256).Fill(0xFF);
            charMask[s->Symbol] = 0;
            p.PrevSuccess = 0;
        }

        while (true)
        {
            Normalize();

            Ppmd7Model.Context* mc = p.MinContext;
            uint numMasked = mc->NumStats;
            uint i;
            do
            {
                p.OrderFall++;
                if (mc->Suffix == 0)
                {
                    return; // marca de fin (símbolo -1): no se usa en 7z
                }

                mc = p.Ctx(mc->Suffix);
                i = mc->NumStats;
            }
            while (i == numMasked);

            p.MinContext = mc;
            int seeIndex = p.MakeEscFreq(numMasked, out uint escFreq);

            Ppmd7Model.State* s = p.Stats(mc);
            uint sum = 0;
            do
            {
                uint cur = s->Symbol;
                if ((int)cur == symbol)
                {
                    uint low = sum;
                    uint freq = s->Freq;
                    Ppmd7Model.SeeUpdate(ref p.SeeTable[seeIndex]);
                    p.FoundState = s;
                    sum += escFreq;
                    uint num2 = i / 2;
                    i &= 1;
                    sum += freq & (0 - i);
                    if (num2 != 0)
                    {
                        s += i;
                        do
                        {
                            sum += s[0].Freq & (uint)charMask[s[0].Symbol];
                            sum += s[1].Freq & (uint)charMask[s[1].Symbol];
                            s += 2;
                        }
                        while (--num2 != 0);
                    }

                    _range /= sum;
                    RcEncode(low, freq);
                    Normalize();
                    p.Update2();
                    return;
                }

                sum += s->Freq & (uint)charMask[cur];
                s++;
            }
            while (--i != 0);

            {
                uint total = sum + escFreq;
                p.SeeTable[seeIndex].Summ = (ushort)(p.SeeTable[seeIndex].Summ + total);
                _range /= total;
                RcEncode(sum, escFreq);
            }

            {
                Ppmd7Model.State* s2 = p.Stats(p.MinContext);
                s--;
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
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _model.Dispose();
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
