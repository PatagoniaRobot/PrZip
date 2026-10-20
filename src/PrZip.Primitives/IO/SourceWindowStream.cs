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

namespace PrZip.Primitives.IO;

/// <summary>
/// Stream de solo lectura sobre un rango [offset, offset + length) de un IRandomAccessSource.
/// No copia datos: cada Read es una lectura posicional, de modo que varias ventanas sobre la
/// misma fuente pueden leerse en paralelo desde hilos distintos.
/// </summary>
internal sealed class SourceWindowStream : Stream
{
    private readonly IRandomAccessSource _source;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public SourceWindowStream(IRandomAccessSource source, long offset, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _source = source;
        _start = offset;
        _length = length;
    }

    // Lectura anticipada: los decodificadores de bits (BZip2, BCJ2) leen byte a byte, y sin búfer
    // cada ReadByte sería una lectura posicional al disco.
    private readonly byte[] _readAhead = new byte[1 << 16];
    private int _aheadPos;
    private int _aheadCount;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_aheadPos == _aheadCount)
        {
            // Pedidos grandes van directo al destino, sin pasar por el búfer.
            if (buffer.Length >= _readAhead.Length)
            {
                return ReadFromSource(buffer);
            }

            _aheadCount = ReadFromSource(_readAhead);
            _aheadPos = 0;
            if (_aheadCount == 0)
            {
                return 0;
            }
        }

        int n = Math.Min(buffer.Length, _aheadCount - _aheadPos);
        _readAhead.AsSpan(_aheadPos, n).CopyTo(buffer);
        _aheadPos += n;
        return n;
    }

    public override int ReadByte()
    {
        if (_aheadPos == _aheadCount)
        {
            _aheadCount = ReadFromSource(_readAhead);
            _aheadPos = 0;
            if (_aheadCount == 0)
            {
                return -1;
            }
        }

        return _readAhead[_aheadPos++];
    }

    private int ReadFromSource(Span<byte> buffer)
    {
        long remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        int toRead = (int)Math.Min(buffer.Length, remaining);
        int read = _source.Read(_start + _position, buffer[..toRead]);
        if (read == 0)
        {
            throw new Exceptions.ArchiveTruncatedException(
                $"Fin prematuro del archivo: el stream empaquetado en el offset {_start} declara {_length} bytes y se cortó en {_position}.");
        }

        _position += read;
        return read;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position - (_aheadCount - _aheadPos);
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
