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

namespace PrZip.Codecs.BZip2;

/// <summary>
/// Decodificador de flujos BZip2 puro en C# (.NET 10) con cero dependencias externas.
/// Implementa la descompresión completa de BZip2: BWT inversa, Move-to-Front (MTF),
/// decodificación Huffman multi-árbol y RLE1 / RLE2.
/// </summary>
internal sealed class BZip2Decoder
{
    private const int BZip2MaxAlphaSize = 258;
    private const int BZip2MaxCodeLen = 23;
    private const int BZip2NumTables = 6;
    private const int BZip2MaxSelectors = 18002;

    public static void Decode(Stream inStream, Stream outStream, long unpackSize = -1)
    {
        ArgumentNullException.ThrowIfNull(inStream);
        ArgumentNullException.ThrowIfNull(outStream);

        var reader = new BitReader(inStream);
        var output = new BufferedStream(outStream, 1 << 16);
        long totalWritten = 0;

        // 7-Zip y pbzip2 pueden concatenar varios streams BZh completos.
        while (DecodeStream(ref reader, output, unpackSize, ref totalWritten)
               && (unpackSize < 0 || totalWritten < unpackSize)
               && reader.TryAlignToNextStream())
        {
        }

        output.Flush();

        if (unpackSize >= 0 && totalWritten != unpackSize)
        {
            throw new CorruptArchiveException($"BZip2: se obtuvieron {totalWritten} bytes de {unpackSize} esperados.");
        }
    }

