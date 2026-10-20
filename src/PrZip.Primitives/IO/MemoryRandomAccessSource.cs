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
/// Implementación de IRandomAccessSource basada en ReadOnlyMemory&lt;byte&gt;.
/// </summary>
public sealed class MemoryRandomAccessSource : IRandomAccessSource
{
    private readonly ReadOnlyMemory<byte> _memory;

    public MemoryRandomAccessSource(ReadOnlyMemory<byte> memory)
    {
        _memory = memory;
    }

    public MemoryRandomAccessSource(byte[] bytes) : this(bytes.AsMemory())
    {
    }

    public long Length => _memory.Length;

    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= _memory.Length)
        {
            return 0;
        }

        int available = (int)Math.Min((long)destination.Length, _memory.Length - offset);
        _memory.Span.Slice((int)offset, available).CopyTo(destination);
        return available;
    }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(offset, destination.Span));
    }

    public void Dispose()
    {
        // No hay recursos nativos no administrados que liberar
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
