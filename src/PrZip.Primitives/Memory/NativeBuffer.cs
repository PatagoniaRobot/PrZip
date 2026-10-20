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

namespace PrZip.Primitives.Memory;

/// <summary>
/// Buffer de memoria no administrada de alto rendimiento alineado para SIMD.
/// Permite indexación con 'nuint' superando el límite de 2 GB de arrays administrados
/// y evita por completo la saturación del Large Object Heap (LOH) del Garbage Collector.
/// </summary>
internal sealed unsafe class NativeBuffer<T> : IDisposable where T : unmanaged
{
    private void* _pointer;
    private readonly nuint _length;
    private readonly nuint _byteLength;
    private readonly nuint _alignment;
    private bool _disposed;

    public const nuint DefaultAlignment = 64; // Alineado a 64 bytes para AVX-512 y líneas de caché

    public NativeBuffer(nuint length, nuint alignment = DefaultAlignment, bool zeroInitialize = true)
    {
        if (length == 0)
        {
            _pointer = null;
            _length = 0;
            _byteLength = 0;
            _alignment = alignment;
            return;
        }

        _length = length;
        _byteLength = checked(length * (nuint)sizeof(T));
        _alignment = alignment;

        _pointer = NativeMemory.AlignedAlloc(_byteLength, _alignment);

        if (zeroInitialize)
        {
            NativeMemory.Clear(_pointer, _byteLength);
        }
    }

    public NativeBuffer(long length, nuint alignment = DefaultAlignment, bool zeroInitialize = true)
        : this((nuint)length, alignment, zeroInitialize)
    {
    }

    ~NativeBuffer()
    {
        DisposeCore();
    }

    public void* RawPointer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfDisposed();
            return _pointer;
        }
    }

    public T* TypedPointer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfDisposed();
            return (T*)_pointer;
        }
    }

    public nuint Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _length;
    }

    public nuint ByteLength
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _byteLength;
    }

    public ref T this[nuint index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ThrowIfDisposed();
            if (index >= _length)
            {
                ThrowIndexOutOfRange(index);
            }

            return ref ((T*)_pointer)[index];
        }
    }

    public ref T this[long index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref this[(nuint)index];
    }

    /// <summary>
    /// Retorna una vista Span si la longitud cabe dentro de un int (hasta 2.147.483.647 elementos).
    /// </summary>
    public Span<T> AsSpan()
    {
        ThrowIfDisposed();
        if (_length > int.MaxValue)
        {
            throw new InvalidOperationException($"La longitud ({_length}) excede int.MaxValue y no puede representarse como Span.");
        }

        return new Span<T>(_pointer, (int)_length);
    }

    /// <summary>
    /// Retorna una vista Span para un segmento específico.
    /// </summary>
    public Span<T> Slice(int start, int length)
    {
        ThrowIfDisposed();
        return AsSpan().Slice(start, length);
    }

    public void Clear()
    {
        ThrowIfDisposed();
        if (_pointer != null && _byteLength > 0)
        {
            NativeMemory.Clear(_pointer, _byteLength);
        }
    }

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (!_disposed)
        {
            if (_pointer != null)
            {
                NativeMemory.AlignedFree(_pointer);
                _pointer = null;
            }
            _disposed = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ThrowIndexOutOfRange(nuint index)
    {
        throw new ArgumentOutOfRangeException(nameof(index), $"Índice {index} fuera de rango. Longitud del buffer: {_length}.");
    }
}
