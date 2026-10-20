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

namespace PrZip.Primitives.Memory;

/// <summary>
/// Controlador de presupuesto de memoria para prevenir ataques de agotamiento de recursos (bombas de descompresión).
/// Realiza seguimiento seguro entre hilos del consumo de RAM antes de alocar.
/// </summary>
internal sealed class MemoryBudget
{
    private readonly long _maxMemoryBytes;
    private long _currentUsageBytes;

    /// <summary>
    /// Presupuesto por defecto: 1 GiB de memoria de trabajo.
    /// </summary>
    public const long DefaultMaxMemory = 1024L * 1024L * 1024L;

    public MemoryBudget(long maxMemoryBytes = DefaultMaxMemory)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMemoryBytes);
        _maxMemoryBytes = maxMemoryBytes;
        _currentUsageBytes = 0;
    }

    public long MaxMemoryBytes => _maxMemoryBytes;

    public long CurrentUsageBytes => Volatile.Read(ref _currentUsageBytes);

    /// <summary>
    /// Intenta reservar memoria contra el presupuesto. Lanza LimitExceededException si supera el tope.
    /// </summary>
    public void Acquire(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes == 0)
        {
            return;
        }

        while (true)
        {
            long current = Volatile.Read(ref _currentUsageBytes);
            long updated = checked(current + bytes);

            if (updated > _maxMemoryBytes)
            {
                throw new LimitExceededException(
                    $"Se superó el presupuesto máximo de memoria permitido: se solicitaron {bytes:N0} bytes, " +
                    $"uso actual: {current:N0} bytes, límite máximo: {_maxMemoryBytes:N0} bytes.");
            }

            if (Interlocked.CompareExchange(ref _currentUsageBytes, updated, current) == current)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Libera memoria previamente adquirida.
    /// </summary>
    public void Release(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes == 0)
        {
            return;
        }

        while (true)
        {
            long current = Volatile.Read(ref _currentUsageBytes);
            long updated = Math.Max(0, current - bytes);

            if (Interlocked.CompareExchange(ref _currentUsageBytes, updated, current) == current)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Asigna un NativeBuffer de tipo T verificando y reservando previamente el presupuesto de memoria.
    /// Al liberar el NativeBuffer, la memoria en el presupuesto debe ser liberada.
    /// </summary>
    public unsafe NativeBuffer<T> AllocateBuffer<T>(nuint length, nuint alignment = NativeBuffer<T>.DefaultAlignment) where T : unmanaged
    {
        long bytesRequired = checked((long)(length * (nuint)sizeof(T)));
        Acquire(bytesRequired);

        try
        {
            return new NativeBuffer<T>(length, alignment);
        }
        catch
        {
            Release(bytesRequired);
            throw;
        }
    }
}
