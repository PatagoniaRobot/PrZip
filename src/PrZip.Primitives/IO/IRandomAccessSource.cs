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

namespace PrZip.Primitives.IO;

/// <summary>
/// Abstracción de acceso aleatorio posicional concurrente para flujos de datos.
/// Permite leer desde múltiples hilos y posiciones independientes sin alterar cursores compartidos.
/// </summary>
public interface IRandomAccessSource : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Longitud total del origen de datos en bytes.
    /// </summary>
    long Length { get; }

    /// <summary>
    /// Lee datos de manera síncrona desde un offset absoluto hacia el buffer de destino.
    /// </summary>
    int Read(long offset, Span<byte> destination);

    /// <summary>
    /// Lee datos de manera asíncrona desde un offset absoluto hacia el buffer de destino.
    /// </summary>
    ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default);
}

/// <summary>
/// Métodos de extensión para IRandomAccessSource.
/// </summary>
public static class RandomAccessSourceExtensions
{
    /// <summary>
    /// Lee exactamente la cantidad de bytes requerida por 'destination'. Lanza ArchiveTruncatedException si el stream finaliza prematuramente.
    /// </summary>
    public static void ReadExact(this IRandomAccessSource source, long offset, Span<byte> destination)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = source.Read(offset + totalRead, destination[totalRead..]);
            if (read == 0)
            {
                throw new ArchiveTruncatedException(
                    $"Fin prematuro del archivo: se esperaban {destination.Length} bytes desde el offset {offset}, pero solo se leyeron {totalRead}.");
            }
            totalRead += read;
        }
    }

    /// <summary>
    /// Lee exactamente la cantidad de bytes requerida por 'destination' de manera asíncrona.
    /// </summary>
    public static async ValueTask ReadExactAsync(
        this IRandomAccessSource source,
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = await source.ReadAsync(offset + totalRead, destination[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new ArchiveTruncatedException(
                    $"Fin prematuro del archivo: se esperaban {destination.Length} bytes desde el offset {offset}, pero solo se leyeron {totalRead}.");
            }
            totalRead += read;
        }
    }
}
