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

using PrZip.Codecs.Huffman;

namespace PrZip.Codecs.Deflate;

/// <summary>
/// Compresor Deflate (RFC 1951) propio, escrito desde la especificación. Coincidencias LZ77 por cadenas
/// de hash en una ventana de 32 KB, con evaluación diferida de un paso desde el nivel 4; cada bloque se
/// emite con códigos de Huffman dinámicos, fijos o sin comprimir, el que resulte más corto.
/// Stream de solo escritura: lo que recibe sale comprimido hacia el destino; <see cref="Finish"/> cierra
/// el último bloque. La salida depende solo de los datos y del nivel.
/// </summary>
internal sealed class DeflateEncoderStream : Stream
{
    private const int WindowSize = 32768;
    private const int MinMatch = 3;
    private const int MaxMatch = 258;
    private const int MinLookahead = MaxMatch + MinMatch + 1;
    private const int BufferSize = WindowSize + (1 << 18);
    private const int HashBits = 15;
    private const int HashSize = 1 << HashBits;
    private const int MaxBlockTokens = 1 << 14;
    private const int EndOfBlock = 256;
    private const int LitLenSymbols = 286;
    private const int DistSymbols = 30;

    private static readonly int[] LengthBase =
        [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];

    private static readonly int[] LengthExtra =
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    private static readonly int[] DistBase =
    [
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769,
        1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577
    ];

    private static readonly int[] DistExtra =
        [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    // Orden de los largos del código de largos (RFC 1951 §3.2.7).
    private static readonly int[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static readonly byte[] LengthToCode = BuildLengthToCode();
    private static readonly byte[] FixedLitLenLengths = BuildFixedLitLen();
    private static readonly byte[] FixedDistLengths = Enumerable.Repeat((byte)5, DistSymbols).ToArray();

    private readonly Stream _output;
    private readonly int _maxChain;
    private readonly int _niceLength;
    private readonly int _maxLazy;
    private readonly bool _lazy;

    private readonly byte[] _buf = new byte[BufferSize];
    private readonly int[] _head = new int[HashSize];
    private readonly int[] _prev = new int[BufferSize];
    private int _len;       // bytes válidos en _buf
    private int _pos;       // próxima posición a analizar
    private int _emitPos;   // hasta dónde llegan los tokens emitidos
    private int _blockStart;

    // Evaluación diferida: la coincidencia hallada en _pos − 1 espera a ver si en _pos hay una mejor.
    private bool _matchAvailable;
    private int _prevLength;
    private int _prevDistance;

    private readonly ushort[] _tokLitLen = new ushort[MaxBlockTokens]; // literal 0..255, o 256 + largo
    private readonly ushort[] _tokDist = new ushort[MaxBlockTokens];   // 0 para literales
    private int _tokens;

    private readonly BitWriter _bits;
    private bool _finished;

    /// <param name="output">Destino del stream Deflate.</param>
    /// <param name="level">1 (rápido) a 9 (máximo). 0 se trata como 1.</param>
    public DeflateEncoderStream(Stream output, int level = 5)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _bits = new BitWriter(output);
        level = Math.Clamp(level, 1, 9);
        (_maxChain, _niceLength, _maxLazy, _lazy) = level switch
        {
            1 => (4, 16, 0, false),
            2 => (8, 32, 0, false),
            3 => (32, 64, 0, false),
            4 => (32, 64, 16, true),
            5 => (128, 128, 32, true),
            6 => (256, 192, 64, true),
            7 => (1024, 258, 128, true),
            8 => (2048, 258, 258, true),
            _ => (4096, 258, 258, true)
        };
        Array.Fill(_head, -1);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        while (!buffer.IsEmpty)
        {
            if (_len == BufferSize)
            {
                Parse(_len - MinLookahead);
                Slide();
            }

            int n = Math.Min(buffer.Length, BufferSize - _len);
            buffer[..n].CopyTo(_buf.AsSpan(_len));
            _len += n;
            buffer = buffer[n..];
        }
    }

    /// <summary>Analiza lo pendiente y escribe el bloque final. Después no se puede escribir más.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        Parse(_len);
        if (_matchAvailable)
        {
            if (_prevLength >= MinMatch)
            {
                EmitMatch(_prevLength, _prevDistance);
            }
            else
            {
                EmitLiteral(_buf[_pos - 1]);
            }

            _matchAvailable = false;
        }

        if (_tokens > 0)
        {
            FlushBlock(final: true);
        }
        else
        {
            // Bloque final vacío con códigos fijos: solo el fin de bloque.
            _bits.Write(1, 1);
            _bits.Write(1, 2);
            WriteFixedSymbol(EndOfBlock);
        }

        _bits.AlignAndFlush();
        _finished = true;
    }

