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

using PrZip.Exceptions;

namespace PrZip.Codecs.Deflate;

/// <summary>
/// Descompresor Deflate (RFC 1951) y Deflate64 escrito desde la especificación.
///
/// Deflate64 ("Enhanced Deflate") difiere solo en tres puntos: ventana de 64 KB, el código de largo 285
/// pasa de "258 fijo" a base 3 con 16 bits extra, y se habilitan los códigos de distancia 30 y 31
/// (bases 32769 y 49153, 14 bits extra).
///
/// Decodifica bloques almacenados, de Huffman fijo y dinámico, con tablas de búsqueda de 9 bits y un
/// recorrido canónico para los códigos más largos. Cualquier inconsistencia lanza CorruptArchiveException.
/// </summary>
internal sealed class Inflater
{
    private const int MaxBits = 15;
    private const int FastBits = 9;

    private static readonly ushort[] LengthBase =
        [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];

    private static readonly byte[] LengthExtra =
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    private static readonly uint[] DistBase =
    [
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769,
        1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577, 32769, 49153
    ];

    private static readonly byte[] DistExtra =
        [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14];

    private static readonly byte[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private readonly bool _deflate64;
    private readonly byte[] _window;
    private readonly int _windowMask;
    private int _windowPos;

    private readonly Stream _input;
    private ulong _bitBuffer;
    private int _bitCount;
    private readonly byte[] _inBuf = new byte[1 << 16];
    private int _inPos;
    private int _inLen;

    private readonly Huffman _litLen = new(288);
    private readonly Huffman _dist = new(32);
    private readonly Huffman _codeLen = new(19);

    private Inflater(Stream input, bool deflate64)
    {
        _input = input;
        _deflate64 = deflate64;
        _window = new byte[deflate64 ? 1 << 16 : 1 << 15];
        _windowMask = _window.Length - 1;
    }

    /// <summary>
    /// Descomprime hasta el bloque final o hasta producir unpackSize bytes (lo que llegue primero).
    /// Con unpackSize conocido, terminar antes es error.
    /// </summary>
    public static void Decode(Stream input, Stream output, long unpackSize, bool deflate64)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        new Inflater(input, deflate64).Run(output, unpackSize);
    }

    private void Run(Stream output, long unpackSize)
    {
        long produced = 0;
        int flushedTo = 0;

        void Flush()
        {
            int end = _windowPos;
            if (end < flushedTo)
            {
                output.Write(_window, flushedTo, _window.Length - flushedTo);
                flushedTo = 0;
            }

            output.Write(_window, flushedTo, end - flushedTo);
            flushedTo = end == _window.Length ? 0 : end;
        }

        bool final = false;
        while (!final && (unpackSize < 0 || produced < unpackSize))
        {
            final = ReadBits(1) == 1;
            int type = (int)ReadBits(2);

            if (type == 0)
            {
                // Bloque almacenado: alinear a byte, LEN y NLEN.
                DropToByteBoundary();
                uint len = ReadBits(16);
                uint nlen = ReadBits(16);
                if ((len ^ 0xFFFF) != nlen)
                {
                    throw new CorruptArchiveException("Deflate: bloque almacenado con LEN/NLEN inconsistentes.");
                }

                for (uint i = 0; i < len; i++)
                {
                    PutByte((byte)ReadBits(8), output, ref flushedTo);
                }

                produced += len;
                continue;
            }

            if (type == 1)
            {
                BuildFixedTables();
            }
            else if (type == 2)
            {
                ReadDynamicTables();
            }
            else
            {
                throw new CorruptArchiveException("Deflate: tipo de bloque reservado (3).");
            }

            while (true)
            {
                int sym = _litLen.Decode(this);
                if (sym < 256)
                {
                    PutByte((byte)sym, output, ref flushedTo);
                    produced++;
                    continue;
                }

                if (sym == 256)
                {
                    break;
                }

                sym -= 257;
                if (sym >= 29)
                {
                    throw new CorruptArchiveException($"Deflate: símbolo de largo inválido ({sym + 257}).");
                }

                int length;
                if (sym == 28 && _deflate64)
                {
                    length = 3 + (int)ReadBits(16);
                }
                else
                {
                    length = LengthBase[sym] + (int)ReadBits(LengthExtra[sym]);
                }

                int distSym = _dist.Decode(this);
                if (distSym >= (_deflate64 ? 32 : 30))
                {
                    throw new CorruptArchiveException($"Deflate: código de distancia inválido ({distSym}).");
                }

                uint distance = DistBase[distSym] + ReadBits(DistExtra[distSym]);
                if (distance > _window.Length || distance > produced)
                {
                    throw new CorruptArchiveException($"Deflate: distancia {distance} fuera de la ventana.");
                }

                int from = (_windowPos - (int)distance) & _windowMask;
                for (int i = 0; i < length; i++)
                {
                    PutByte(_window[from], output, ref flushedTo);
                    from = (from + 1) & _windowMask;
                }

                produced += length;
            }
        }

        Flush();

        if (unpackSize >= 0 && produced < unpackSize)
        {
            throw new CorruptArchiveException($"Deflate: el stream terminó con {produced} bytes de {unpackSize} esperados.");
        }
    }

