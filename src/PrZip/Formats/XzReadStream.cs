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
using System.Security.Cryptography;
using PrZip.Container.Extraction;
using PrZip.Container.Headers;
using PrZip.Exceptions;
using PrZip.Primitives.Checksum;
using PrZip.Primitives.IO;

namespace PrZip.Formats;

/// <summary>
/// Lectura de un archivo .xz (formato XZ 1.x, especificación de dominio público de Tukaani). Antes de
/// descomprimir recorre desde el final los pies e índices de cada stream: así conoce los límites exactos de
/// cada bloque, admite streams concatenados y el relleno entre ellos, y no depende de que el
/// descompresor se detenga en el byte justo. Cada bloque se decodifica con el LZMA2 y los filtros del motor
/// (el mismo grafo de coders que en 7z) y se verifica contra su CRC32, CRC64 o SHA-256.
/// </summary>
internal sealed class XzReadStream : Stream
{
    private static readonly byte[] HeaderMagic = [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00];
    private const int MaxFilters = 4;

    private readonly IRandomAccessSource _source;
    private readonly bool _ownsSource;
    private readonly List<BlockInfo> _blocks;
    private int _nextBlock;

    private Stream? _decoded;
    private BlockInfo _current;
    private long _currentRead;
    private IncrementalHash? _sha256;
    private uint _crc32;
    private ulong _crc64;
    private bool _disposed;

    public XzReadStream(IRandomAccessSource source, bool ownsSource = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _ownsSource = ownsSource;
        try
        {
            _blocks = ReadIndexes(source);
        }
        catch
        {
            // Si el archivo está dañado no hay stream: el origen se cierra acá o queda abierto para siempre.
            if (ownsSource)
            {
                source.Dispose();
            }

            throw;
        }
    }

