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

using System.Buffers;
using PrZip.Codecs.Huffman;

namespace PrZip.Codecs.BZip2;

/// <summary>
/// Compresor BZip2 propio, escrito desde el formato. Cada bloque (hasta nivel × 100 000 bytes después de la
/// RLE inicial) se transforma con BWT (orden de rotaciones de Larsson–Sadakane), MTF y corridas de
/// ceros RUNA/RUNB, y se codifica con 2 a 6 tablas de Huffman elegidas cada 50 símbolos y refinadas en
/// cuatro pasadas. Los bloques son independientes: con varios hilos se comprimen a la vez, en una fila
/// continua mientras se sigue leyendo la entrada, y se escriben en orden, así que la salida es la misma para
/// cualquier cantidad de hilos.
/// </summary>
internal sealed class BZip2EncoderStream : Stream
{
    private const int GroupSize = 50;
    private const int MaxCodeLength = 17;
    private const int Iterations = 4;
    private const int RunA = 0;
    private const int RunB = 1;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private readonly Stream _output;
    private readonly int _blockSize100k;
    private readonly int _capacity;
    private readonly int _threads;
    private readonly BitBuffer _out = new();

    private byte[] _block;
    private int _blockLength;
    private uint _blockCrc = 0xFFFFFFFF;
    private int _runChar = -1;
    private int _runLength;
    private uint _combinedCrc;
    private readonly Queue<(Task<BitBuffer> Work, uint Crc, byte[] Block)> _inFlight = new();
    private readonly Stack<byte[]> _freeBlocks = new();
    private bool _finished;

    /// <param name="output">Destino del stream BZip2.</param>
    /// <param name="blockSize100k">Tamaño de bloque, 1 a 9 (× 100 000 bytes).</param>
    /// <param name="threads">Bloques que se comprimen a la vez; 0 = todos los núcleos.</param>
    public BZip2EncoderStream(Stream output, int blockSize100k = 9, int threads = 1)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _blockSize100k = Math.Clamp(blockSize100k, 1, 9);
        _capacity = _blockSize100k * 100000 - 19;
        _threads = threads <= 0 ? Environment.ProcessorCount : threads;
        _block = new byte[_capacity];