    /// <summary>
    /// Decodifica un stream BZh completo. Devuelve true si terminó en su marcador de fin.
    /// </summary>
    private static bool DecodeStream(ref BitReader reader, Stream outStream, long unpackSize, ref long totalWritten)
    {
        // Cabecera: 'B', 'Z', 'h', nivel ('1'..'9')
        int b0 = reader.ReadBits(8);
        int b1 = reader.ReadBits(8);
        int b2 = reader.ReadBits(8);
        int b3 = reader.ReadBits(8);

        if (b0 != 'B' || b1 != 'Z' || b2 != 'h' || b3 < '1' || b3 > '9')
        {
            throw new CorruptArchiveException("Cabecera de flujo BZip2 inválida o corrupta.");
        }

        int blockSize100k = b3 - '0';
        int maxBlockSize = blockSize100k * 100000;
        int[] tt = new int[maxBlockSize]; // Vector de transformación BWT
        byte[] ll = new byte[maxBlockSize]; // Columna L de símbolos decodificados RLE1

        // Buffers reutilizables fuera del bucle de bloques para evitar CA2014
        Span<bool> inUse16 = stackalloc bool[16];
        Span<byte> seqToUnseq = stackalloc byte[256];
        Span<byte> mtfSelectors = stackalloc byte[BZip2NumTables];
        Span<byte> mtf = stackalloc byte[256];
        Span<int> unzftab = stackalloc int[256];
        Span<int> cftab = stackalloc int[257];

        uint combinedCrc = 0;

        while (true)
        {
            // Leer magic de 48 bits (6 bytes)
            ulong magic = reader.ReadBits64(48);
            if (magic == 0x177245385090UL)
            {
                // Fin de flujo BZip2
                uint storedCombined = (uint)reader.ReadBits(32);
                if (storedCombined != combinedCrc)
                {
                    throw new CorruptArchiveException(
                        $"CRC combinado del stream BZip2 inválido: esperado 0x{storedCombined:X8}, calculado 0x{combinedCrc:X8}.");
                }

                return true;
            }

            if (magic != 0x314159265359UL)
            {
                throw new CorruptArchiveException($"Magic de bloque BZip2 inválido (0x{magic:X12}).");
            }

            uint storedBlockCrc = (uint)reader.ReadBits(32);
            // Bloque "randomised": el compresor alteró algunos bytes antes de ordenar (bzip2 0.9.0 lo hacía con
            // datos muy repetitivos, SharpZipLib todavía lo hace) y hay que deshacerlo al reconstruir.
            bool randomised = reader.ReadBit();
            int origPtr = reader.ReadBits(24);

            if (origPtr < 0 || origPtr >= maxBlockSize)
            {
                throw new CorruptArchiveException("Puntero de origen BWT fuera de rango.");
            }

            // 1. Tabla de mapeo de símbolos
            for (int i = 0; i < 16; i++)
            {
                inUse16[i] = reader.ReadBit();
            }

            int numSymbols = 0;
            for (int i = 0; i < 16; i++)
            {
                if (inUse16[i])
                {
                    for (int j = 0; j < 16; j++)
                    {
                        if (reader.ReadBit())
                        {
                            seqToUnseq[numSymbols++] = (byte)((i << 4) + j);
                        }
                    }
                }
            }

            int alphaSize = numSymbols + 2; // RUNA y RUNB

            // 2. Tablas y selectores Huffman
            int numGroups = reader.ReadBits(3);
            if (numGroups < 2 || numGroups > BZip2NumTables)
            {
                throw new CorruptArchiveException("Cantidad inválida de grupos Huffman en BZip2.");
            }

            int numSelectors = reader.ReadBits(15);
            if (numSelectors < 1 || numSelectors > BZip2MaxSelectors)
            {
                throw new CorruptArchiveException("Cantidad inválida de selectores Huffman en BZip2.");
            }

            for (int i = 0; i < numGroups; i++)
            {
                mtfSelectors[i] = (byte)i;
            }

            byte[] selectors = new byte[numSelectors];
            for (int i = 0; i < numSelectors; i++)
            {
                int count = 0;
                while (reader.ReadBit())
                {
                    count++;
                    if (count >= numGroups)
                    {
                        throw new CorruptArchiveException("Selector MTF fuera de rango.");
                    }
                }

                byte v = mtfSelectors[count];
                for (int j = count; j > 0; j--)
                {
                    mtfSelectors[j] = mtfSelectors[j - 1];
                }
                mtfSelectors[0] = v;
                selectors[i] = v;
            }

            // 3. Longitudes de códigos Huffman para cada grupo
            byte[][] codeLengths = new byte[numGroups][];
            for (int t = 0; t < numGroups; t++)
            {
                codeLengths[t] = new byte[alphaSize];
                int currLen = reader.ReadBits(5);
                for (int i = 0; i < alphaSize; i++)
                {
                    while (reader.ReadBit())
                    {
                        currLen += reader.ReadBit() ? -1 : 1;
                    }
                    if (currLen < 1 || currLen > BZip2MaxCodeLen)
                    {
                        throw new CorruptArchiveException("Longitud de código Huffman fuera de rango.");
                    }
                    codeLengths[t][i] = (byte)currLen;
                }
            }

            // Construir tablas de decodificación Huffman
            var huffmanTables = new HuffmanTable[numGroups];
            for (int t = 0; t < numGroups; t++)
            {
                huffmanTables[t] = HuffmanTable.Create(codeLengths[t], alphaSize);
            }

            // 4. Decodificar símbolos MTF hacia el buffer BWT
            for (int i = 0; i < numSymbols; i++)
            {
                mtf[i] = (byte)i;
            }

            int groupIdx = -1;
            int groupPos = 0;
            HuffmanTable? currTable = null;
            int blockSize = 0;

            unzftab.Clear();

            int runCount = 0;
            int runPower = 1;

            while (true)
            {
                if (groupPos == 0)
                {
                    groupIdx++;
                    if (groupIdx >= numSelectors)
                    {
                        throw new CorruptArchiveException("Exceso de selectores consumidos en BZip2.");
                    }
                    currTable = huffmanTables[selectors[groupIdx]];
                    groupPos = 50; // Cada selector dura 50 símbolos
                }
                groupPos--;

                int nextSym = currTable!.DecodeNext(ref reader);

                if (nextSym == 0 || nextSym == 1) // RUNA o RUNB
                {
                    runCount += runPower * (nextSym + 1);
                    runPower <<= 1;
                    continue;
                }

                if (runCount > 0)
                {
                    byte runByte = seqToUnseq[mtf[0]];
                    unzftab[runByte] += runCount;
                    while (runCount > 0)
                    {
                        if (blockSize >= maxBlockSize)
                        {
                            throw new CorruptArchiveException("Desbordamiento de tamaño de bloque BZip2.");
                        }
                        ll[blockSize++] = runByte;
                        runCount--;
                    }
                    runPower = 1;
                }

                if (nextSym == numSymbols + 1) // EOB (End of Block)
                {
                    break;
                }

                // Desplazar símbolo MTF al frente
                int mtfIdx = nextSym - 1;
                byte nextByteIdx = mtf[mtfIdx];
                for (int j = mtfIdx; j > 0; j--)
                {
                    mtf[j] = mtf[j - 1];
                }
                mtf[0] = nextByteIdx;

                byte literal = seqToUnseq[nextByteIdx];
                unzftab[literal]++;
                if (blockSize >= maxBlockSize)
                {
                    throw new CorruptArchiveException("Desbordamiento de tamaño de bloque BZip2.");
                }
                ll[blockSize++] = literal;
            }

            // 5. Invertir transformación BWT en O(N)
            cftab[0] = 0;
            for (int i = 0; i < 256; i++)
            {
                cftab[i + 1] = cftab[i] + unzftab[i];
            }

            for (int i = 0; i < blockSize; i++)
            {
                byte ch = ll[i];
                tt[cftab[ch]++] = i;
            }

            // 6. Deshacer el RLE inicial de bzip2: tras 4 bytes iguales (los 4 se emiten)
            //    el byte siguiente no es dato sino la cantidad de repeticiones extra (0..255).
            uint blockCrc = 0xFFFFFFFFu;
            int tpos = origPtr;
            int lastCh = -1;
            int runLength = 0;

            // Aleatorización: cada byte reconstruido se invierte en su bit 0 cuando la cuenta regresiva, que
            // se recarga de BZip2RandomTable, llega a 1. Vuelve a empezar en cada bloque.
            int randomCountdown = 0;
            int randomIndex = 0;

            for (int count = 0; count < blockSize; count++)
            {
                tpos = tt[tpos];
                int ch = ll[tpos];
                if (randomised)
                {
                    if (randomCountdown == 0)
                    {
                        randomCountdown = BZip2RandomTable.Numbers[randomIndex];
                        randomIndex = (randomIndex + 1) & 511;
                    }

                    randomCountdown--;
                    if (randomCountdown == 1)
                    {
                        ch ^= 1;
                    }
                }

                if (runLength == 4)
                {
                    for (int k = 0; k < ch; k++)
                    {
                        blockCrc = BZip2Crc.Update(blockCrc, (byte)lastCh);
                        WriteLimited(outStream, (byte)lastCh, unpackSize, ref totalWritten);
                    }

                    runLength = 0;
                    lastCh = -1;
                    continue;
                }

                runLength = ch == lastCh ? runLength + 1 : 1;
                lastCh = ch;
                blockCrc = BZip2Crc.Update(blockCrc, (byte)ch);
                WriteLimited(outStream, (byte)ch, unpackSize, ref totalWritten);
            }

            blockCrc = ~blockCrc;
            if (blockCrc != storedBlockCrc)
            {
                throw new CorruptArchiveException(
                    $"CRC de bloque BZip2 inválido: esperado 0x{storedBlockCrc:X8}, calculado 0x{blockCrc:X8}.");
            }

            combinedCrc = ((combinedCrc << 1) | (combinedCrc >> 31)) ^ blockCrc;
        }
    }