    private void PutByte(byte b, Stream output, ref int flushedTo)
    {
        _window[_windowPos++] = b;
        if (_windowPos == _window.Length)
        {
            output.Write(_window, flushedTo, _window.Length - flushedTo);
            _windowPos = 0;
            flushedTo = 0;
        }
    }

    // ---------------- Bits (LSB primero) ----------------

    private int NextInputByte()
    {
        if (_inPos == _inLen)
        {
            _inLen = _input.Read(_inBuf, 0, _inBuf.Length);
            _inPos = 0;
            if (_inLen <= 0)
            {
                _inLen = 0;
                return -1;
            }
        }

        return _inBuf[_inPos++];
    }

    private void EnsureBits(int count)
    {
        while (_bitCount < count)
        {
            int b = NextInputByte();
            if (b < 0)
            {
                throw new ArchiveTruncatedException("Fin inesperado del flujo Deflate.");
            }

            _bitBuffer |= (ulong)b << _bitCount;
            _bitCount += 8;
        }
    }

    /// <summary>Intenta tener count bits; al final del stream completa con ceros (para la búsqueda rápida).</summary>
    private int PeekBits(int count)
    {
        while (_bitCount < count)
        {
            int b = NextInputByte();
            if (b < 0)
            {
                break;
            }

            _bitBuffer |= (ulong)b << _bitCount;
            _bitCount += 8;
        }

        return (int)(_bitBuffer & ((1UL << count) - 1));
    }