    private void Parse(int limit)
    {
        while (_pos < limit)
        {
            int p = _pos;
            int curLength = 0, curDistance = 0;
            if (_len - p >= MinMatch)
            {
                if (!_lazy || !_matchAvailable || _prevLength < _maxLazy)
                {
                    (curLength, curDistance) = FindMatch(p, _lazy && _matchAvailable ? _prevLength : MinMatch - 1);
                }

                Insert(p);
            }

            if (!_lazy)
            {
                if (curLength >= MinMatch)
                {
                    EmitMatch(curLength, curDistance);
                    InsertRange(p + 1, p + curLength);
                    _pos = p + curLength;
                }
                else
                {
                    EmitLiteral(_buf[p]);
                    _pos = p + 1;
                }

                continue;
            }

            if (_matchAvailable && _prevLength >= MinMatch && curLength <= _prevLength)
            {
                // La coincidencia de la posición anterior gana: se emite y se saltea lo que cubre.
                EmitMatch(_prevLength, _prevDistance);
                int end = p - 1 + _prevLength;
                InsertRange(p + 1, end);
                _pos = end;
                _matchAvailable = false;
                _prevLength = 0;
            }
            else
            {
                if (_matchAvailable)
                {
                    EmitLiteral(_buf[p - 1]);
                }

                _matchAvailable = true;
                _prevLength = curLength;
                _prevDistance = curDistance;
                _pos = p + 1;
            }
        }
    }

    private int Hash(int p) => ((_buf[p] << 10) ^ (_buf[p + 1] << 5) ^ _buf[p + 2]) & (HashSize - 1);

    private void Insert(int p)
    {
        if (p + MinMatch > _len)
        {
            return;
        }

        int h = Hash(p);
        _prev[p] = _head[h];
        _head[h] = p;
    }

