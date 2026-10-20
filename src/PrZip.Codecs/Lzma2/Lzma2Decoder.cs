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
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using PrZip.Codecs.Lzma;
using PrZip.Exceptions;

namespace PrZip.Codecs.Lzma2;

/// <summary>
/// Decodificador del algoritmo LZMA2 basado en la máquina de estados canónica de Igor Pavlov (Lzma2Dec.c).
/// Controla chunks sin comprimir, chunks comprimidos con reinicio de estado, propiedades y diccionario.
/// </summary>
internal sealed class Lzma2Decoder
{
    private readonly LzmaDecoder _lzmaDecoder = new();
    private readonly byte[] _chunkBuffer = new byte[65536];

    private uint _dictSize;
    private bool _needInitDict = true;
    private bool _needInitState = true;
    private bool _needInitProps = true;

    private LzmaProperties _currentProps;

    public Lzma2Decoder()
    {
    }

    /// <summary>
    /// Configura el tamaño del diccionario para el flujo LZMA2.
    /// </summary>
    public void SetDictionarySize(uint dictSize)
    {
        _dictSize = Math.Max(dictSize, 1u << 12);

        // El diccionario se reserva ya: un stream puede empezar con un chunk sin comprimir (0x01)
        // cuyos bytes tienen que quedar en la historia para los chunks LZMA siguientes.
        _lzmaDecoder.SetProperties(new LzmaProperties(3, 0, 2, _dictSize));
        Reset();
    }

    /// <summary>
    /// Reinicia la máquina de estados para comenzar a procesar un nuevo stream LZMA2.
    /// </summary>
    public void Reset()
    {
        _needInitDict = true;
        _needInitState = true;
        _needInitProps = true;
    }

