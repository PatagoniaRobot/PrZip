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
using System.Runtime.InteropServices;

namespace PrZip.Codecs.Ppmd;

/// <summary>
/// Modelo PPMd var.H (Ppmd7). Port de C/Ppmd7.c del LZMA SDK (Igor Pavlov, dominio público; basado en
/// PPMd var.H de Dmitry Shkarin, dominio público).
///
/// Como el original, todo el modelo vive en un único bloque de memoria nativa: los contextos (12 bytes),
/// los vectores de estados (6 bytes por símbolo) y una copia parcial del texto. Las referencias entre
/// registros son desplazamientos de 32 bits desde la base (modo no PPMD_32BIT del SDK).
/// </summary>
internal sealed unsafe class Ppmd7Model : IDisposable
{
    public const int MinOrder = 2;
    public const int MaxOrderLimit = 64;
    public const uint MinMemSize = 1 << 11;
    public const uint MaxMemSize = 0xFFFFFFFF - 12 * 3;

    private const int IntBits = 7;
    private const int PeriodBits = 7;
    private const int BinScale = 1 << (IntBits + PeriodBits);
    private const int MaxFreq = 124;
    private const int UnitSize = 12;
    private const int N1 = 4, N2 = 4, N3 = 4;
    private const int N4 = (128 + 3 - 1 * N1 - 2 * N2 - 3 * N3) / 4;
    private const int NumIndexes = N1 + N2 + N3 + N4;

    private static ReadOnlySpan<byte> ExpEscapeInit => [25, 14, 9, 7, 5, 5, 4, 4, 4, 3, 3, 3, 2, 2, 2, 2];
    private static ReadOnlySpan<ushort> InitBinEsc => [0x3CDD, 0x1F3F, 0x59BF, 0x48F3, 0x64A1, 0x5ABC, 0x6632, 0x6051];

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct State
    {
        public byte Symbol;
        public byte Freq;
        public ushort Successor0;
        public ushort Successor1;
    }

    /// <summary>
    /// Contexto de 12 bytes. Con NumStats == 1 el estado único vive embebido a partir del byte 2
    /// (Symbol, Freq) y su sucesor ocupa el lugar de Stats (bytes 4..7).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 12)]
    public struct Context
    {
        [FieldOffset(0)] public ushort NumStats;
        [FieldOffset(2)] public ushort SummFreq;
        [FieldOffset(4)] public uint Stats;
        [FieldOffset(8)] public uint Suffix;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct Node
    {
        [FieldOffset(0)] public ushort Stamp;
        [FieldOffset(2)] public ushort NU;
        [FieldOffset(4)] public uint Next;
        [FieldOffset(8)] public uint Prev;
    }

    public struct See
    {
        public ushort Summ;
        public byte Shift;
        public byte Count;
    }

    private readonly byte[] _indx2Units = new byte[NumIndexes + 2];
    private readonly byte[] _units2Indx = new byte[128];
    private readonly uint[] _freeList = new uint[NumIndexes];
    public readonly byte[] NS2BSIndx = new byte[256];
    public readonly byte[] NS2Indx = new byte[256];
    public readonly byte[] ExpEscape = new byte[16];
    public readonly See[] SeeTable = new See[25 * 16 + 1]; // el último es DummySee
    public readonly ushort[] BinSumm = new ushort[128 * 64];
    public const int DummySeeIndex = 25 * 16;

    private byte* _baseAlloc;
    private byte* _base;
    private uint _size;

    /// <summary>Memoria del modelo en bytes.</summary>
    public uint Size => _size;
    private uint _alignOffset;

    public Context* MinContext;
    public Context* MaxContext;
    public State* FoundState;
    public uint OrderFall, InitEsc, PrevSuccess, MaxOrder, HiBitsFlag;
    public int RunLength, InitRL;

    private uint _glueCount;
    private byte* _loUnit, _hiUnit, _text, _unitsStart;