    private void InsertRange(int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            Insert(i);
        }
    }

    /// <summary>Mejor coincidencia anterior a <paramref name="p"/> que supere <paramref name="minLength"/>.</summary>
    private (int Length, int Distance) FindMatch(int p, int minLength)
    {
        int maxLength = Math.Min(MaxMatch, _len - p);
        if (maxLength < MinMatch || minLength >= maxLength)
        {
            return (0, 0);
        }

        int bestLength = minLength;
        int bestDistance = 0;
        int chain = _maxChain;
        int candidate = _head[Hash(p)];
        int lowest = p - WindowSize;
        var buf = _buf;

        while (candidate >= lowest && candidate >= 0 && chain-- > 0)
        {
            if (candidate < p
                && buf[candidate + bestLength] == buf[p + bestLength]
                && buf[candidate] == buf[p]
                && buf[candidate + 1] == buf[p + 1])
            {
                int length = 2;
                while (length < maxLength && buf[candidate + length] == buf[p + length])
                {
                    length++;
                }

                if (length > bestLength)
                {
                    bestLength = length;
                    bestDistance = p - candidate;
                    if (length >= _niceLength || length == maxLength)
                    {
                        break;
                    }
                }
            }

            candidate = _prev[candidate];
        }

        // Una coincidencia de 3 bytes muy lejana cuesta más que tres literales.
        if (bestDistance == 0 || (bestLength == MinMatch && bestDistance > 4096))
        {
            return (0, 0);
        }

        return (bestLength, bestDistance);
    }

    private void EmitLiteral(byte value)
    {
        _tokLitLen[_tokens] = value;
        _tokDist[_tokens] = 0;
        _tokens++;
        _emitPos++;
        if (_tokens == MaxBlockTokens)
        {
            FlushBlock(final: false);
        }
    }

    private void EmitMatch(int length, int distance)
    {
        _tokLitLen[_tokens] = (ushort)(256 + length);
        _tokDist[_tokens] = (ushort)distance;
        _tokens++;
        _emitPos += length;
        if (_tokens == MaxBlockTokens)
        {
            FlushBlock(final: false);
        }
    }

    /// <summary>
    /// Conserva los últimos 32 KB ya analizados más lo pendiente y rehace las cadenas de hash. Antes
    /// cierra el bloque en curso: sus bytes originales hacen falta si conviene emitirlo sin comprimir.
    /// </summary>
    private void Slide()
    {
        if (_tokens > 0)
        {
            FlushBlock(final: false);
        }

        int keepFrom = Math.Max(0, Math.Min(_pos, _emitPos) - WindowSize);
        int kept = _len - keepFrom;
        Buffer.BlockCopy(_buf, keepFrom, _buf, 0, kept);
        _len = kept;
        _pos -= keepFrom;
        _emitPos -= keepFrom;
        _blockStart -= keepFrom;

        Array.Fill(_head, -1);
        for (int i = 0; i < _pos; i++)
        {
            Insert(i);
        }
    }

    private void FlushBlock(bool final)
    {
        Span<int> litFreq = stackalloc int[LitLenSymbols];
        Span<int> distFreq = stackalloc int[DistSymbols];
        litFreq.Clear();
        distFreq.Clear();
        long extraBits = 0;
        for (int i = 0; i < _tokens; i++)
        {
            int v = _tokLitLen[i];
            if (v < 256)
            {
                litFreq[v]++;
            }
            else
            {
                int lc = LengthToCode[v - 256];
                litFreq[257 + lc]++;
                int dc = DistanceCode(_tokDist[i]);
                distFreq[dc]++;
                extraBits += LengthExtra[lc] + DistExtra[dc];
            }
        }

        litFreq[EndOfBlock]++;

        Span<byte> litLengths = stackalloc byte[LitLenSymbols];
        Span<byte> distLengths = stackalloc byte[DistSymbols];
        BuildCompleteLengths(litFreq, 15, litLengths);
        BuildCompleteLengths(distFreq, 15, distLengths);

        int hlit = LitLenSymbols;
        while (hlit > 257 && litLengths[hlit - 1] == 0)
        {
            hlit--;
        }

        int hdist = DistSymbols;
        while (hdist > 1 && distLengths[hdist - 1] == 0)
        {
            hdist--;
        }

        // Largos de los dos códigos, uno a continuación del otro, comprimidos con los símbolos 16/17/18.
        Span<byte> all = stackalloc byte[LitLenSymbols + DistSymbols];
        litLengths[..hlit].CopyTo(all);
        distLengths[..hdist].CopyTo(all[hlit..]);
        var clSymbols = new List<(int Symbol, int Extra)>();
        RunLengthEncode(all[..(hlit + hdist)], clSymbols);

        Span<int> clFreq = stackalloc int[19];
        clFreq.Clear();
        foreach (var (s, _) in clSymbols)
        {
            clFreq[s]++;
        }

        Span<byte> clLengths = stackalloc byte[19];
        BuildCompleteLengths(clFreq, 7, clLengths);
        int hclen = 19;
        while (hclen > 4 && clLengths[CodeLengthOrder[hclen - 1]] == 0)
        {
            hclen--;
        }

        long dynamicBits = 3 + 5 + 5 + 4 + 3L * hclen + extraBits;
        foreach (var (s, _) in clSymbols)
        {
            dynamicBits += clLengths[s] + (s == 16 ? 2 : s == 17 ? 3 : s == 18 ? 7 : 0);
        }

        long fixedBits = 3 + extraBits;
        for (int i = 0; i < LitLenSymbols; i++)
        {
            dynamicBits += (long)litFreq[i] * litLengths[i];
            fixedBits += (long)litFreq[i] * FixedLitLenLengths[i];
        }

        for (int i = 0; i < DistSymbols; i++)
        {
            dynamicBits += (long)distFreq[i] * distLengths[i];
            fixedBits += (long)distFreq[i] * FixedDistLengths[i];
        }

        int rawLength = _emitPos - _blockStart;
        long storedBits = 8L * rawLength + 48L * Math.Max(1, (rawLength + 65534) / 65535);

        if (storedBits < dynamicBits && storedBits < fixedBits)
        {
            WriteStored(final, rawLength);
        }
        else if (fixedBits <= dynamicBits)
        {
            _bits.Write(final ? 1u : 0u, 1);
            _bits.Write(1, 2);
            WriteTokens(FixedLitLenLengths, FixedDistLengths);
        }
        else
        {
            _bits.Write(final ? 1u : 0u, 1);
            _bits.Write(2, 2);
            _bits.Write((uint)(hlit - 257), 5);
            _bits.Write((uint)(hdist - 1), 5);
            _bits.Write((uint)(hclen - 4), 4);
            for (int i = 0; i < hclen; i++)
            {
                _bits.Write(clLengths[CodeLengthOrder[i]], 3);
            }

            Span<int> clCodes = stackalloc int[19];
            HuffmanCodeBuilder.BuildCanonicalCodes(clLengths, clCodes, 7);
            foreach (var (s, extra) in clSymbols)
            {
                _bits.WriteCode(clCodes[s], clLengths[s]);
                if (s == 16) _bits.Write((uint)extra, 2);
                else if (s == 17) _bits.Write((uint)extra, 3);
                else if (s == 18) _bits.Write((uint)extra, 7);
            }

            WriteTokens(litLengths, distLengths);
        }

        _tokens = 0;
        _blockStart = _emitPos;
    }

    private void WriteStored(bool final, int rawLength)
    {
        int offset = _blockStart;
        do
        {
            int chunk = Math.Min(65535, rawLength);
            rawLength -= chunk;
            _bits.Write(final && rawLength == 0 ? 1u : 0u, 1);
            _bits.Write(0, 2);
            _bits.AlignToByte();
            _bits.Write((uint)chunk, 16);
            _bits.Write((uint)(chunk ^ 0xFFFF), 16);
            _bits.WriteBytes(_buf.AsSpan(offset, chunk));
            offset += chunk;
        }
        while (rawLength > 0);
    }

    private void WriteTokens(ReadOnlySpan<byte> litLengths, ReadOnlySpan<byte> distLengths)
    {
        // Con todos los largos: en el código fijo (288 símbolos) los 286 y 287 no se usan, pero cuentan
        // para asignar los códigos canónicos de 9 bits. Sin ellos los literales 144..255 salen corridos.
        Span<int> litCodes = stackalloc int[litLengths.Length];
        Span<int> distCodes = stackalloc int[distLengths.Length];
        HuffmanCodeBuilder.BuildCanonicalCodes(litLengths, litCodes, 15);
        HuffmanCodeBuilder.BuildCanonicalCodes(distLengths, distCodes, 15);

        for (int i = 0; i < _tokens; i++)
        {
            int v = _tokLitLen[i];
            if (v < 256)
            {
                _bits.WriteCode(litCodes[v], litLengths[v]);
                continue;
            }

            int length = v - 256;
            int lc = LengthToCode[length];
            _bits.WriteCode(litCodes[257 + lc], litLengths[257 + lc]);
            if (LengthExtra[lc] > 0)
            {
                _bits.Write((uint)(length - LengthBase[lc]), LengthExtra[lc]);
            }

            int distance = _tokDist[i];
            int dc = DistanceCode(distance);
            _bits.WriteCode(distCodes[dc], distLengths[dc]);
            if (DistExtra[dc] > 0)
            {
                _bits.Write((uint)(distance - DistBase[dc]), DistExtra[dc]);
            }
        }

        _bits.WriteCode(litCodes[EndOfBlock], litLengths[EndOfBlock]);
    }

    private void WriteFixedSymbol(int symbol)
    {
        Span<int> codes = stackalloc int[288];
        HuffmanCodeBuilder.BuildCanonicalCodes(FixedLitLenLengths, codes, 15);
        _bits.WriteCode(codes[symbol], FixedLitLenLengths[symbol]);
    }

    /// <summary>
    /// Largos con código completo: con cero o un símbolo usado se agregan símbolos de relleno para que haya
    /// al menos dos de largo 1 (RFC 1951 admite un único código de distancia, pero no todos los decodificadores).
    /// </summary>
    private static void BuildCompleteLengths(Span<int> freq, int maxLength, Span<byte> lengths)
    {
        int used = 0;
        foreach (int f in freq)
        {
            if (f > 0) used++;
        }

        if (used < 2)
        {
            for (int i = 0; i < freq.Length && used < 2; i++)
            {
                if (freq[i] == 0)
                {
                    freq[i] = 1;
                    used++;
                }
            }
        }

        HuffmanCodeBuilder.BuildLengths(freq, maxLength, lengths);
    }

    private static void RunLengthEncode(ReadOnlySpan<byte> lengths, List<(int Symbol, int Extra)> output)
    {
        int i = 0;
        while (i < lengths.Length)
        {
            int value = lengths[i];
            int run = 1;
            while (i + run < lengths.Length && lengths[i + run] == value)
            {
                run++;
            }

            i += run;
            if (value == 0)
            {
                while (run >= 11)
                {
                    int n = Math.Min(run, 138);
                    output.Add((18, n - 11));
                    run -= n;
                }

                if (run >= 3)
                {
                    output.Add((17, run - 3));
                    run = 0;
                }
            }
            else
            {
                output.Add((value, 0));
                run--;
                while (run >= 3)
                {
                    int n = Math.Min(run, 6);
                    output.Add((16, n - 3));
                    run -= n;
                }
            }

            for (; run > 0; run--)
            {
                output.Add((value, 0));
            }
        }
    }

    private static int DistanceCode(int distance)
    {
        int code = 0;
        while (code < DistSymbols - 1 && DistBase[code + 1] <= distance)
        {
            code++;
        }

        return code;
    }

    private static byte[] BuildLengthToCode()
    {
        var table = new byte[MaxMatch + 1];
        for (int length = MinMatch; length <= MaxMatch; length++)
        {
            int code = 0;
            while (code < LengthBase.Length - 1 && LengthBase[code + 1] <= length)
            {
                code++;
            }

            table[length] = (byte)code;
        }

        return table;
    }

    private static byte[] BuildFixedLitLen()
    {
        var lengths = new byte[288];
        for (int i = 0; i < 288; i++)
        {
            lengths[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
        }

        return lengths;
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

    /// <summary>Bits hacia el destino, del menos significativo al más (orden de Deflate).</summary>
    private sealed class BitWriter(Stream output)
    {
        private readonly byte[] _buffer = new byte[1 << 16];
        private int _count;
        private ulong _acc;
        private int _bitCount;

        public void Write(uint value, int bits)
        {
            _acc |= (ulong)value << _bitCount;
            _bitCount += bits;
            while (_bitCount >= 8)
            {
                PutByte((byte)_acc);
                _acc >>= 8;
                _bitCount -= 8;
            }
        }

        /// <summary>Código de Huffman: se emite desde su bit más significativo.</summary>
        public void WriteCode(int code, int length)
        {
            uint reversed = 0;
            for (int i = 0; i < length; i++)
            {
                reversed = (reversed << 1) | (uint)((code >> i) & 1);
            }

            Write(reversed, length);
        }

        public void AlignToByte()
        {
            if (_bitCount > 0)
            {
                Write(0, 8 - _bitCount);
            }
        }

        public void WriteBytes(ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
            {
                PutByte(b);
            }
        }

        public void AlignAndFlush()
        {
            AlignToByte();
            output.Write(_buffer, 0, _count);
            _count = 0;
        }

        private void PutByte(byte b)
        {
            _buffer[_count++] = b;
            if (_count == _buffer.Length)
            {
                output.Write(_buffer, 0, _count);
                _count = 0;
            }
        }
    }
}
