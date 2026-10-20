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
using PrZip.Exceptions;
using PrZip.Primitives.Checksum;
using PrZip.Primitives.IO;

namespace PrZip.Container.Headers;

/// <summary>
/// Encabezado de firma de 32 bytes de un archivo 7z.
/// Offset 0 (o tras el ejecutable en archivos SFX).
/// </summary>
internal readonly struct SignatureHeader
{
    public const int HeaderSize = 32;
    public static readonly byte[] MagicBytes = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    public byte MajorVersion { get; }
    public byte MinorVersion { get; }
    public uint StartHeaderCrc { get; }
    public long NextHeaderOffset { get; }
    public long NextHeaderSize { get; }
    public uint NextHeaderCrc { get; }
    public long BaseOffset { get; } // Offset donde comienza este SignatureHeader en el archivo

    public SignatureHeader(
        byte majorVersion,
        byte minorVersion,
        uint startHeaderCrc,
        long nextHeaderOffset,
        long nextHeaderSize,
        uint nextHeaderCrc,
        long baseOffset = 0)
    {
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        StartHeaderCrc = startHeaderCrc;
        NextHeaderOffset = nextHeaderOffset;
        NextHeaderSize = nextHeaderSize;
        NextHeaderCrc = nextHeaderCrc;
        BaseOffset = baseOffset;
    }

    /// <summary>
    /// Lee y valida la cabecera de firma de 32 bytes desde un origen de acceso aleatorio.
    /// Soporta detección de archivos estándar y ejecutables SFX.
    /// </summary>
    public static SignatureHeader Read(IRandomAccessSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Length < HeaderSize)
        {
            throw new ArchiveTruncatedException($"El archivo es demasiado pequeño para ser un archivo 7z ({source.Length} bytes).");
        }

        Span<byte> buffer = stackalloc byte[HeaderSize];

        // 1. Probar en offset 0 (caso estándar)
        source.ReadExact(0, buffer);
        if (buffer[..6].SequenceEqual(MagicBytes))
        {
            return ParseAndValidate(buffer, baseOffset: 0);
        }

        // 2. Búsqueda SFX (escaneo de hasta 1 MB por cabecera incrustada)
        const int sfxScanLimit = 1024 * 1024;
        int maxScan = (int)Math.Min(source.Length - HeaderSize, (long)sfxScanLimit);
        byte[] scanChunk = new byte[4096];

        for (int offset = 0; offset < maxScan; offset += scanChunk.Length - 6)
        {
            int toRead = Math.Min(scanChunk.Length, (int)(source.Length - offset));
            source.Read(offset, scanChunk.AsSpan(0, toRead));

            for (int i = 0; i <= toRead - HeaderSize; i++)
            {
                if (scanChunk.AsSpan(i, 6).SequenceEqual(MagicBytes))
                {
                    long candidateOffset = offset + i;
                    source.ReadExact(candidateOffset, buffer);

                    // Verificar CRC de la cabecera candidata para evitar falsos positivos
                    uint nextHeaderCrcComputed = Crc32Algorithm.Compute(buffer.Slice(12, 20));
                    uint startHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4));

                    if (nextHeaderCrcComputed == startHeaderCrc)
                    {
                        return ParseAndValidate(buffer, candidateOffset);
                    }
                }
            }
        }

        throw new CorruptArchiveException("La firma mágica de 7z (0x37 0x7A 0xBC 0xAF 0x27 0x1C) no fue encontrada.");
    }

    private static SignatureHeader ParseAndValidate(ReadOnlySpan<byte> buffer, long baseOffset)
    {
        byte major = buffer[6];
        byte minor = buffer[7];

        if (major != 0)
        {
            throw new UnsupportedMethodException($"Versión mayor de 7z no compatible ({major}.{minor}). Se requiere versión 0.x.");
        }

        uint startHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4));
        uint computedCrc = Crc32Algorithm.Compute(buffer.Slice(12, 20));

        if (startHeaderCrc != computedCrc)
        {
            throw new CorruptArchiveException(
                $"CRC de StartHeader inválido: esperado 0x{startHeaderCrc:X8}, calculado 0x{computedCrc:X8}.");
        }

        long nextHeaderOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(12, 8));
        long nextHeaderSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(20, 8));
        uint nextHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(28, 4));

        return new SignatureHeader(major, minor, startHeaderCrc, nextHeaderOffset, nextHeaderSize, nextHeaderCrc, baseOffset);
    }

    /// <summary>
    /// Escribe los 32 bytes de la cabecera de firma en el búfer de destino.
    /// </summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException($"Se requieren {HeaderSize} bytes para escribir SignatureHeader.", nameof(destination));
        }

        MagicBytes.CopyTo(destination[..6]);
        destination[6] = MajorVersion;
        destination[7] = MinorVersion;

        // Escribir campos de offset, size y CRC en bytes 12..31
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(12, 8), (ulong)NextHeaderOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(20, 8), (ulong)NextHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(28, 4), NextHeaderCrc);

        // Calcular y escribir CRC de bytes 12..31 en offset 8..11
        uint startHeaderCrc = Crc32Algorithm.Compute(destination.Slice(12, 20));
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(8, 4), startHeaderCrc);
    }
}