    public Ppmd7Model(uint memSize)
    {
        if (memSize < MinMemSize || memSize > MaxMemSize)
        {
            throw new ArgumentOutOfRangeException(nameof(memSize), $"Memoria PPMd fuera de rango: {memSize}.");
        }

        // Ppmd7_Construct
        for (int i = 0, k = 0; i < NumIndexes; i++)
        {
            int step = i >= 12 ? 4 : (i >> 2) + 1;
            do
            {
                _units2Indx[k++] = (byte)i;
            }
            while (--step != 0);

            _indx2Units[i] = (byte)k;
        }

        NS2BSIndx[0] = 0 << 1;
        NS2BSIndx[1] = 1 << 1;
        NS2BSIndx.AsSpan(2, 9).Fill(2 << 1);
        NS2BSIndx.AsSpan(11, 256 - 11).Fill(3 << 1);

        int idx = 0;
        for (; idx < 3; idx++)
        {
            NS2Indx[idx] = (byte)idx;
        }

        for (int m = idx, k = 1; idx < 256; idx++)
        {
            NS2Indx[idx] = (byte)m;
            if (--k == 0)
            {
                k = ++m - 2;
            }
        }

        ExpEscapeInit.CopyTo(ExpEscape);

        // Ppmd7_Alloc
        _alignOffset = (4 - memSize) & 3;
        _baseAlloc = (byte*)NativeMemory.Alloc((nuint)_alignOffset + memSize);
        _base = _baseAlloc;
        _size = memSize;
    }

    public void Dispose()
    {
        if (_baseAlloc != null)
        {
            NativeMemory.Free(_baseAlloc);
            _baseAlloc = null;
        }
    }

    // ---------------- Referencias ----------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Ref(void* ptr) => (uint)((byte*)ptr - _base);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Context* Ctx(uint r) => (Context*)(_base + r);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public State* Stats(Context* c) => (State*)(_base + c->Stats);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static State* OneState(Context* c) => (State*)((byte*)c + 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Context* SuffixOf(Context* c) => (Context*)(_base + c->Suffix);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint GetSuccessor(State* s) => s->Successor0 | ((uint)s->Successor1 << 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetSuccessor(State* s, uint v)
    {
        s->Successor0 = (ushort)v;
        s->Successor1 = (ushort)(v >> 16);
    }

    public byte* Text => _text;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint U2B(uint nu) => nu * UnitSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint U2I(uint nu) => _units2Indx[nu - 1];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint I2U(uint indx) => _indx2Units[indx];

    // ---------------- Memoria ----------------

    private void InsertNode(void* node, uint indx)
    {
        *(uint*)node = _freeList[indx];
        _freeList[indx] = Ref(node);
    }

    private void* RemoveNode(uint indx)
    {
        uint* node = (uint*)(_base + _freeList[indx]);
        _freeList[indx] = *node;
        return node;
    }

    private void SplitBlock(void* ptr, uint oldIndx, uint newIndx)
    {
        uint nu = I2U(oldIndx) - I2U(newIndx);
        ptr = (byte*)ptr + U2B(I2U(newIndx));
        uint i = U2I(nu);
        if (I2U(i) != nu)
        {
            uint k = I2U(--i);
            InsertNode((byte*)ptr + U2B(k), nu - k - 1);
        }

        InsertNode(ptr, i);
    }

    private void GlueFreeBlocks()
    {
        uint head;
        uint n = 0;
        _glueCount = 255;

        if (_loUnit != _hiUnit)
        {
            ((Node*)_loUnit)->Stamp = 1;
        }

        for (uint i = 0; i < NumIndexes; i++)
        {
            ushort nu = _indx2Units[i];
            uint next = _freeList[i];
            _freeList[i] = 0;
            while (next != 0)
            {
                Node* un = (Node*)(_base + next);
                uint tmp = next;
                next = *(uint*)un; // NextRef comparte los primeros 4 bytes
                un->Stamp = 0;
                un->NU = nu;
                un->Next = n;
                n = tmp;
            }
        }

        head = n;
        {
            uint* prev = &head;
            while (n != 0)
            {
                Node* node = (Node*)(_base + n);
                uint nu = node->NU;
                n = node->Next;
                if (nu == 0)
                {
                    *prev = n;
                    continue;
                }

                prev = &node->Next;
                while (true)
                {
                    Node* node2 = node + nu;
                    nu += node2->NU;
                    if (node2->Stamp != 0 || nu >= 0x10000)
                    {
                        break;
                    }

                    node->NU = (ushort)nu;
                    node2->NU = 0;
                }
            }
        }

        for (n = head; n != 0;)
        {
            Node* node = (Node*)(_base + n);
            uint nu = node->NU;
            n = node->Next;
            if (nu == 0)
            {
                continue;
            }

            for (; nu > 128; nu -= 128, node += 128)
            {
                InsertNode(node, NumIndexes - 1);
            }

            uint i = U2I(nu);
            if (I2U(i) != nu)
            {
                uint k = I2U(--i);
                InsertNode(node + k, nu - k - 1);
            }

            InsertNode(node, i);
        }
    }

