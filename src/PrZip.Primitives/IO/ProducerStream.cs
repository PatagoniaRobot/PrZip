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
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace PrZip.Primitives.IO;

/// <summary>
/// Convierte un decodificador que escribe hacia un Stream (estilo "push") en un Stream que se lee
/// a demanda ("pull"). El productor corre en un hilo propio, arrancado en la primera lectura, y entrega
/// bloques por una cola acotada: la memoria en tránsito nunca supera capacidad × tamaño de bloque.
/// Una excepción del productor se relanza en el lector; cerrar el lector antes de tiempo detiene al productor.
/// </summary>
internal sealed class ProducerStream : Stream
{
    private const int ChunkSize = 256 * 1024;
    private const int QueueCapacity = 4;

    private readonly Action<Stream> _producer;
    private readonly string _name;
    private readonly BlockingCollection<Chunk> _queue = new(QueueCapacity);
    private readonly CancellationTokenSource _cancel = new();

    private Thread? _thread;
    private ExceptionDispatchInfo? _error;
    private Chunk _current;
    private int _currentPos;
    private long _position;
    private bool _completed;
    private bool _disposed;

    public ProducerStream(Action<Stream> producer, string name)
    {
        ArgumentNullException.ThrowIfNull(producer);
        _producer = producer;
        _name = name;
    }

    private readonly record struct Chunk(byte[] Buffer, int Count);

    private void EnsureStarted()
    {
        if (_thread != null)
        {
            return;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"7z {_name}"
        };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            using var sink = new ChunkSink(this);
            _producer(sink);
            sink.FlushChunk();
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            // El lector se cerró antes de consumir todo.
        }
        catch (Exception ex)
        {
            _error = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            _queue.CompleteAdding();
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (!FillCurrent())
        {
            return 0;
        }

        int n = Math.Min(buffer.Length, _current.Count - _currentPos);
        _current.Buffer.AsSpan(_currentPos, n).CopyTo(buffer);
        _currentPos += n;
        _position += n;
        return n;
    }

    public override int ReadByte()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!FillCurrent())
        {
            return -1;
        }

        _position++;
        return _current.Buffer[_currentPos++];
    }

    private bool FillCurrent()
    {
        if (_current.Buffer != null && _currentPos < _current.Count)
        {
            return true;
        }

        if (_completed)
        {
            return false;
        }

        EnsureStarted();
        ReturnCurrent();

        if (_queue.TryTake(out Chunk next, Timeout.Infinite))
        {
            _current = next;
            _currentPos = 0;
            return true;
        }

        // Cola cerrada: o terminó bien, o el productor falló.
        _completed = true;
        _error?.Throw();
        return false;
    }

    private void ReturnCurrent()
    {
        if (_current.Buffer != null)
        {
            ArrayPool<byte>.Shared.Return(_current.Buffer);
            _current = default;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _cancel.Cancel();
            ReturnCurrent();

            // Vaciar la cola desbloquea a un productor detenido en Add.
            if (_thread != null)
            {
                while (!_queue.IsCompleted)
                {
                    if (_queue.TryTake(out Chunk c, 50))
                    {
                        ArrayPool<byte>.Shared.Return(c.Buffer);
                    }
                }

                _thread.Join();
            }

            while (_queue.TryTake(out Chunk rest))
            {
                ArrayPool<byte>.Shared.Return(rest.Buffer);
            }

            _queue.Dispose();
            _cancel.Dispose();
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Stream de escritura que recibe el productor: agrupa en bloques de 256 KB y los encola.
    /// </summary>
    private sealed class ChunkSink(ProducerStream owner) : Stream
    {
        private byte[]? _buffer;
        private int _count;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                _buffer ??= ArrayPool<byte>.Shared.Rent(ChunkSize);
                int n = Math.Min(data.Length, ChunkSize - _count);
                data[..n].CopyTo(_buffer.AsSpan(_count));
                _count += n;
                data = data[n..];

                if (_count == ChunkSize)
                {
                    FlushChunk();
                }
            }
        }

        public override void WriteByte(byte value)
        {
            _buffer ??= ArrayPool<byte>.Shared.Rent(ChunkSize);
            _buffer[_count++] = value;
            if (_count == ChunkSize)
            {
                FlushChunk();
            }
        }

        public void FlushChunk()
        {
            if (_buffer == null || _count == 0)
            {
                return;
            }

            var chunk = new Chunk(_buffer, _count);
            _buffer = null;
            _count = 0;

            try
            {
                owner._queue.Add(chunk, owner._cancel.Token);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(chunk.Buffer);
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_buffer != null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
            }

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

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
