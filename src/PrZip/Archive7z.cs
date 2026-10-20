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
using PrZip.Codecs.Crypto;
using PrZip.Container.Extraction;
using PrZip.Container.Headers;
using PrZip.Exceptions;
using PrZip.Primitives.Checksum;
using PrZip.Primitives.IO;

namespace PrZip;

/// <summary>
/// API principal de alto nivel para abrir, listar y extraer archivos 7z.
/// Cero dependencias externas y compatible con Native AOT.
/// </summary>
public sealed class Archive7z : IDisposable, IAsyncDisposable
{
    private readonly IRandomAccessSource _source;
    private readonly bool _ownsSource;
    private readonly string? _password;
    private readonly Aes7zKeyCache? _keys;
    private readonly ArchiveMetadata _metadata;
    private readonly List<Entry7z> _entries;
    private bool _disposed;

    private Archive7z(IRandomAccessSource source, bool ownsSource, string? password = null)
    {
        _source = source;
        _ownsSource = ownsSource;
        _password = password;
        _keys = string.IsNullOrEmpty(password) ? null : new Aes7zKeyCache(password);
        try
        {
            _metadata = ArchiveHeaderReader.Read(source, password, _keys);
        }
        catch
        {
            _keys?.Dispose();
            throw;
        }

        _entries = new List<Entry7z>(_metadata.Files.Count);
        for (int i = 0; i < _metadata.Files.Count; i++)
        {
            var item = _metadata.Files[i];
            bool isEncrypted = false;
            if (item.FolderIndex >= 0 && item.FolderIndex < _metadata.Folders.Count)
            {
                isEncrypted = _metadata.Folders[item.FolderIndex].Coders.Any(c => MethodId.Equals(c.MethodId, MethodId.Aes256));
            }
            _entries.Add(new Entry7z(item, i, isEncrypted));
        }
    }

