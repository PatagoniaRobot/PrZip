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

namespace PrZip.Codecs.Lzma;

/// <summary>
/// Stream descompresor de lectura que decodifica flujos LZMA de forma incremental y sobre la marcha.
/// Cero dependencias externas y cero búferes intermedios gigantes.
/// </summary>
internal sealed class LzmaStream : Stream
{
    private readonly Stream _compressedStream;
    private readonly LzmaDecoder _decoder;
    private readonly long _unpackSize;
    private long _position;
    private bool _disposed;

    public LzmaStream(Stream compressedStream, LzmaProperties properties, long unpackSize = -1)
    {
        ArgumentNullException.ThrowIfNull(compressedStream);

        _compressedStream = compressedStream;
        _unpackSize = unpackSize;
        _position = 0;

        _decoder = new LzmaDecoder();
        _decoder.SetProperties(properties);
        _decoder.InitStream(compressedStream);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override long Length => _unpackSize >= 0 ? _unpackSize : throw new NotSupportedException("Longitud de stream desconocida.");

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException("LzmaStream es de solo avance (Forward-only) y no admite posicionamiento arbitrario.");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
        {
            return 0;
        }

        // Si conocemos el tamaño límite y ya lo alcanzamos
        if (_unpackSize >= 0 && _position >= _unpackSize)
        {
            return 0;
        }

        Span<byte> target = buffer;
        if (_unpackSize >= 0)
        {
            long remaining = _unpackSize - _position;
            if (remaining < target.Length)
            {
                target = target[..(int)remaining];
            }
        }

        int read = _decoder.DecodeChunk(_compressedStream, target);
        _position += read;
        return read;
    }

    public override void Flush()
    {
        // No-op para stream de solo lectura
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("LzmaStream no admite Seek.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("LzmaStream es de solo lectura.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("LzmaStream es de solo lectura.");

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _compressedStream.Dispose();
            }
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