    /// <summary>Tamaño descomprimido total, según los índices.</summary>
    public long UncompressedLength => _blocks.Sum(b => b.UncompressedSize);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!buffer.IsEmpty)
        {
            if (_decoded == null)
            {
                if (_nextBlock >= _blocks.Count)
                {
                    return 0;
                }

                OpenBlock(_blocks[_nextBlock++]);
            }

            long remaining = _current.UncompressedSize - _currentRead;
            int toRead = (int)Math.Min(buffer.Length, Math.Max(remaining, 1));
            int n = _decoded!.Read(buffer[..toRead]);
            if (n > 0)
            {
                if (n > remaining)
                {
                    throw new CorruptArchiveException("XZ: el bloque produce más datos de los que declara el índice.");
                }

                UpdateCheck(buffer[..n]);
                _currentRead += n;
                return n;
            }

            CloseBlock();
        }

        return 0;
    }

    private void OpenBlock(BlockInfo block)
    {
        _current = block;
        _currentRead = 0;
        _crc32 = 0;
        _crc64 = 0;
        _sha256?.Dispose();
        _sha256 = block.CheckType == 10 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

        // Cabecera del bloque: tamaño, banderas, tamaños opcionales, filtros, relleno y CRC32.
        byte sizeByte = ReadByteAt(block.Offset);
        if (sizeByte == 0)
        {
            throw new CorruptArchiveException("XZ: el índice apunta a un bloque inexistente.");
        }

        int headerSize = (sizeByte + 1) * 4;
        var header = new byte[headerSize];
        _source.ReadExact(block.Offset, header);
        if (Crc32Algorithm.Compute(header.AsSpan(0, headerSize - 4)) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(headerSize - 4)))
        {
            throw new CorruptArchiveException("XZ: CRC de la cabecera de bloque inválido.");
        }

        byte flags = header[1];
        if ((flags & 0x3C) != 0)
        {
            throw new UnsupportedMethodException("XZ: banderas de bloque reservadas.");
        }

        int filterCount = (flags & 0x03) + 1;
        int pos = 2;
        long? compressedSize = null, uncompressedSize = null;
        if ((flags & 0x40) != 0)
        {
            compressedSize = ReadVli(header, ref pos, headerSize - 4);
        }

        if ((flags & 0x80) != 0)
        {
            uncompressedSize = ReadVli(header, ref pos, headerSize - 4);
        }

        var filters = new List<(ulong Id, byte[] Props)>(filterCount);
        for (int i = 0; i < filterCount; i++)
        {
            ulong id = (ulong)ReadVli(header, ref pos, headerSize - 4);
            long propsSize = ReadVli(header, ref pos, headerSize - 4);
            if (propsSize > headerSize - 4 - pos)
            {
                throw new CorruptArchiveException("XZ: propiedades de filtro fuera de la cabecera.");
            }

            filters.Add((id, header.AsSpan(pos, (int)propsSize).ToArray()));
            pos += (int)propsSize;
        }

        for (; pos < headerSize - 4; pos++)
        {
            if (header[pos] != 0)
            {
                throw new CorruptArchiveException("XZ: relleno de cabecera de bloque no nulo.");
            }
        }

        long dataSize = block.UnpaddedSize - headerSize - CheckSize(block.CheckType);
        if (dataSize <= 0 || (compressedSize.HasValue && compressedSize != dataSize)
            || (uncompressedSize.HasValue && uncompressedSize != block.UncompressedSize))
        {
            throw new CorruptArchiveException("XZ: la cabecera del bloque no coincide con el índice.");
        }

        _current = block with { HeaderSize = headerSize, DataSize = dataSize };
        var folder = BuildFolder(filters, block.UncompressedSize);
        var packed = new SourceWindowStream(_source, block.Offset + headerSize, dataSize);
        _decoded = FolderDecoder.Open(folder, [packed]);
    }

    private void CloseBlock()
    {
        _decoded!.Dispose();
        _decoded = null;
        if (_currentRead != _current.UncompressedSize)
        {
            throw new CorruptArchiveException(
                $"XZ: el bloque produjo {_currentRead} bytes de {_current.UncompressedSize} declarados.");
        }

        // La verificación va después de los datos, alineada a 4 bytes.
        long checkOffset = _current.Offset + _current.HeaderSize + ((_current.DataSize + 3) & ~3L);
        for (long p = _current.Offset + _current.HeaderSize + _current.DataSize; p < checkOffset; p++)
        {
            if (ReadByteAt(p) != 0)
            {
                throw new CorruptArchiveException("XZ: relleno de bloque no nulo.");
            }
        }

        int checkSize = CheckSize(_current.CheckType);
        var stored = new byte[checkSize];
        _source.ReadExact(checkOffset, stored);
        bool ok = _current.CheckType switch
        {
            0 => true,
            1 => BinaryPrimitives.ReadUInt32LittleEndian(stored) == _crc32,
            4 => BinaryPrimitives.ReadUInt64LittleEndian(stored) == _crc64,
            10 => stored.AsSpan().SequenceEqual(_sha256!.GetHashAndReset()),
            _ => true // tipos que el formato reserva: se respeta su tamaño pero no se pueden calcular
        };

        if (!ok)
        {
            throw new CorruptArchiveException("XZ: la verificación del bloque no coincide (datos dañados).");
        }
    }

    private void UpdateCheck(ReadOnlySpan<byte> data)
    {
        switch (_current.CheckType)
        {
            case 1:
                _crc32 = Crc32Algorithm.Update(_crc32, data);
                break;
            case 4:
                _crc64 = Crc64Algorithm.Update(_crc64, data);
                break;
            case 10:
                _sha256!.AppendData(data);
                break;
        }
    }

    /// <summary>Grafo de decodificación: LZMA2 primero y después los filtros en orden inverso.</summary>
    private static Folder BuildFolder(List<(ulong Id, byte[] Props)> filters, long uncompressedSize)
    {
        if (filters[^1].Id != 0x21)
        {
            throw new UnsupportedMethodException($"XZ: el último filtro debe ser LZMA2 y es 0x{filters[^1].Id:X}.");
        }

        var folder = new Folder();
        for (int i = filters.Count - 1; i >= 0; i--)
        {
            var (id, props) = filters[i];
            if (i < filters.Count - 1 && id == 0x21)
            {
                throw new CorruptArchiveException("XZ: LZMA2 solo puede ir al final de la cadena.");
            }

            byte[] method = id switch
            {
                0x21 => MethodId.Lzma2,
                0x03 => MethodId.Delta,
                0x04 => MethodId.BcjX86,
                0x05 => MethodId.BcjPpc,
                0x06 => MethodId.BcjIa64,
                0x07 => MethodId.BcjArm,
                0x08 => MethodId.BcjArmThumb,
                0x09 => MethodId.BcjSparc,
                0x0A => MethodId.BcjArm64,
                0x0B => MethodId.BcjRiscV,
                _ => throw new UnsupportedMethodException($"XZ: filtro 0x{id:X} no soportado.")
            };

            folder.Coders.Add(new CoderInfo { MethodId = method, Properties = props.Length > 0 ? props : null });
            folder.UnpackSizes.Add(uncompressedSize);
        }

        for (int i = 1; i < folder.Coders.Count; i++)
        {
            folder.BindPairs.Add(new BindPair(i, i - 1));
        }

        folder.PackedStreamIndices.Add(0);
        return folder;
    }

    /// <summary>
    /// Recorre el archivo desde el final: relleno entre streams, pie, índice y cabecera de cada uno. Devuelve
    /// los bloques en orden, con su posición, tamaño sin relleno, tamaño descomprimido y tipo de verificación.
    /// </summary>
    private static List<BlockInfo> ReadIndexes(IRandomAccessSource source)
    {
        var all = new List<BlockInfo>();
        long end = source.Length;
        Span<byte> four = stackalloc byte[4];
        Span<byte> footer = stackalloc byte[12];

        while (end > 0)
        {
            // Relleno de stream: ceros en múltiplos de 4.
            while (true)
            {
                if (end < 12 + 12)
                {
                    throw new CorruptArchiveException("XZ: archivo truncado o sin pie de stream.");
                }

                source.ReadExact(end - 4, four);
                if (BinaryPrimitives.ReadUInt32LittleEndian(four) != 0)
                {
                    break;
                }

                end -= 4;
            }

            source.ReadExact(end - 12, footer);
            if (footer[10] != (byte)'Y' || footer[11] != (byte)'Z')
            {
                throw new CorruptArchiveException("XZ: pie de stream inválido.");
            }

            if (Crc32Algorithm.Compute(footer[4..10]) != BinaryPrimitives.ReadUInt32LittleEndian(footer))
            {
                throw new CorruptArchiveException("XZ: CRC del pie de stream inválido.");
            }

            long indexSize = ((long)BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) + 1) * 4;
            byte footerFlags0 = footer[8], footerFlags1 = footer[9];
            int checkType = footerFlags1 & 0x0F;
            if (footerFlags0 != 0 || (footerFlags1 & 0xF0) != 0)
            {
                throw new UnsupportedMethodException("XZ: banderas de stream desconocidas.");
            }

            long indexStart = end - 12 - indexSize;
            if (indexStart < 12)
            {
                throw new CorruptArchiveException("XZ: índice fuera del archivo.");
            }

            var index = new byte[indexSize];
            source.ReadExact(indexStart, index);
            if (index[0] != 0 || Crc32Algorithm.Compute(index.AsSpan(0, index.Length - 4)) != BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(index.Length - 4)))
            {
                throw new CorruptArchiveException("XZ: índice inválido.");
            }

            int pos = 1;
            long records = ReadVli(index, ref pos, index.Length - 4);
            if (records > (index.Length - 4) / 2)
            {
                throw new CorruptArchiveException("XZ: el índice declara más bloques de los que puede describir.");
            }

            var streamBlocks = new List<BlockInfo>((int)records);
            long totalPadded = 0;
            for (long r = 0; r < records; r++)
            {
                long unpadded = ReadVli(index, ref pos, index.Length - 4);
                long uncompressed = ReadVli(index, ref pos, index.Length - 4);
                if (unpadded < 5 || unpadded > end)
                {
                    throw new CorruptArchiveException("XZ: tamaño de bloque inválido en el índice.");
                }

                streamBlocks.Add(new BlockInfo(totalPadded, unpadded, uncompressed, checkType, 0, 0));
                totalPadded += (unpadded + 3) & ~3L;
            }

            for (; pos < index.Length - 4; pos++)
            {
                if (index[pos] != 0)
                {
                    throw new CorruptArchiveException("XZ: relleno de índice no nulo.");
                }
            }

            long streamStart = indexStart - totalPadded - 12;
            if (streamStart < 0)
            {
                throw new CorruptArchiveException("XZ: los bloques del índice no caben en el archivo.");
            }

            var header = new byte[12];
            source.ReadExact(streamStart, header);
            if (!header.AsSpan(0, 6).SequenceEqual(HeaderMagic)
                || header[6] != footerFlags0 || header[7] != footerFlags1
                || Crc32Algorithm.Compute(header.AsSpan(6, 2)) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)))
            {
                throw new CorruptArchiveException("XZ: cabecera de stream inválida o distinta del pie.");
            }

            for (int i = 0; i < streamBlocks.Count; i++)
            {
                streamBlocks[i] = streamBlocks[i] with { Offset = streamStart + 12 + streamBlocks[i].Offset };
            }

            all.InsertRange(0, streamBlocks);
            end = streamStart;
        }

        return all;
    }

    private static int CheckSize(int checkType) => checkType switch
    {
        0 => 0,
        <= 3 => 4,
        <= 6 => 8,
        <= 9 => 16,
        <= 12 => 32,
        _ => 64
    };

    /// <summary>Entero de largo variable de XZ: 7 bits por byte, hasta 9 bytes, sin ceros de más al final.</summary>
    private static long ReadVli(byte[] data, ref int pos, int limit)
    {
        ulong value = 0;
        for (int i = 0; i < 9; i++)
        {
            if (pos >= limit)
            {
                throw new CorruptArchiveException("XZ: entero truncado.");
            }

            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                if (b == 0 && i > 0)
                {
                    throw new CorruptArchiveException("XZ: entero con ceros de más.");
                }

                if (value > long.MaxValue)
                {
                    throw new CorruptArchiveException("XZ: entero fuera de rango.");
                }

                return (long)value;
            }
        }

        throw new CorruptArchiveException("XZ: entero demasiado largo.");
    }

    private byte ReadByteAt(long offset)
    {
        Span<byte> one = stackalloc byte[1];
        _source.ReadExact(offset, one);
        return one[0];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _decoded?.Dispose();
            _sha256?.Dispose();
            if (_ownsSource)
            {
                _source.Dispose();
            }
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private readonly record struct BlockInfo(long Offset, long UnpaddedSize, long UncompressedSize, int CheckType, int HeaderSize, long DataSize);
}