    private uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        EnsureBits(count);
        uint value = (uint)(_bitBuffer & ((1UL << count) - 1));
        _bitBuffer >>= count;
        _bitCount -= count;
        return value;
    }

    private void Consume(int count)
    {
        if (count > _bitCount)
        {
            throw new ArchiveTruncatedException("Fin inesperado del flujo Deflate.");
        }

        _bitBuffer >>= count;
        _bitCount -= count;
    }

    private void DropToByteBoundary()
    {
        int drop = _bitCount & 7;
        _bitBuffer >>= drop;
        _bitCount -= drop;
    }

    // ---------------- Tablas ----------------

    private void BuildFixedTables()
    {
        Span<byte> lengths = stackalloc byte[288];
        lengths[..144].Fill(8);
        lengths[144..256].Fill(9);
        lengths[256..280].Fill(7);
        lengths[280..].Fill(8);
        _litLen.Build(lengths);

        Span<byte> dist = stackalloc byte[32];
        dist.Fill(5);
        _dist.Build(dist);
    }

    private void ReadDynamicTables()
    {
        int hlit = (int)ReadBits(5) + 257;
        int hdist = (int)ReadBits(5) + 1;
        int hclen = (int)ReadBits(4) + 4;
        if (hlit > 286 && !_deflate64)
        {
            throw new CorruptArchiveException($"Deflate: HLIT inválido ({hlit}).");
        }

        Span<byte> codeLengths = stackalloc byte[19];
        for (int i = 0; i < hclen; i++)
        {
            codeLengths[CodeLengthOrder[i]] = (byte)ReadBits(3);
        }

        _codeLen.Build(codeLengths);

        Span<byte> lengths = stackalloc byte[hlit + hdist];
        int n = 0;
        while (n < lengths.Length)
        {
            int sym = _codeLen.Decode(this);
            if (sym < 16)
            {
                lengths[n++] = (byte)sym;
                continue;
            }

            int repeat;
            byte value = 0;
            if (sym == 16)
            {
                if (n == 0)
                {
                    throw new CorruptArchiveException("Deflate: repetición sin longitud previa.");
                }

                value = lengths[n - 1];
                repeat = 3 + (int)ReadBits(2);
            }
            else if (sym == 17)
            {
                repeat = 3 + (int)ReadBits(3);
            }
            else
            {
                repeat = 11 + (int)ReadBits(7);
            }

            if (n + repeat > lengths.Length)
            {
                throw new CorruptArchiveException("Deflate: las longitudes de código exceden la tabla.");
            }

            lengths.Slice(n, repeat).Fill(value);
            n += repeat;
        }

        if (lengths[256] == 0)
        {
            throw new CorruptArchiveException("Deflate: el bloque no tiene código de fin.");
        }

        _litLen.Build(lengths[..hlit]);
        _dist.Build(lengths[hlit..]);
    }

    /// <summary>
    /// Código de Huffman canónico: tabla directa de 9 bits para los códigos cortos y recorrido por
    /// longitud (con contadores y símbolos ordenados) para los demás.
    /// </summary>
    private sealed class Huffman(int maxSymbols)
    {
        private readonly short[] _fast = new short[1 << FastBits]; // (símbolo << 4) | longitud; -1 = buscar lento
        private readonly ushort[] _count = new ushort[MaxBits + 1];
        private readonly ushort[] _symbols = new ushort[maxSymbols];
        private int _numSymbols;

        public void Build(ReadOnlySpan<byte> lengths)
        {
            Array.Clear(_count);
            foreach (byte l in lengths)
            {
                _count[l]++;
            }

            _count[0] = 0;
            _numSymbols = lengths.Length;

            // Verificar que el código no esté sobresuscrito.
            int left = 1;
            for (int len = 1; len <= MaxBits; len++)
            {
                left <<= 1;
                left -= _count[len];
                if (left < 0)
                {
                    throw new CorruptArchiveException("Deflate: código de Huffman sobresuscrito.");
                }
            }

            Span<ushort> offsets = stackalloc ushort[MaxBits + 2];
            for (int len = 1; len <= MaxBits; len++)
            {
                offsets[len + 1] = (ushort)(offsets[len] + _count[len]);
            }

            for (int sym = 0; sym < lengths.Length; sym++)
            {
                if (lengths[sym] != 0)
                {
                    _symbols[offsets[lengths[sym]]++] = (ushort)sym;
                }
            }

            // Tabla rápida: recorre los códigos canónicos de hasta FastBits bits.
            Array.Fill(_fast, (short)-1);
            int code = 0;
            int index = 0;
            for (int len = 1; len <= FastBits; len++)
            {
                for (int k = 0; k < _count[len]; k++, index++, code++)
                {
                    int reversed = Reverse(code, len);
                    short entry = (short)((_symbols[index] << 4) | len);
                    for (int fill = reversed; fill < (1 << FastBits); fill += 1 << len)
                    {
                        _fast[fill] = entry;
                    }
                }

                code <<= 1;
            }
        }

        private static int Reverse(int code, int len)
        {
            int r = 0;
            for (int i = 0; i < len; i++)
            {
                r = (r << 1) | (code & 1);
                code >>= 1;
            }

            return r;
        }

        public int Decode(Inflater inflater)
        {
            int peek = inflater.PeekBits(FastBits);
            short entry = _fast[peek];
            if (entry >= 0)
            {
                inflater.Consume(entry & 15);
                return entry >> 4;
            }

            // Recorrido canónico bit a bit (RFC 1951, 3.2.2): los códigos de cada longitud son consecutivos.
            int code = 0;
            int first = 0;
            int index = 0;
            for (int len = 1; len <= MaxBits; len++)
            {
                code |= (int)inflater.ReadBits(1);
                int count = _count[len];
                if (code - count < first)
                {
                    return _symbols[index + (code - first)];
                }

                index += count;
                first += count;
                first <<= 1;
                code <<= 1;
            }

            throw new CorruptArchiveException("Deflate: código de Huffman inválido.");
        }
    }
}