        _out.WriteBits('B', 8);
        _out.WriteBits('Z', 8);
        _out.WriteBits('h', 8);
        _out.WriteBits((uint)('0' + _blockSize100k), 8);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        foreach (byte b in buffer)
        {
            if (b == _runChar && _runLength < 255)
            {
                _runLength++;
            }
            else
            {
                FlushRun();
                _runChar = b;
                _runLength = 1;
            }
        }
    }

    /// <summary>Cierra el último bloque y escribe el fin de stream con el CRC combinado.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        FlushRun();
        CloseBlock();
        while (_inFlight.Count > 0)
        {
            WriteOldest();
        }

        _out.WriteBits(0x177245, 24);
        _out.WriteBits(0x385090, 24);
        _out.WriteBits(_combinedCrc, 32);
        _out.FlushTo(_output, final: true);
        _finished = true;
    }

    /// <summary>
    /// Vuelca la corrida pendiente con la RLE inicial de bzip2: 1 a 3 bytes tal cual, o 4 bytes más uno con
    /// las repeticiones restantes. Una corrida nunca se parte entre dos bloques: cada bloque se decodifica solo.
    /// </summary>
    private void FlushRun()
    {
        if (_runLength == 0)
        {
            return;
        }

        int encoded = _runLength >= 4 ? 5 : _runLength;
        if (_blockLength + encoded > _capacity)
        {
            CloseBlock();
        }

        byte c = (byte)_runChar;
        for (int i = 0; i < _runLength; i++)
        {
            _blockCrc = (_blockCrc << 8) ^ CrcTable[(_blockCrc >> 24) ^ c];
        }

        int literal = Math.Min(_runLength, 4);
        for (int i = 0; i < literal; i++)
        {
            _block[_blockLength++] = c;
        }

        if (_runLength >= 4)
        {
            _block[_blockLength++] = (byte)(_runLength - 4);
        }

        _runLength = 0;
        _runChar = -1;
    }

    private void CloseBlock()
    {
        if (_blockLength == 0)
        {
            return;
        }

        byte[] block = _block;
        int length = _blockLength;
        uint crc = ~_blockCrc;
        _blockLength = 0;
        _blockCrc = 0xFFFFFFFF;

        if (_threads == 1)
        {
            Commit(CompressBlock(block, length, crc), crc);
            return;
        }

        // Fila continua: mientras los hilos comprimen, este sigue llenando el próximo bloque.
        _inFlight.Enqueue((Task.Run(() => CompressBlock(block, length, crc)), crc, block));
        _block = _freeBlocks.Count > 0 ? _freeBlocks.Pop() : new byte[_capacity];
        if (_inFlight.Count >= _threads)
        {
            WriteOldest();
        }
    }

    private void WriteOldest()
    {
        var (work, crc, block) = _inFlight.Dequeue();
        Commit(work.GetAwaiter().GetResult(), crc);
        _freeBlocks.Push(block);
    }

    private void Commit(BitBuffer compressed, uint crc)
    {
        _combinedCrc = ((_combinedCrc << 1) | (_combinedCrc >> 31)) ^ crc;
        _out.Append(compressed);
        _out.FlushTo(_output, final: false);
    }

    /// <summary>Un bloque completo, desde su marca de 48 bits hasta el último símbolo.</summary>
    internal static BitBuffer CompressBlock(byte[] block, int n, uint blockCrc)
    {
        var bits = new BitBuffer();
        int[] order = ArrayPool<int>.Shared.Rent(n);
        int[] rank = ArrayPool<int>.Shared.Rent(n);
        int[] tmp = ArrayPool<int>.Shared.Rent(n);
        byte[] last = ArrayPool<byte>.Shared.Rent(n);
        ushort[] mtfv = ArrayPool<ushort>.Shared.Rent(n + 1);
        try
        {
            SortRotations(block, n, order, rank, tmp);
            WriteBlock(bits, block, n, blockCrc, order, last, mtfv);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(order);
            ArrayPool<int>.Shared.Return(rank);
            ArrayPool<int>.Shared.Return(tmp);
            ArrayPool<byte>.Shared.Return(last);
            ArrayPool<ushort>.Shared.Return(mtfv);
        }

        return bits;
    }

    private static void WriteBlock(BitBuffer bits, byte[] block, int n, uint blockCrc, int[] order, byte[] last, ushort[] mtfv)
    {

        // Columna final de la matriz de rotaciones y fila de la rotación original.
        int origPtr = 0;
        for (int i = 0; i < n; i++)
        {
            int start = order[i];
            if (start == 0)
            {
                origPtr = i;
            }

            last[i] = block[start == 0 ? n - 1 : start - 1];
        }

        Span<bool> inUse = stackalloc bool[256];
        inUse.Clear();
        for (int i = 0; i < n; i++)
        {
            inUse[last[i]] = true;
        }

        Span<byte> unseqToSeq = stackalloc byte[256];
        int nInUse = 0;
        for (int i = 0; i < 256; i++)
        {
            if (inUse[i])
            {
                unseqToSeq[i] = (byte)nInUse++;
            }
        }

        int alphaSize = nInUse + 2;
        int endOfBlock = nInUse + 1;
        var freq = new int[alphaSize];
        int nMtf = GenerateMtfValues(last.AsSpan(0, n), unseqToSeq, nInUse, mtfv, freq);

        int nGroups = nMtf < 200 ? 2 : nMtf < 600 ? 3 : nMtf < 1200 ? 4 : nMtf < 2400 ? 5 : 6;
        int nSelectors = (nMtf + GroupSize - 1) / GroupSize;
        var lengths = new byte[nGroups][];
        for (int t = 0; t < nGroups; t++)
        {
            lengths[t] = new byte[alphaSize];
        }

        // Tablas iniciales: el alfabeto repartido en tramos de frecuencia parecida, uno por tabla.
        int remaining = nMtf;
        int gs = 0;
        for (int part = nGroups; part > 0; part--)
        {
            int target = remaining / part;
            int ge = gs - 1;
            int acc = 0;
            while (acc < target && ge < alphaSize - 1)
            {
                ge++;
                acc += freq[ge];
            }

            if (ge > gs && part != nGroups && part != 1 && ((nGroups - part) % 2 == 1))
            {
                acc -= freq[ge];
                ge--;
            }

            for (int v = 0; v < alphaSize; v++)
            {
                lengths[part - 1][v] = (byte)(v >= gs && v <= ge ? 0 : 15);
            }

            gs = ge + 1;
            remaining -= acc;
        }

        var selectors = new byte[nSelectors];
        var tableFreq = new int[nGroups][];
        for (int t = 0; t < nGroups; t++)
        {
            tableFreq[t] = new int[alphaSize];
        }

        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            for (int t = 0; t < nGroups; t++)
            {
                Array.Clear(tableFreq[t]);
            }

            for (int s = 0, start = 0; start < nMtf; s++, start += GroupSize)
            {
                int end = Math.Min(start + GroupSize, nMtf);
                int best = 0;
                long bestCost = long.MaxValue;
                for (int t = 0; t < nGroups; t++)
                {
                    long cost = 0;
                    byte[] len = lengths[t];
                    for (int i = start; i < end; i++)
                    {
                        cost += len[mtfv[i]];
                    }

                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = t;
                    }
                }

                selectors[s] = (byte)best;
                int[] f = tableFreq[best];
                for (int i = start; i < end; i++)
                {
                    f[mtfv[i]]++;
                }
            }

            // Cada tabla se rehace con lo que le tocó; todos los símbolos llevan código (peso mínimo 1).
            var weights = new int[alphaSize];
            for (int t = 0; t < nGroups; t++)
            {
                for (int v = 0; v < alphaSize; v++)
                {
                    weights[v] = Math.Max(tableFreq[t][v], 1);
                }

                HuffmanCodeBuilder.BuildLengths(weights, MaxCodeLength, lengths[t]);
            }
        }

        var codes = new int[nGroups][];
        for (int t = 0; t < nGroups; t++)
        {
            codes[t] = new int[alphaSize];
            HuffmanCodeBuilder.BuildCanonicalCodes(lengths[t], codes[t], MaxCodeLength);
        }

        // Cabecera del bloque.
        bits.WriteBits(0x314159, 24);
        bits.WriteBits(0x265359, 24);
        bits.WriteBits(blockCrc, 32);
        bits.WriteBits(0, 1); // no aleatorizado
        bits.WriteBits((uint)origPtr, 24);

        // Mapa de bytes usados: 16 grupos de 16.
        uint used16 = 0;
        for (int g = 0; g < 16; g++)
        {
            for (int j = 0; j < 16; j++)
            {
                if (inUse[g * 16 + j])
                {
                    used16 |= 1u << (15 - g);
                    break;
                }
            }
        }

        bits.WriteBits(used16, 16);
        for (int g = 0; g < 16; g++)
        {
            if ((used16 & (1u << (15 - g))) == 0)
            {
                continue;
            }

            uint group = 0;
            for (int j = 0; j < 16; j++)
            {
                if (inUse[g * 16 + j])
                {
                    group |= 1u << (15 - j);
                }
            }

            bits.WriteBits(group, 16);
        }

        bits.WriteBits((uint)nGroups, 3);
        bits.WriteBits((uint)nSelectors, 15);

        // Selectores con MTF y en unario.
        Span<byte> position = stackalloc byte[nGroups];
        for (int t = 0; t < nGroups; t++)
        {
            position[t] = (byte)t;
        }

        foreach (byte selector in selectors)
        {
            int j = 0;
            while (position[j] != selector)
            {
                j++;
            }

            for (int k = j; k > 0; k--)
            {
                position[k] = position[k - 1];
            }

            position[0] = selector;
            for (int k = 0; k < j; k++)
            {
                bits.WriteBits(1, 1);
            }

            bits.WriteBits(0, 1);
        }

        // Largos de cada tabla, en deltas.
        for (int t = 0; t < nGroups; t++)
        {
            int current = lengths[t][0];
            bits.WriteBits((uint)current, 5);
            for (int v = 0; v < alphaSize; v++)
            {
                int target = lengths[t][v];
                while (current < target)
                {
                    bits.WriteBits(2, 2);
                    current++;
                }

                while (current > target)
                {
                    bits.WriteBits(3, 2);
                    current--;
                }

                bits.WriteBits(0, 1);
            }
        }

        // Los símbolos, cada grupo con su tabla.
        for (int s = 0, start = 0; start < nMtf; s++, start += GroupSize)
        {
            int end = Math.Min(start + GroupSize, nMtf);
            int[] code = codes[selectors[s]];
            byte[] len = lengths[selectors[s]];
            for (int i = start; i < end; i++)
            {
                int v = mtfv[i];
                bits.WriteBits((uint)code[v], len[v]);
            }
        }

        _ = endOfBlock;
    }

    /// <summary>MTF sobre los símbolos usados, con las corridas de ceros en RUNA/RUNB y el fin de bloque.</summary>
    private static int GenerateMtfValues(ReadOnlySpan<byte> last, ReadOnlySpan<byte> unseqToSeq, int nInUse, ushort[] mtfv, int[] freq)
    {
        Span<byte> list = stackalloc byte[256];
        for (int i = 0; i < nInUse; i++)
        {
            list[i] = (byte)i;
        }

        int written = 0;
        int zeroRun = 0;
        foreach (byte b in last)
        {
            byte symbol = unseqToSeq[b];
            if (list[0] == symbol)
            {
                zeroRun++;
                continue;
            }

            if (zeroRun > 0)
            {
                written = EmitZeroRun(zeroRun, mtfv, freq, written);
                zeroRun = 0;
            }

            int j = 1;
            byte previous = list[0];
            while (list[j] != symbol)
            {
                (list[j], previous) = (previous, list[j]);
                j++;
            }

            list[j] = previous;
            list[0] = symbol;
            mtfv[written++] = (ushort)(j + 1);
            freq[j + 1]++;
        }

        if (zeroRun > 0)
        {
            written = EmitZeroRun(zeroRun, mtfv, freq, written);
        }

        mtfv[written++] = (ushort)(nInUse + 1);
        freq[nInUse + 1]++;
        return written;
    }

    /// <summary>Una corrida de ceros en base 2 biyectiva: RUNA vale 1 y RUNB 2 en cada posición.</summary>
    private static int EmitZeroRun(int run, ushort[] mtfv, int[] freq, int written)
    {
        run--;
        while (true)
        {
            int symbol = (run & 1) != 0 ? RunB : RunA;
            mtfv[written++] = (ushort)symbol;
            freq[symbol]++;
            if (run < 2)
            {
                return written;
            }

            run = (run - 2) / 2;
        }
    }

    /// <summary>
    /// Orden de las n rotaciones del bloque (Larsson–Sadakane, ver <see cref="SortByLarssonSadakane"/>).
    /// Rotaciones idénticas, que solo aparecen en bloques periódicos, dan la misma salida en cualquier orden.
    /// </summary>
    public static int[] SortRotations(byte[] block, int n)
    {
        ArgumentNullException.ThrowIfNull(block);
        var sa = new int[n];
        SortRotations(block, n, sa, new int[n], new int[n]);
        return sa;
    }

    /// <summary>Igual, sobre arreglos de trabajo provistos de al menos n elementos.</summary>
    private static void SortRotations(byte[] block, int n, int[] sa, int[] rank, int[] tmp)
    {
        if (n < 2)
        {
            if (n == 1)
            {
                sa[0] = 0;
            }

            return;
        }

        SortByLarssonSadakane(block, n, sa, rank, tmp);
    }

    /// <summary>
    /// Orden de rotaciones de Larsson y Sadakane ("Faster suffix sorting", 1999), adaptado a rotaciones
    /// cíclicas. Parte de las rotaciones ordenadas por sus primeros 4 bytes y en cada vuelta duplica el
    /// largo comparado (h), pero solo dentro de los grupos que siguen empatados: los ya resueltos se saltean
    /// de a tramos (largos negativos en <paramref name="sa"/>). El rango de un grupo es la última posición que
    /// ocupa. Con datos muy repetitivos el trabajo se concentra en las zonas repetidas en lugar de rehacer el
    /// bloque entero en cada vuelta. Las rotaciones idénticas (bloques periódicos) nunca se separan: al llegar
    /// h al largo del bloque quedan en el orden en que estén, que da la misma columna final.
    /// </summary>
    private static void SortByLarssonSadakane(byte[] block, int n, int[] sa, int[] rank, int[] tmp)
    {
        // Orden inicial por los primeros 4 bytes de cada rotación: radix LSD de dos pasadas de 16 bits.
        int[] count = ArrayPool<int>.Shared.Rent(65537);
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                int[] from = pass == 0 ? null! : tmp;
                int[] to = pass == 0 ? tmp : sa;
                Array.Clear(count, 0, 65537);
                for (int i = 0; i < n; i++)
                {
                    int r = pass == 0 ? i : from[i];
                    count[Key16(block, n, r, pass) + 1]++;
                }

                for (int k = 1; k <= 65536; k++)
                {
                    count[k] += count[k - 1];
                }

                for (int i = 0; i < n; i++)
                {
                    int r = pass == 0 ? i : from[i];
                    to[count[Key16(block, n, r, pass)]++] = r;
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(count);
        }

        // Grupos de igual clave: cada uno toma como rango su última posición; los de un elemento quedan
        // resueltos (−1 en sa).
        int groupEnd = n - 1;
        for (int i = n - 1; i >= 0; i--)
        {
            if (i < n - 1 && Key32(block, n, sa[i]) != Key32(block, n, sa[i + 1]))
            {
                groupEnd = i;
            }

            rank[sa[i]] = groupEnd;
        }

        // Un grupo de un elemento es el que termina donde empieza. El rango del anterior se guarda antes de
        // marcarlo, porque la marca pisa su entrada en sa.
        int previousRank = -1;
        for (int i = 0; i < n; i++)
        {
            int current = rank[sa[i]];
            bool alone = previousRank != current && current == i;
            previousRank = current;
            if (alone)
            {
                sa[i] = -1;
            }
        }

        var sorter = new GroupSorter(sa, rank, n);
        for (long h = 4; sa[0] > -n; h *= 2)
        {
            if (h >= n)
            {
                // Lo que sigue empatado son rotaciones idénticas: cada una recibe su posición actual.
                for (int i = 0; i < n;)
                {
                    int s = sa[i];
                    if (s < 0)
                    {
                        i -= s;
                        continue;
                    }

                    int end = rank[s] + 1;
                    for (int j = i; j < end; j++)
                    {
                        rank[sa[j]] = j;
                    }

                    i = end;
                }

                break;
            }

            sorter.H = (int)h;
            int pi = 0;
            int sortedRun = 0;
            while (pi < n)
            {
                int s = sa[pi];
                if (s < 0)
                {
                    pi -= s;
                    sortedRun += s;
                }
                else
                {
                    if (sortedRun != 0)
                    {
                        sa[pi + sortedRun] = sortedRun;
                        sortedRun = 0;
                    }

                    int end = rank[s] + 1;
                    if (end <= pi)
                    {
                        // Invariante: un grupo sin resolver termina después de donde empieza. Si no, un
                        // error de rangos dejaría este recorrido sin avanzar nunca.
                        throw new InvalidOperationException("BZip2: grupo de rotaciones inconsistente.");
                    }

                    sorter.SortSplit(pi, end - pi);
                    pi = end;
                }
            }

            if (sortedRun != 0)
            {
                sa[pi + sortedRun] = sortedRun;
            }
        }

        // rank tiene ahora la posición final de cada rotación.
        for (int i = 0; i < n; i++)
        {
            sa[rank[i]] = i;
        }
    }

    /// <summary>16 bits de la clave de 4 bytes de la rotación r: pass 0 los bajos, pass 1 los altos.</summary>
    private static int Key16(byte[] block, int n, int r, int pass)
    {
        int a = pass == 0 ? r + 2 : r;
        if (a >= n) a -= n;
        int b = a + 1;
        if (b >= n) b -= n;
        return (block[a] << 8) | block[b];
    }

    private static uint Key32(byte[] block, int n, int r)
    {
        uint key = 0;
        for (int k = 0; k < 4; k++)
        {
            int i = r + k;
            if (i >= n) i -= n;
            if (i >= n) i %= n;
            key = (key << 8) | block[i];
        }

        return key;
    }

    /// <summary>
    /// Ordenamiento de un grupo por el rango de la rotación h posiciones más adelante (quicksort de tres
    /// vías con partición de extremos, como qsufsort), actualizando los rangos de los subgrupos que salen.
    /// </summary>
    private sealed class GroupSorter(int[] sa, int[] rank, int n)
    {
        public int H { get; set; }

        private int Key(int p)
        {
            int i = sa[p] + H;
            if (i >= n) i -= n;
            return rank[i];
        }

        private void Swap(int a, int b) => (sa[a], sa[b]) = (sa[b], sa[a]);

        private void UpdateGroup(int pl, int pm)
        {
            int g = pm;
            rank[sa[pl]] = g;
            if (pl == pm)
            {
                sa[pl] = -1;
                return;
            }

            while (pl < pm)
            {
                rank[sa[++pl]] = g;
            }
        }

        private void SelectSortSplit(int p, int len)
        {
            int pa = p;
            int pn = p + len - 1;
            while (pa < pn)
            {
                int pb = pa + 1;
                int f = Key(pa);
                for (int pi = pa + 1; pi <= pn; pi++)
                {
                    int v = Key(pi);
                    if (v < f)
                    {
                        f = v;
                        Swap(pi, pa);
                        pb = pa + 1;
                    }
                    else if (v == f)
                    {
                        Swap(pi, pb);
                        pb++;
                    }
                }

                UpdateGroup(pa, pb - 1);
                pa = pb;
            }

            if (pa == pn)
            {
                rank[sa[pa]] = pa;
                sa[pa] = -1;
            }
        }

        private int ChoosePivot(int p, int len)
        {
            int pm = p + len / 2;
            if (len > 7)
            {
                int pl = p;
                int pn = p + len - 1;
                if (len > 40)
                {
                    int s = len / 8;
                    pl = MedianOf3(pl, pl + s, pl + 2 * s);
                    pm = MedianOf3(pm - s, pm, pm + s);
                    pn = MedianOf3(pn - 2 * s, pn - s, pn);
                }

                pm = MedianOf3(pl, pm, pn);
            }

            return Key(pm);
        }

        private int MedianOf3(int a, int b, int c)
        {
            int ka = Key(a), kb = Key(b), kc = Key(c);
            return ka < kb
                ? (kb < kc ? b : ka < kc ? c : a)
                : (kb > kc ? b : ka > kc ? c : a);
        }

        public void SortSplit(int p, int len)
        {
            while (true)
            {
                if (len < 7)
                {
                    SelectSortSplit(p, len);
                    return;
                }

                int v = ChoosePivot(p, len);
                int pa = p, pb = p, pc = p + len - 1, pd = p + len - 1;
                while (true)
                {
                    int f;
                    while (pb <= pc && (f = Key(pb)) <= v)
                    {
                        if (f == v)
                        {
                            Swap(pa, pb);
                            pa++;
                        }

                        pb++;
                    }

                    while (pc >= pb && (f = Key(pc)) >= v)
                    {
                        if (f == v)
                        {
                            Swap(pc, pd);
                            pd--;
                        }

                        pc--;
                    }

                    if (pb > pc)
                    {
                        break;
                    }

                    Swap(pb, pc);
                    pb++;
                    pc--;
                }

                // Los iguales, que quedaron en los extremos, pasan al medio.
                int pn = p + len;
                int s = Math.Min(pa - p, pb - pa);
                for (int pl = p, pm = pb - s; s > 0; s--, pl++, pm++)
                {
                    Swap(pl, pm);
                }

                s = Math.Min(pd - pc, pn - pd - 1);
                for (int pl = pb, pm = pn - s; s > 0; s--, pl++, pm++)
                {
                    Swap(pl, pm);
                }

                int less = pb - pa;
                int greater = pd - pc;
                if (less > 0)
                {
                    SortSplit(p, less);
                }

                UpdateGroup(p + less, p + len - greater - 1);
                if (greater == 0)
                {
                    return;
                }

                // Los mayores, sin recursión: el orden menores → iguales → mayores se respeta igual.
                p = p + len - greater;
                len = greater;
            }
        }
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            }

            table[i] = c;
        }

        return table;
    }

    protected override void Dispose(bool disposing)
    {
        _finished = true;
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

    public override void Flush() => _output.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Bits del más significativo al menos (orden de BZip2), en memoria.</summary>
    internal sealed class BitBuffer
    {
        private byte[] _bytes = new byte[1 << 12];
        private int _byteCount;
        private uint _acc;      // bits pendientes, alineados a la derecha
        private int _accBits;

        public void WriteBits(uint value, int count)
        {
            // De a 16 como máximo por vuelta: el acumulador nunca pasa de 23 bits.
            while (count > 16)
            {
                count -= 16;
                WriteBits((value >> count) & 0xFFFF, 16);
            }

            _acc = (_acc << count) | (value & ((1u << count) - 1));
            _accBits += count;
            while (_accBits >= 8)
            {
                _accBits -= 8;
                PutByte((byte)(_acc >> _accBits));
            }

            _acc &= (1u << _accBits) - 1;
        }

        /// <summary>Agrega todos los bits de otro búfer (que no se vuelve a usar).</summary>
        public void Append(BitBuffer other)
        {
            for (int i = 0; i < other._byteCount; i++)
            {
                WriteBits(other._bytes[i], 8);
            }

            if (other._accBits > 0)
            {
                WriteBits(other._acc, other._accBits);
            }
        }

        /// <summary>Vuelca los bytes completos; con <paramref name="final"/> completa el último con ceros.</summary>
        public void FlushTo(Stream output, bool final)
        {
            if (final && _accBits > 0)
            {
                WriteBits(0, 8 - _accBits);
            }

            output.Write(_bytes, 0, _byteCount);
            _byteCount = 0;
        }

        private void PutByte(byte b)
        {
            if (_byteCount == _bytes.Length)
            {
                Array.Resize(ref _bytes, _bytes.Length * 2);
            }

            _bytes[_byteCount++] = b;
        }
    }
}