    private static void WriteLimited(Stream outStream, byte value, long unpackSize, ref long totalWritten)
    {
        if (unpackSize >= 0 && totalWritten >= unpackSize)
        {
            throw new CorruptArchiveException("BZip2: el stream produce más datos que el tamaño declarado.");
        }

        outStream.WriteByte(value);
        totalWritten++;
    }

    /// <summary>
    /// CRC-32 de bzip2: polinomio 0x04C11DB7 sin reflejar (MSB primero), distinto del CRC de 7z.
    /// </summary>
    private static class BZip2Crc
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 0x80000000u) != 0 ? (c << 1) ^ 0x04C11DB7u : c << 1;
                }

                table[i] = c;
            }

            return table;
        }

        public static uint Update(uint crc, byte b) => (crc << 8) ^ Table[(crc >> 24) ^ b];
    }

    private sealed class HuffmanTable
    {
        private readonly int[] _limit = new int[BZip2MaxCodeLen + 2];
        private readonly int[] _base = new int[BZip2MaxCodeLen + 2];
        private readonly int[] _perm = new int[BZip2MaxAlphaSize];
        private readonly int _minLen;
        private readonly int _maxLen;

        private HuffmanTable(int minLen, int maxLen)
        {
            _minLen = minLen;
            _maxLen = maxLen;
        }

        public static HuffmanTable Create(ReadOnlySpan<byte> lengths, int alphaSize)
        {
            int minLen = 32;
            int maxLen = 0;

            for (int i = 0; i < alphaSize; i++)
            {
                if (lengths[i] > maxLen) maxLen = lengths[i];
                if (lengths[i] < minLen) minLen = lengths[i];
            }

            var table = new HuffmanTable(minLen, maxLen);

            int pp = 0;
            for (int i = minLen; i <= maxLen; i++)
            {
                for (int j = 0; j < alphaSize; j++)
                {
                    if (lengths[j] == i)
                    {
                        table._perm[pp++] = j;
                    }
                }
            }

            for (int i = 0; i < table._base.Length; i++) table._base[i] = 0;
            for (int i = 0; i < alphaSize; i++) table._base[lengths[i] + 1]++;
            for (int i = 1; i < table._base.Length; i++) table._base[i] += table._base[i - 1];

            for (int i = 0; i < table._limit.Length; i++) table._limit[i] = -1;
            int vec = 0;

            for (int i = minLen; i <= maxLen; i++)
            {
                vec += (table._base[i + 1] - table._base[i]);
                table._limit[i] = vec - 1;
                vec <<= 1;
            }

            for (int i = minLen + 1; i <= maxLen; i++)
            {
                table._base[i] = ((table._limit[i - 1] + 1) << 1) - table._base[i];
            }

            return table;
        }

        public int DecodeNext(ref BitReader reader)
        {
            int code = 0;
            for (int len = 1; len <= _maxLen; len++)
            {
                code = (code << 1) | (reader.ReadBit() ? 1 : 0);
                if (len >= _minLen && code <= _limit[len])
                {
                    return _perm[code - _base[len]];
                }
            }

            throw new CorruptArchiveException("Código Huffman BZip2 no encontrado en la tabla.");
        }
    }

    private ref struct BitReader(Stream stream)
    {
        private readonly Stream _stream = stream;
        private ulong _buffer = 0;
        private int _bitCount = 0;

        public bool ReadBit() => ReadBits(1) != 0;

        public int ReadBits(int count)
        {
            while (_bitCount < count)
            {
                int b = _stream.ReadByte();
                if (b < 0)
                {
                    throw new CorruptArchiveException("Fin inesperado de flujo BZip2.");
                }
                _buffer = (_buffer << 8) | (byte)b;
                _bitCount += 8;
            }

            int shift = _bitCount - count;
            int result = (int)((_buffer >> shift) & ((1UL << count) - 1));
            _bitCount -= count;
            return result;
        }

        /// <summary>
        /// Descarta el relleno hasta el próximo byte y verifica si sigue otro stream "BZh".
        /// </summary>
        public bool TryAlignToNextStream()
        {
            _bitCount -= _bitCount % 8;
            while (_bitCount < 8)
            {
                int b = _stream.ReadByte();
                if (b < 0)
                {
                    return false;
                }

                _buffer = (_buffer << 8) | (byte)b;
                _bitCount += 8;
            }

            return ((_buffer >> (_bitCount - 8)) & 0xFF) == 'B';
        }

        public ulong ReadBits64(int count)
        {
            if (count <= 32)
            {
                return (ulong)ReadBits(count);
            }

            ulong hi = (ulong)ReadBits(count - 32);
            ulong lo = (ulong)ReadBits(32);
            return (hi << 32) | lo;
        }
    }
}
