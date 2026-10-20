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

using Microsoft.Win32.SafeHandles;

namespace PrZip.Primitives.IO;

/// <summary>
/// Implementación de IRandomAccessSource basada en SafeFileHandle y System.IO.RandomAccess.
/// Permite lectura concurrente multi-hilo sobre el mismo archivo sin interferencia de cursores de posición.
/// </summary>
public sealed class FileRandomAccessSource : IRandomAccessSource
{
    private readonly SafeFileHandle _handle;
    private readonly bool _ownsHandle;
    private readonly long _length;
    private bool _disposed;

    public FileRandomAccessSource(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        _handle = File.OpenHandle(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous);

        _ownsHandle = true;
        _length = RandomAccess.GetLength(_handle);
    }

    public FileRandomAccessSource(SafeFileHandle handle, bool ownsHandle = false)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
        _ownsHandle = ownsHandle;
        _length = RandomAccess.GetLength(_handle);
    }

    public long Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    public int Read(long offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        if (offset < 0 || offset >= _length)
        {
            return 0;
        }

        return RandomAccess.Read(_handle, destination, offset);
    }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (offset < 0 || offset >= _length)
        {
            return ValueTask.FromResult(0);
        }

        return RandomAccess.ReadAsync(_handle, destination, offset, cancellationToken);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsHandle)
            {
                _handle.Dispose();
            }
            _disposed = true;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