    private void* AllocUnitsRare(uint indx)
    {
        if (_glueCount == 0)
        {
            GlueFreeBlocks();
            if (_freeList[indx] != 0)
            {
                return RemoveNode(indx);
            }
        }

        uint i = indx;
        do
        {
            if (++i == NumIndexes)
            {
                uint numBytes = U2B(I2U(indx));
                byte* us = _unitsStart;
                _glueCount--;
                return (uint)(us - _text) > numBytes ? (_unitsStart = us - numBytes) : null;
            }
        }
        while (_freeList[i] == 0);

        void* block = RemoveNode(i);
        SplitBlock(block, i, indx);
        return block;
    }

    private void* AllocUnits(uint indx)
    {
        if (_freeList[indx] != 0)
        {
            return RemoveNode(indx);
        }

        uint numBytes = U2B(I2U(indx));
        byte* lo = _loUnit;
        if ((uint)(_hiUnit - lo) >= numBytes)
        {
            _loUnit = lo + numBytes;
            return lo;
        }

        return AllocUnitsRare(indx);
    }

    private static void Copy12(void* dest, void* src, uint num)
    {
        uint* d = (uint*)dest;
        uint* z = (uint*)src;
        do
        {
            d[0] = z[0];
            d[1] = z[1];
            d[2] = z[2];
            z += 3;
            d += 3;
        }
        while (--num != 0);
    }

    // ---------------- Modelo ----------------

    private void RestartModel()
    {
        Array.Clear(_freeList);

        _text = _base + _alignOffset;
        _hiUnit = _text + _size;
        _loUnit = _unitsStart = _hiUnit - _size / 8 / UnitSize * 7 * UnitSize;
        _glueCount = 0;

        OrderFall = MaxOrder;
        RunLength = InitRL = -(int)(MaxOrder < 12 ? MaxOrder : 12) - 1;
        PrevSuccess = 0;

        {
            _hiUnit -= UnitSize;
            Context* mc = (Context*)_hiUnit;
            State* s = (State*)_loUnit;

            _loUnit += U2B(256 / 2);
            MaxContext = MinContext = mc;
            FoundState = s;

            mc->NumStats = 256;
            mc->SummFreq = 256 + 1;
            mc->Stats = Ref(s);
            mc->Suffix = 0;

            for (int i = 0; i < 256; i++, s++)
            {
                s->Symbol = (byte)i;
                s->Freq = 1;
                SetSuccessor(s, 0);
            }
        }

        for (int i = 0; i < 128; i++)
        {
            for (int k = 0; k < 8; k++)
            {
                ushort val = (ushort)(BinScale - InitBinEsc[k] / (i + 2));
                for (int m = 0; m < 64; m += 8)
                {
                    BinSumm[i * 64 + k + m] = val;
                }
            }
        }

        for (int i = 0; i < 25; i++)
        {
            uint summ = (uint)((5 * i + 10) << (PeriodBits - 4));
            for (int k = 0; k < 16; k++)
            {
                ref See s = ref SeeTable[i * 16 + k];
                s.Summ = (ushort)summ;
                s.Shift = PeriodBits - 4;
                s.Count = 4;
            }
        }

        ref See dummy = ref SeeTable[DummySeeIndex];
        dummy.Summ = 0;
        dummy.Shift = PeriodBits;
        dummy.Count = 64;
    }

    public void Init(uint maxOrder)
    {
        MaxOrder = maxOrder;
        RestartModel();
    }