    /// <summary>
    /// Decodifica un flujo LZMA2 completo desde 'inStream' hacia 'outStream'.
    /// Si 'unpackSize' &gt;= 0, se detiene al alcanzar dicho tamaño.
    /// </summary>
    public void Decode(Stream inStream, Stream outStream, long unpackSize)
    {
        ArgumentNullException.ThrowIfNull(inStream);
        ArgumentNullException.ThrowIfNull(outStream);

        long totalWritten = 0;

        while (unpackSize < 0 || totalWritten < unpackSize)
        {
            int controlByte = inStream.ReadByte();
            if (controlByte < 0 || controlByte == 0x00)
            {
                // Fin de flujo LZMA2 (0x00 o EOF)
                break;
            }

            if (controlByte > 0x02 && controlByte < 0x80)
            {
                throw new CorruptArchiveException($"Byte de control LZMA2 inválido: 0x{controlByte:X2}.");
            }

            if (controlByte is 0x01 or 0x02)
            {
                // 1. Chunk sin comprimir (hasta 64 KB)
                int b0 = inStream.ReadByte();
                int b1 = inStream.ReadByte();
                if (b0 < 0 || b1 < 0)
                {
                    throw new ArchiveTruncatedException("Flujo truncado al leer longitud de chunk sin comprimir LZMA2.");
                }

                int chunkSize = ((b0 << 8) | b1) + 1;

                if (controlByte == 0x01)
                {
                    // Reset de diccionario
                    _lzmaDecoder.Reset();
                    _needInitDict = false;
                }
                else if (_needInitDict)
                {
                    throw new CorruptArchiveException("El primer chunk de LZMA2 debe reiniciar el diccionario.");
                }

                // Copiar datos sin comprimir al diccionario y a la salida
                int readTotal = 0;
                while (readTotal < chunkSize)
                {
                    int toRead = Math.Min(chunkSize - readTotal, _chunkBuffer.Length);
                    int read = inStream.Read(_chunkBuffer, 0, toRead);
                    if (read == 0)
                    {
                        throw new ArchiveTruncatedException("Flujo truncado dentro de chunk sin comprimir LZMA2.");
                    }

                    outStream.Write(_chunkBuffer, 0, read);
                    _lzmaDecoder.PutBytes(_chunkBuffer.AsSpan(0, read));
                    readTotal += read;
                    totalWritten += read;
                }

                // Regla de Lzma2Dec.c (needInitLevel): solo tras un 0x01 el siguiente chunk LZMA debe
                // traer estado y propiedades nuevos (>= 0xC0). Tras un 0x02 el modelo continúa.
                if (controlByte == 0x01)
                {
                    _needInitState = true;
                    _needInitProps = true;
                }
            }
            else
            {
                // 2. Chunk comprimido LZMA (0x80 a 0xFF)
                int resetMode = (controlByte >> 5) & 3;

                int u0 = inStream.ReadByte();
                int u1 = inStream.ReadByte();
                int p0 = inStream.ReadByte();
                int p1 = inStream.ReadByte();

                if (u0 < 0 || u1 < 0 || p0 < 0 || p1 < 0)
                {
                    throw new ArchiveTruncatedException("Flujo truncado al leer cabecera de chunk comprimido LZMA2.");
                }

                int chunkUnpackSize = (((controlByte & 0x1F) << 16) | (u0 << 8) | u1) + 1;
                int chunkPackSize = ((p0 << 8) | p1) + 1;

                if (resetMode >= 2)
                {
                    // Trae nuevas propiedades
                    int propByte = inStream.ReadByte();
                    if (propByte < 0)
                    {
                        throw new ArchiveTruncatedException("Flujo truncado al leer byte de propiedades de LZMA2.");
                    }

                    // pb = propByte / 45: un byte >= 225 daría pb > 4 (datos dañados o contraseña incorrecta).
                    if (propByte >= 9 * 5 * 5)
                    {
                        throw new CorruptArchiveException($"Byte de propiedades LZMA2 inválido: 0x{propByte:X2}.");
                    }

                    int lc = propByte % 9;
                    int remainder = propByte / 9;
                    int lp = remainder % 5;
                    int pb = remainder / 5;

                    if (lc + lp > 4)
                    {
                        throw new CorruptArchiveException($"Propiedades LZMA2 inválidas: lc + lp ({lc} + {lp}) > 4.");
                    }

                    // Sin tocar el diccionario: con 0xC0 la historia se conserva.
                    _currentProps = new LzmaProperties(lc, lp, pb, _dictSize);
                    _lzmaDecoder.SetLiteralProperties(lc, lp, pb);
                    _needInitProps = false;
                }

                if (_needInitProps)
                {
                    throw new CorruptArchiveException("El primer chunk LZMA de un flujo LZMA2 debe proporcionar propiedades.");
                }

                if (resetMode == 3)
                {
                    // Reset de diccionario y estado
                    _lzmaDecoder.Reset();
                    _needInitDict = false;
                    _needInitState = false;
                }
                else
                {
                    if (_needInitDict)
                    {
                        throw new CorruptArchiveException("El primer chunk de LZMA2 debe reiniciar el diccionario.");
                    }

                    if (resetMode >= 1)
                    {
                        // Reset de estado del modelo únicamente (preservando el diccionario histórico)
                        _lzmaDecoder.ResetState();
                        _needInitState = false;
                    }
                    else if (_needInitState)
                    {
                        throw new CorruptArchiveException("El chunk LZMA requiere reinicio de estado tras un chunk sin comprimir.");
                    }
                }

                // Leer exactamente los 'chunkPackSize' bytes del stream empaquetado
                byte[] packBuffer = ArrayPool<byte>.Shared.Rent(chunkPackSize);
                try
                {
                    if (inStream.ReadAtLeast(packBuffer.AsSpan(0, chunkPackSize), chunkPackSize, throwOnEndOfStream: false) < chunkPackSize)
                    {
                        throw new ArchiveTruncatedException($"Flujo LZMA2 truncado: el chunk declara {chunkPackSize} bytes comprimidos.");
                    }

                    using var chunkInStream = new MemoryStream(packBuffer, 0, chunkPackSize, writable: false);
                    _lzmaDecoder.Decode(chunkInStream, outStream, chunkUnpackSize);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(packBuffer);
                }

                totalWritten += chunkUnpackSize;
            }
        }
    }
}