    public static Archive7z Open(string filePath, string? password = null)
    {
        var source = new FileRandomAccessSource(filePath);
        try
        {
            return new Archive7z(source, ownsSource: true, password);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public static Archive7z Open(IRandomAccessSource source, string? password = null)
    {
        return new Archive7z(source, ownsSource: false, password);
    }

    public IReadOnlyList<Entry7z> Entries => _entries;

    // Cursor de lectura secuencial: extraer las entradas de un folder sólido en orden lo decodifica
    // una sola vez; pedir una entrada anterior al cursor obliga a reabrir el folder desde el principio.
    private int _cursorFolderIndex = -1;
    private Stream? _cursorStream;
    private List<Stream>? _cursorPackedStreams;
    private long _cursorPosition;

    /// <summary>
    /// Extrae el contenido de una entrada hacia un Stream de destino, en streaming.
    /// El CRC-32 se calcula al vuelo y se verifica al terminar: si no coincide se lanza una excepción,
    /// pero los bytes ya escritos en destination quedan escritos (quien llama decide qué hacer con ellos).
    /// </summary>
    public void Extract(Entry7z entry, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (entry.IsDirectory || entry.IsEmptyStream)
        {
            return; // Directorios o archivos vacíos no tienen datos
        }

        int folderIndex = entry.Item.FolderIndex;
        if (folderIndex < 0 || folderIndex >= _metadata.Folders.Count)
        {
            throw new CorruptArchiveException($"La entrada '{entry.Name}' no tiene una carpeta de datos asociada.");
        }

        Folder folder = _metadata.Folders[folderIndex];
        bool isEncrypted = folder.Coders.Any(c => MethodId.Equals(c.MethodId, MethodId.Aes256));
        long offset = entry.Item.StreamOffset;
        long size = entry.Item.Size;

        if (offset < 0 || size < 0)
        {
            throw new CorruptArchiveException($"Rango de extracción inválido para '{entry.Name}': offset={offset}, size={size}.");
        }

        uint crc = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
        try
        {
            Stream reader = PositionCursor(folderIndex, folder, offset);
            long remaining = size;
            while (remaining > 0)
            {
                int read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new CorruptArchiveException(
                        $"Datos truncados en '{entry.Name}': el folder terminó con {size - remaining} de {size} bytes.");
                }

                crc = Crc32Algorithm.Update(crc, buffer.AsSpan(0, read));
                destination.Write(buffer, 0, read);
                remaining -= read;
                _cursorPosition += read;
            }
        }
        catch (Exception ex) when (isEncrypted
                                   && ex is CorruptArchiveException or InvalidDataException or EndOfStreamException
                                   && ex is not InvalidPasswordOrCorruptDataException)
        {
            ResetCursor();
            throw new InvalidPasswordOrCorruptDataException(
                $"No se pudo descomprimir '{entry.Name}' tras descifrarlo: contraseña incorrecta o datos corruptos.", ex);
        }
        catch
        {
            ResetCursor();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (entry.Crc.HasValue && crc != entry.Crc.Value)
        {
            if (isEncrypted)
            {
                throw new InvalidPasswordOrCorruptDataException(
                    $"Error de integridad/contraseña en '{entry.Name}': esperado 0x{entry.Crc.Value:X8}, calculado 0x{crc:X8}.");
            }

            throw new CorruptArchiveException(
                $"Error de CRC en '{entry.Name}': esperado 0x{entry.Crc.Value:X8}, calculado 0x{crc:X8}.");
        }
    }

    private Stream PositionCursor(int folderIndex, Folder folder, long offset)
    {
        if (_cursorStream == null || _cursorFolderIndex != folderIndex || _cursorPosition > offset)
        {
            ResetCursor();
            _cursorPackedStreams = OpenPackedStreams(folderIndex, folder);
            _cursorStream = FolderDecoder.Open(folder, _cursorPackedStreams, _password, _keys);
            _cursorFolderIndex = folderIndex;
            _cursorPosition = 0;
        }

        // Saltar hasta el comienzo de la entrada (en un folder sólido, las entradas previas no pedidas).
        long toSkip = offset - _cursorPosition;
        if (toSkip > 0)
        {
            byte[] skip = ArrayPool<byte>.Shared.Rent(1 << 16);
            try
            {
                while (toSkip > 0)
                {
                    int read = _cursorStream.Read(skip, 0, (int)Math.Min(skip.Length, toSkip));
                    if (read == 0)
                    {
                        throw new CorruptArchiveException($"El folder {folderIndex} terminó antes del offset {offset}.");
                    }

                    toSkip -= read;
                    _cursorPosition += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(skip);
            }
        }

        return _cursorStream;
    }

    private List<Stream> OpenPackedStreams(int folderIndex, Folder folder)
    {
        PackInfo packInfo = _metadata.PackInfo ?? throw new CorruptArchiveException("Falta información PackInfo.");

        // Los streams empaquetados se asignan a los folders en orden, tantos como entradas sin ligar tenga cada uno.
        int firstPackStream = 0;
        for (int f = 0; f < folderIndex; f++)
        {
            firstPackStream += _metadata.Folders[f].PackedStreamIndices.Count;
        }

        long packOffset = _metadata.Signature.BaseOffset + SignatureHeader.HeaderSize + packInfo.PackPos;
        for (int i = 0; i < firstPackStream && i < packInfo.PackSizes.Count; i++)
        {
            packOffset += packInfo.PackSizes[i];
        }

        var streams = new List<Stream>(folder.PackedStreamIndices.Count);
        for (int p = 0; p < folder.PackedStreamIndices.Count; p++)
        {
            int index = firstPackStream + p;
            if (index >= packInfo.PackSizes.Count)
            {
                throw new CorruptArchiveException($"El folder {folderIndex} referencia el stream empaquetado {index}, que no existe.");
            }

            long packSize = packInfo.PackSizes[index];
            if (packOffset + packSize > _source.Length)
            {
                throw new ArchiveTruncatedException(
                    $"El stream empaquetado {index} ({packSize} bytes en el offset {packOffset}) excede el archivo ({_source.Length} bytes).");
            }

            streams.Add(new SourceWindowStream(_source, packOffset, packSize));
            packOffset += packSize;
        }

        return streams;
    }

    private void ResetCursor()
    {
        _cursorStream?.Dispose();
        _cursorStream = null;
        if (_cursorPackedStreams != null)
        {
            foreach (var s in _cursorPackedStreams)
            {
                s.Dispose();
            }

            _cursorPackedStreams = null;
        }

        _cursorFolderIndex = -1;
        _cursorPosition = 0;
    }

    /// <summary>
    /// Extrae una entrada a un archivo en disco con protección anti Zip-Slip y preservación de marcas de tiempo.
    /// Si el archivo ya existe se lanza IOException y no se toca: pisarlo tiene que pedirse con
    /// <paramref name="overwrite"/>. Antes se pisaba siempre y, si la extracción fallaba, se borraba el
    /// archivo que estaba, aunque fuera de otro.
    /// </summary>
    public void Extract(Entry7z entry, string destinationFilePath) => Extract(entry, destinationFilePath, overwrite: false);

    public void Extract(Entry7z entry, string destinationFilePath, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(destinationFilePath);
            ApplyMetadata(entry, destinationFilePath, isDirectory: true);
            return;
        }

        bool created = false;
        try
        {
            using var fs = new FileStream(destinationFilePath, overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            created = true;
            Extract(entry, fs);
        }
        catch
        {
            // Un archivo que no superó la verificación no debe quedar en disco con el nombre del original. Solo
            // se borra lo que creó esta extracción: si ya existía (y no se pidió pisarlo), no es nuestro.
            if (created)
            {
                File.Delete(destinationFilePath);
            }

            throw;
        }

        ApplyMetadata(entry, destinationFilePath, isDirectory: false);
    }

    /// <summary>
    /// Extrae todas las entradas del archivo hacia el directorio de destino.
    /// Protege activamente contra ataques de evasión de rutas (Zip-Slip).
    /// </summary>
    public void ExtractAll(string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ExtractAll(destinationDirectory, onRenamed: null);
    }

    /// <summary>
    /// Extrae todas las entradas. Los nombres se sanean con <see cref="EntryPathSanitizer"/>; cuando el
    /// destino no refleja el nombre original (carácter inválido, nombre de dispositivo, choque con otra
    /// entrada) se invoca onRenamed(entrada, rutaFinal) para que quien llama lo deje registrado.
    /// </summary>
    public void ExtractAll(string destinationDirectory, Action<Entry7z, string>? onRenamed) =>
        ExtractAll(destinationDirectory, onRenamed, overwrite: false);

    /// <summary>Igual, pudiendo pisar archivos que ya estén en el destino (por defecto, no).</summary>
    public void ExtractAll(string destinationDirectory, Action<Entry7z, string>? onRenamed, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);
        var used = EntryPathSanitizer.CreatePathSet();

        foreach (var entry in _entries)
        {
            string targetPath = EntryPathSanitizer.ResolveTarget(root, entry.Name, entry.IsDirectory, flatten: false, used, out bool renamed);
            if (renamed)
            {
                onRenamed?.Invoke(entry, targetPath);
            }

            Extract(entry, targetPath, overwrite);
        }
    }

    private static void ApplyMetadata(Entry7z entry, string path, bool isDirectory)
    {
        try
        {
            if (entry.LastWriteTime.HasValue)
            {
                // UtcDateTime con los Set...Utc: el 7z guarda la hora en UTC. Con .DateTime y SetLastWriteTime
                // se tomaba como hora local y el archivo quedaba corrido el huso horario (3 h en Argentina).
                if (isDirectory) Directory.SetLastWriteTimeUtc(path, entry.LastWriteTime.Value.UtcDateTime);
                else File.SetLastWriteTimeUtc(path, entry.LastWriteTime.Value.UtcDateTime);
            }

            if (entry.CreationTime.HasValue)
            {
                if (isDirectory) Directory.SetCreationTimeUtc(path, entry.CreationTime.Value.UtcDateTime);
                else File.SetCreationTimeUtc(path, entry.CreationTime.Value.UtcDateTime);
            }

            if (entry.Attributes.HasValue && !isDirectory)
            {
                File.SetAttributes(path, entry.Attributes.Value);
            }
        }
        catch
        {
            // Tolerar restricciones de sistema de archivos al aplicar atributos
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            ResetCursor();
            _keys?.Dispose();
            if (_ownsSource)
            {
                _source.Dispose();
            }
            _disposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            ResetCursor();
            _keys?.Dispose();
            if (_ownsSource)
            {
                await _source.DisposeAsync().ConfigureAwait(false);
            }
            _disposed = true;
        }
    }
}