    private Context* CreateSuccessors()
    {
        Context* c = MinContext;
        uint upBranch = GetSuccessor(FoundState);
        byte newSym, newFreq;
        uint numPs = 0;
        State** ps = stackalloc State*[MaxOrderLimit];

        if (OrderFall != 0)
        {
            ps[numPs++] = FoundState;
        }

        while (c->Suffix != 0)
        {
            State* s;
            c = SuffixOf(c);
            if (c->NumStats != 1)
            {
                byte sym = FoundState->Symbol;
                for (s = Stats(c); s->Symbol != sym; s++)
                {
                }
            }
            else
            {
                s = OneState(c);
            }

            uint successor = GetSuccessor(s);
            if (successor != upBranch)
            {
                c = Ctx(successor);
                if (numPs == 0)
                {
                    return c;
                }

                break;
            }

            ps[numPs++] = s;
        }

        newSym = *(_base + upBranch);
        upBranch++;

        if (c->NumStats == 1)
        {
            newFreq = OneState(c)->Freq;
        }
        else
        {
            State* s;
            for (s = Stats(c); s->Symbol != newSym; s++)
            {
            }

            uint cf = (uint)s->Freq - 1;
            uint s0 = (uint)c->SummFreq - c->NumStats - cf;
            newFreq = (byte)(1 + (2 * cf <= s0 ? (5 * cf > s0 ? 1u : 0u) : (2 * cf + s0 - 1) / (2 * s0) + 1));
        }

        do
        {
            Context* c1;
            if (_hiUnit != _loUnit)
            {
                _hiUnit -= UnitSize;
                c1 = (Context*)_hiUnit;
            }
            else if (_freeList[0] != 0)
            {
                c1 = (Context*)RemoveNode(0);
            }
            else
            {
                c1 = (Context*)AllocUnitsRare(0);
                if (c1 == null)
                {
                    return null;
                }
            }

            c1->NumStats = 1;
            OneState(c1)->Symbol = newSym;
            OneState(c1)->Freq = newFreq;
            SetSuccessor(OneState(c1), upBranch);
            c1->Suffix = Ref(c);
            SetSuccessor(ps[--numPs], Ref(c1));
            c = c1;
        }
        while (numPs != 0);

        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwapStates(State* s)
    {
        State tmp = s[0];
        s[0] = s[-1];
        s[-1] = tmp;
    }

    public void UpdateModel()
    {
        uint maxSuccessor, minSuccessor;
        Context* c;
        Context* mc;
        uint s0, ns;

        if (FoundState->Freq < MaxFreq / 4 && MinContext->Suffix != 0)
        {
            c = SuffixOf(MinContext);
            if (c->NumStats == 1)
            {
                State* s = OneState(c);
                if (s->Freq < 32)
                {
                    s->Freq++;
                }
            }
            else
            {
                State* s = Stats(c);
                byte sym = FoundState->Symbol;
                if (s->Symbol != sym)
                {
                    do
                    {
                        s++;
                    }
                    while (s->Symbol != sym);

                    if (s[0].Freq >= s[-1].Freq)
                    {
                        SwapStates(s);
                        s--;
                    }
                }

                if (s->Freq < MaxFreq - 9)
                {
                    s->Freq = (byte)(s->Freq + 2);
                    c->SummFreq = (ushort)(c->SummFreq + 2);
                }
            }
        }

        if (OrderFall == 0)
        {
            MaxContext = MinContext = CreateSuccessors();
            if (MinContext == null)
            {
                RestartModel();
                return;
            }

            SetSuccessor(FoundState, Ref(MinContext));
            return;
        }

        {
            byte* text = _text;
            *text++ = FoundState->Symbol;
            _text = text;
            if (text >= _unitsStart)
            {
                RestartModel();
                return;
            }

            maxSuccessor = Ref(text);
        }

        minSuccessor = GetSuccessor(FoundState);

        if (minSuccessor != 0)
        {
            if (minSuccessor <= maxSuccessor)
            {
                Context* cs = CreateSuccessors();
                if (cs == null)
                {
                    RestartModel();
                    return;
                }

                minSuccessor = Ref(cs);
            }

            if (--OrderFall == 0)
            {
                maxSuccessor = minSuccessor;
                if (MaxContext != MinContext)
                {
                    _text--;
                }
            }
        }
        else
        {
            SetSuccessor(FoundState, maxSuccessor);
            minSuccessor = Ref(MinContext);
        }

        mc = MinContext;
        c = MaxContext;

        MaxContext = MinContext = Ctx(minSuccessor);

        if (c == mc)
        {
            return;
        }

        ns = mc->NumStats;
        s0 = mc->SummFreq - ns - ((uint)FoundState->Freq - 1);

        do
        {
            uint ns1;
            uint sum;

            if ((ns1 = c->NumStats) != 1)
            {
                if ((ns1 & 1) == 0)
                {
                    uint oldNU = ns1 >> 1;
                    uint i = U2I(oldNU);
                    if (i != U2I(oldNU + 1))
                    {
                        void* ptr = AllocUnits(i + 1);
                        if (ptr == null)
                        {
                            RestartModel();
                            return;
                        }

                        void* oldPtr = Stats(c);
                        Copy12(ptr, oldPtr, oldNU);
                        InsertNode(oldPtr, i);
                        c->Stats = Ref(ptr);
                    }
                }

                sum = c->SummFreq;
                sum += (2 * ns1 < ns ? 1u : 0u) + 2 * ((4 * ns1 <= ns ? 1u : 0u) & (sum <= 8 * ns1 ? 1u : 0u));
            }
            else
            {
                State* s = (State*)AllocUnits(0);
                if (s == null)
                {
                    RestartModel();
                    return;
                }

                State* one = OneState(c);
                uint freq = one->Freq;
                s->Symbol = one->Symbol;
                s->Successor0 = one->Successor0;
                s->Successor1 = one->Successor1;
                c->Stats = Ref(s);
                if (freq < MaxFreq / 4 - 1)
                {
                    freq <<= 1;
                }
                else
                {
                    freq = MaxFreq - 4;
                }

                s->Freq = (byte)freq;
                sum = freq + InitEsc + (ns > 3 ? 1u : 0u);
            }

            {
                State* s = Stats(c) + ns1;
                uint cf = 2 * (sum + 6) * FoundState->Freq;
                uint sf = s0 + sum;
                s->Symbol = FoundState->Symbol;
                c->NumStats = (ushort)(ns1 + 1);
                SetSuccessor(s, maxSuccessor);
                if (cf < 6 * sf)
                {
                    cf = 1 + (cf > sf ? 1u : 0u) + (cf >= 4 * sf ? 1u : 0u);
                    sum += 3;
                }
                else
                {
                    cf = 4 + (cf >= 9 * sf ? 1u : 0u) + (cf >= 12 * sf ? 1u : 0u) + (cf >= 15 * sf ? 1u : 0u);
                    sum += cf;
                }

                c->SummFreq = (ushort)sum;
                s->Freq = (byte)cf;
            }

            c = SuffixOf(c);
        }
        while (c != mc);
    }

    private void Rescale()
    {
        uint i, adder, sumFreq, escFreq;
        State* stats = Stats(MinContext);
        State* s = FoundState;

        if (s != stats)
        {
            State tmp = *s;
            do
            {
                s[0] = s[-1];
            }
            while (--s != stats);

            *s = tmp;
        }

        sumFreq = s->Freq;
        escFreq = MinContext->SummFreq - sumFreq;
        adder = OrderFall != 0 ? 1u : 0u;
        sumFreq = (sumFreq + 4 + adder) >> 1;
        i = (uint)MinContext->NumStats - 1;
        s->Freq = (byte)sumFreq;

        do
        {
            uint freq = (++s)->Freq;
            escFreq -= freq;
            freq = (freq + adder) >> 1;
            sumFreq += freq;
            s->Freq = (byte)freq;
            if (freq > s[-1].Freq)
            {
                State tmp = *s;
                State* s1 = s;
                do
                {
                    s1[0] = s1[-1];
                }
                while (--s1 != stats && freq > s1[-1].Freq);

                *s1 = tmp;
            }
        }
        while (--i != 0);

        if (s->Freq == 0)
        {
            uint numStats, numStatsNew, n0, n1;
            i = 0;
            do
            {
                i++;
            }
            while ((--s)->Freq == 0);

            escFreq += i;
            Context* mc = MinContext;
            numStats = mc->NumStats;
            numStatsNew = numStats - i;
            mc->NumStats = (ushort)numStatsNew;
            n0 = (numStats + 1) >> 1;

            if (numStatsNew == 1)
            {
                uint freq = stats->Freq;
                do
                {
                    escFreq >>= 1;
                    freq = (freq + 1) >> 1;
                }
                while (escFreq > 1);

                s = OneState(mc);
                *s = *stats;
                s->Freq = (byte)freq;
                FoundState = s;
                InsertNode(stats, U2I(n0));
                return;
            }

            n1 = (numStatsNew + 1) >> 1;
            if (n0 != n1)
            {
                uint i0 = U2I(n0);
                uint i1 = U2I(n1);
                if (i0 != i1)
                {
                    if (_freeList[i1] != 0)
                    {
                        void* ptr = RemoveNode(i1);
                        MinContext->Stats = Ref(ptr);
                        Copy12(ptr, stats, n1);
                        InsertNode(stats, i0);
                    }
                    else
                    {
                        SplitBlock(stats, i0, i1);
                    }
                }
            }
        }

        {
            Context* mc = MinContext;
            mc->SummFreq = (ushort)(sumFreq + escFreq - (escFreq >> 1));
            FoundState = Stats(mc);
        }
    }

    /// <summary>Ppmd7_MakeEscFreq: devuelve el índice del SEE en SeeTable y la frecuencia de escape.</summary>
    public int MakeEscFreq(uint numMasked, out uint escFreq)
    {
        Context* mc = MinContext;
        uint numStats = mc->NumStats;
        if (numStats != 256)
        {
            uint nonMasked = numStats - numMasked;
            int see = NS2Indx[nonMasked - 1] * 16
                + (nonMasked < (uint)SuffixOf(mc)->NumStats - numStats ? 1 : 0)
                + 2 * (mc->SummFreq < 11 * numStats ? 1 : 0)
                + 4 * (numMasked > nonMasked ? 1 : 0)
                + (int)HiBitsFlag;
            ref See s = ref SeeTable[see];
            uint summ = s.Summ;
            uint r = summ >> s.Shift;
            s.Summ = (ushort)(summ - r);
            escFreq = r + (r == 0 ? 1u : 0u);
            return see;
        }

        escFreq = 1;
        return DummySeeIndex;
    }

    public static void SeeUpdate(ref See s)
    {
        if (s.Shift < PeriodBits && --s.Count == 0)
        {
            s.Summ = (ushort)(s.Summ << 1);
            s.Count = (byte)(3 << s.Shift++);
        }
    }

    private void NextContext()
    {
        Context* c = Ctx(GetSuccessor(FoundState));
        if (OrderFall == 0 && (byte*)c > _text)
        {
            MaxContext = MinContext = c;
        }
        else
        {
            UpdateModel();
        }
    }

    public void Update1()
    {
        State* s = FoundState;
        uint freq = s->Freq;
        freq += 4;
        MinContext->SummFreq = (ushort)(MinContext->SummFreq + 4);
        s->Freq = (byte)freq;
        if (freq > s[-1].Freq)
        {
            SwapStates(s);
            FoundState = --s;
            if (freq > MaxFreq)
            {
                Rescale();
            }
        }

        NextContext();
    }

    public void Update1_0()
    {
        State* s = FoundState;
        Context* mc = MinContext;
        uint freq = s->Freq;
        uint summFreq = mc->SummFreq;
        PrevSuccess = 2 * freq > summFreq ? 1u : 0u;
        RunLength += (int)PrevSuccess;
        mc->SummFreq = (ushort)(summFreq + 4);
        freq += 4;
        s->Freq = (byte)freq;
        if (freq > MaxFreq)
        {
            Rescale();
        }

        NextContext();
    }

    public void Update2()
    {
        State* s = FoundState;
        uint freq = s->Freq;
        freq += 4;
        RunLength = InitRL;
        MinContext->SummFreq = (ushort)(MinContext->SummFreq + 4);
        s->Freq = (byte)freq;
        if (freq > MaxFreq)
        {
            Rescale();
        }

        UpdateModel();
    }

    /// <summary>Índice en BinSumm para el contexto binario actual (Ppmd7_GetBinSumm).</summary>
    public int BinSummIndex()
    {
        State* one = OneState(MinContext);
        HiBitsFlag = HiBitsFlag3(FoundState->Symbol);
        return (one->Freq - 1) * 64
            + (int)PrevSuccess
            + ((RunLength >> 26) & 0x20)
            + NS2BSIndx[SuffixOf(MinContext)->NumStats - 1]
            + (int)HiBitsFlag4(one->Symbol)
            + (int)HiBitsFlag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HiBitsFlag3(uint sym) => ((sym + 0xC0) >> (8 - 3)) & (1 << 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HiBitsFlag4(uint sym) => ((sym + 0xC0) >> (8 - 4)) & (1 << 4);

    public static int GetMean(uint prob) => (int)((prob + (1 << (PeriodBits - 2))) >> PeriodBits);

    public const int ProbIntBits = IntBits;
}
