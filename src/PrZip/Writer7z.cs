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
using PrZip.Codecs.Crypto;
using PrZip.Codecs.Filters;
using PrZip.Codecs.Lzma;
using PrZip.Codecs.Lzma2;
using PrZip.Codecs.Ppmd;
using PrZip.Container.Headers;
using PrZip.Exceptions;
using PrZip.Primitives.Checksum;

namespace PrZip;

/// <summary>
/// Motor de escritura y creación de archivos comprimidos en formato 7z.
/// Permite generar archivos 100% compatibles con el estándar de Igor Pavlov,
/// con cero dependencias externas y compatibilidad con Native AOT.
/// </summary>
public sealed class Writer7z : IDisposable, IAsyncDisposable
{
    private readonly Stream _outputStream;
    private readonly WriterOptions7z _options;
    private readonly bool _ownsStream;

    private readonly List<Folder> _folders = [];
    private readonly List<FileItem> _files = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly PackInfo _packInfo;

    private readonly string? _ownedFilePath;

    private readonly List<int> _subStreamCounts = [];
    private FolderBuilder? _solid;
    private readonly List<FileItem> _solidItems = [];

    // Archivos sin modo sólido que se comprimen en otros hilos; se escriben en el orden en que se agregaron.
    private readonly Queue<PendingFolder> _pending = new();
    private long _pendingMemory;
    private SemaphoreSlim? _workers;

    // La clave AES se deriva una sola vez por archivo, con una sal aleatoria propia; cada folder (y la
    // cabecera cifrada) usa un IV nuevo. Derivar cuesta decenas de ms y sin sólido hay un folder por entrada.
    private Aes7zEncoder? _aesKey;

    /// <summary>Memoria estimada que pueden ocupar a la vez los archivos comprimiéndose en paralelo.</summary>
    private const long ParallelMemoryBudget = 2L << 30;

    private bool _finished;
    private bool _faulted;
    private bool _disposed;

    private Writer7z(Stream outputStream, WriterOptions7z options, bool ownsStream, string? ownedFilePath = null)
    {
        if (!outputStream.CanSeek || !outputStream.CanWrite)
        {
            throw new ArgumentException("El stream de destino debe ser seekable y writable para poder generar archivos 7z.", nameof(outputStream));
        }

        _outputStream = outputStream;
        // Copia: las opciones se validaron al crear y no deben poder cambiar a mitad del archivo.
        _options = options.Clone();
        _ownsStream = ownsStream;
        _ownedFilePath = ownedFilePath;

        _packInfo = new PackInfo
        {
            PackPos = 0 // Inicia en offset 32 (inmediatamente tras el SignatureHeader)
        };

        // Reservar los primeros 32 bytes para el SignatureHeader (placeholder)
        Span<byte> placeholder = stackalloc byte[SignatureHeader.HeaderSize];
        placeholder.Clear();
        _outputStream.Position = 0;
        _outputStream.Write(placeholder);
    }

    /// <summary>
    /// Crea el 7z en <paramref name="filePath"/>. Si ya existe un archivo ahí, falla sin tocarlo: si se lo
    /// pisara y el empaquetado fallara después, el escritor lo borraría y se perderían los dos.
    /// </summary>
    public static Writer7z Create(string filePath, WriterOptions7z? options = null) =>
        Create(filePath, options, overwrite: false);

    /// <param name="overwrite">True para reemplazar un archivo existente (se borra si el empaquetado falla).</param>
    public static Writer7z Create(string filePath, WriterOptions7z? options, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidateOptions(options);
        var fs = new FileStream(filePath, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        return new Writer7z(fs, options ?? new WriterOptions7z(), ownsStream: true, ownedFilePath: Path.GetFullPath(filePath));
    }

    public static Writer7z Create(Stream outputStream, WriterOptions7z? options = null)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        ValidateOptions(options);
        return new Writer7z(outputStream, options ?? new WriterOptions7z(), ownsStream: false);
    }

    /// <summary>
    /// Rechaza las combinaciones que antes producían un 7z sin cifrar sin avisar: quien pidió cifrar los
    /// nombres o pasó una contraseña vacía creía tener un paquete protegido.
    /// </summary>
    private static void ValidateOptions(WriterOptions7z? options)
    {
        if (options == null)
        {
            return;
        }

        if (options.Password is { Length: 0 })
        {
            throw new ArgumentException(
                "La contraseña está vacía: para no cifrar, deje Password en null.", nameof(options));
        }

        if (options.EncryptHeaders && options.Password == null)
        {
            throw new ArgumentException(
                "EncryptHeaders cifra los nombres con la contraseña, pero no se indicó ninguna.", nameof(options));
        }
    }

    /// <summary>
    /// Añade una entrada sin contenido (por ejemplo directorio o archivo vacío).
    /// </summary>
    public void Add(WriterEntry7z entry) => Add(entry, null);

    /// <summary>
    /// Añade una entrada con datos proporcionados por un Stream.
    /// </summary>
    public void Add(WriterEntry7z entry, Stream? contentStream) => AddChecked(entry, contentStream, deferrable: null);

    /// <summary>
    /// Agrega una entrada. Con <paramref name="deferrable"/>, el archivo puede comprimirse en otro hilo y la
    /// llamada devuelve true: el escritor pasa a ser dueño del stream.
    /// </summary>
    private bool AddChecked(WriterEntry7z entry, Stream? contentStream, Stream? deferrable)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
        {
            throw new InvalidOperationException("El escritor quedó inutilizable: una entrada anterior falló al empaquetarse.");
        }

        // Antes del try: una entrada rechazada no escribió nada y el escritor sigue sirviendo.
        string name = CheckEntry(entry, contentStream);
        try
        {
            return AddCore(entry, name, contentStream, deferrable);
        }
        catch
        {
            // Los bytes de la entrada fallida ya pueden estar en el stream: cerrar el archivo
            // produciría un 7z de apariencia válida al que le faltan entradas.
            _faulted = true;
            throw;
        }
    }

    /// <summary>
    /// Normaliza y valida el nombre: '\' pasa a '/' y una carpeta pierde la barra final (7-Zip las guarda
    /// sin ella). Se rechazan los nombres vacíos, con un carácter nulo (cortaba la lista de nombres de la
    /// cabecera y corría los de todas las entradas siguientes), absolutos o con unidad, con componentes
    /// vacíos, "." o "..", los repetidos, y una carpeta con contenido (que antes se descartaba sin aviso).
    /// </summary>
    private string CheckEntry(WriterEntry7z entry, Stream? content)
    {
        if (entry.IsDirectory && content != null)
        {
            throw new ArgumentException($"'{entry.Name}' es una carpeta: no puede llevar contenido.", nameof(entry));
        }

        string name = (entry.Name ?? string.Empty).Replace('\\', '/');
        if (entry.IsDirectory)
        {
            name = name.TrimEnd('/');
        }

        string? problem =
            name.Length == 0 ? "está vacío" :
            name.Contains('\0') ? "contiene un carácter nulo" :
            name[0] == '/' || IsDriveRooted(name) ? "es una ruta absoluta" :
            name.EndsWith('/') ? "termina en '/' pero la entrada no es una carpeta" :
            name.Split('/').Any(part => part is "" or "." or "..") ? "tiene un componente vacío, '.' o '..'" :
            null;
        if (problem != null)
        {
            throw new ArgumentException($"Nombre de entrada inválido '{entry.Name}': {problem}.", nameof(entry));
        }

        if (!_names.Add(name))
        {
            throw new ArgumentException($"Ya hay una entrada llamada '{name}'.", nameof(entry));
        }

        return name;
    }

    /// <summary>"C:" o "C:/...": una unidad de Windows. "x:y.txt" es un nombre válido en otros sistemas.</summary>
    private static bool IsDriveRooted(string name) =>
        name.Length >= 2 && name[1] == ':' && char.IsAsciiLetter(name[0]) && (name.Length == 2 || name[2] == '/');

    private bool AddCore(WriterEntry7z entry, string name, Stream? contentStream, Stream? deferrable)
    {
        var item = new FileItem
        {
            Name = name,
            IsEmptyStream = entry.IsDirectory || contentStream == null,
            IsEmptyFile = !entry.IsDirectory && contentStream == null,
            MTime = (ulong?)entry.LastWriteTime?.ToFileTime()
        };

        if (entry.Attributes.HasValue)
        {
            item.Attributes = (uint)entry.Attributes.Value;
        }

        _files.Add(item);

        // Directorios y entradas sin contenido no ocupan datos.
        if (item.IsEmptyStream)
        {
            return false;
        }

        bool useBcj = _options.AutoDetectFilters && IsExecutable(name);

        if (_options.Solid && !useBcj)
        {
            DrainPending();
            _solid ??= new FolderBuilder(_options, GetEffectiveDictSize(), useBcj: false, _outputStream, NewFolderCipher());
            var (size, crc) = _solid.AddSubStream(contentStream!);
            if (size == 0)
            {
                MarkEmptyFile(item);
                return false;
            }

            item.Size = size;
            item.Crc = crc;
            _solidItems.Add(item);

            if (_solid.TotalUnpackSize >= _options.SolidBlockSize)
            {
                CloseSolidFolder();
            }

            return false;
        }

        // Un ejecutable lleva su propio folder con BCJ. Los folders se escriben contiguos, así que el
        // bloque sólido abierto se cierra antes.
        CloseSolidFolder();

        if (deferrable != null && TryQueue(item, deferrable, useBcj))
        {
            return true;
        }

        DrainPending();

        using var builder = new FolderBuilder(_options, GetEffectiveDictSize(), useBcj, _outputStream, NewFolderCipher());
        var (fileSize, fileCrc) = builder.AddSubStream(contentStream!);
        if (fileSize == 0)
        {
            // Nada llegó al destino: los codificadores no emiten hasta recibir datos.
            MarkEmptyFile(item);
            return false;
        }

        item.Size = fileSize;
        item.Crc = fileCrc;
        RegisterFolder(builder, [item]);
        return false;
    }

    /// <summary>Cifrador para un folder nuevo: misma clave, IV propio. Null sin contraseña.</summary>
    private Aes7zEncoder? NewFolderCipher()
    {
        if (_options.Password == null)
        {
            return null;
        }

        _aesKey ??= new Aes7zEncoder(_options.Password.AsSpan());
        return _aesKey.WithNewIv();
    }

    private int EffectiveThreads => _options.Threads <= 0 ? Environment.ProcessorCount : _options.Threads;

    /// <summary>
    /// Manda a comprimir el archivo en otro hilo, a un búfer propio, si vale la pena: hay más de un hilo,
    /// el método comprime y el archivo no es tan grande como para que LZMA2 ya lo reparta en bloques.
    /// Cada archivo usa un solo hilo, y como la salida de LZMA2 no depende de los hilos, el resultado
    /// es idéntico byte a byte al de comprimir en serie.
    /// </summary>
    private bool TryQueue(FileItem item, Stream content, bool useBcj)
    {
        int threads = EffectiveThreads;
        if (threads <= 1 || _options.Method == CompressionMethod.Copy || _options.Level == 0)
        {
            return false;
        }

        uint dictSize = GetEffectiveDictSize();
        long length = content.Length;
        if (_options.Method == CompressionMethod.Lzma2 && length > new Lzma2Encoder(dictSize).BlockSize)
        {
            return false;
        }

        // BZip2 ya reparte entre hilos los bloques de un archivo grande.
        if (_options.Method == CompressionMethod.BZip2 && length > 2L * FolderBuilder.BZip2BlockSize(_options.Level) * 100000)
        {
            return false;
        }

        long memory = EstimateMemory(length);
        if (memory > ParallelMemoryBudget)
        {
            return false;
        }

        // La fila de espera es más larga que la cantidad de hilos para que un archivo lento no detenga
        // a los demás; el semáforo limita cuántos se comprimen a la vez.
        while (_pending.Count >= 4 * threads || (_pending.Count > 0 && _pendingMemory + memory > ParallelMemoryBudget))
        {
            DrainOne();
        }

        var options = _options.WithThreads(1);
        var cipher = NewFolderCipher();
        var workers = _workers ??= new SemaphoreSlim(threads);
        var task = Task.Run(async () =>
        {
            await workers.WaitAsync().ConfigureAwait(false);
            try
            {
                return EncodeDetached(options, dictSize, useBcj, content, cipher);
            }
            finally
            {
                workers.Release();
            }
        });
        _pending.Enqueue(new PendingFolder(item, task, memory));
        _pendingMemory += memory;
        return true;
    }

    /// <summary>
    /// Memoria aproximada de comprimir un archivo solo: la entrada y la salida en búferes, más el buscador
    /// de coincidencias (BT4: unos 9 bytes por byte de diccionario, que se ajusta al tamaño del archivo)
    /// o el modelo de PPMd, que se reserva entero.
    /// </summary>
    private long EstimateMemory(long length)
    {
        long model = _options.Method switch
        {
            CompressionMethod.Ppmd => Ppmd7EncoderStream.DefaultsForLevel(_options.Level).MemorySize,
            // Bloque, rotaciones (cuatro enteros por byte) y la salida MTF.
            CompressionMethod.BZip2 => 20L * Math.Min(length, FolderBuilder.BZip2BlockSize(_options.Level) * 100000L),
            CompressionMethod.Deflate => 2L << 20,
            _ => 9 * Math.Min(length, GetEffectiveDictSize())
        };
        return 2 * length + model + (1 << 20);
    }

    private static EncodedFolder EncodeDetached(WriterOptions7z options, uint dictSize, bool useBcj, Stream content, Aes7zEncoder? cipher)
    {
        using (content)
        {
            var data = new MemoryStream();
            using var builder = new FolderBuilder(options, dictSize, useBcj, data, cipher);
            var (size, crc) = builder.AddSubStream(content);
            if (size == 0)
            {
                return new EncodedFolder(null, 0, data, 0, 0);
            }

            Folder folder = builder.Finish(out long packSize);
            return new EncodedFolder(folder, packSize, data, size, crc);
        }
    }

    /// <summary>Escribe, en orden, los archivos que se estaban comprimiendo en otros hilos.</summary>
    private void DrainPending()
    {
        while (_pending.Count > 0)
        {
            DrainOne();
        }
    }

    private void DrainOne()
    {
        var pending = _pending.Dequeue();
        _pendingMemory -= pending.Memory;
        EncodedFolder result = pending.Task.GetAwaiter().GetResult();
        using var data = result.Data;
        if (result.Folder == null)
        {
            MarkEmptyFile(pending.Item);
            return;
        }

        data.Position = 0;
        data.CopyTo(_outputStream);
        pending.Item.Size = result.Size;
        pending.Item.Crc = result.Crc;
        RegisterFolder(result.Folder, result.PackSize, [pending.Item]);
    }

    /// <summary>Tras una falla: espera a los hilos que quedaron trabajando y descarta lo que produjeron.</summary>
    private void AbandonPending()
    {
        while (_pending.Count > 0)
        {
            var pending = _pending.Dequeue();
            try
            {
                pending.Task.GetAwaiter().GetResult().Data.Dispose();
            }
#pragma warning disable CA1031 // El archivo ya se descarta; la falla que se informa es la original
            catch (Exception)
            {
            }
#pragma warning restore CA1031
        }

        _pendingMemory = 0;
    }

    private sealed record PendingFolder(FileItem Item, Task<EncodedFolder> Task, long Memory);

    private sealed record EncodedFolder(Folder? Folder, long PackSize, MemoryStream Data, long Size, uint Crc);

    private static bool IsExecutable(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static void MarkEmptyFile(FileItem item)
    {
        item.IsEmptyStream = true;
        item.IsEmptyFile = true;
        item.Size = 0;
        item.Crc = null;
    }

    private void CloseSolidFolder()
    {
        if (_solid == null)
        {
            return;
        }

        using var builder = _solid;
        _solid = null;
        if (builder.SubStreamCount > 0)
        {
            RegisterFolder(builder, _solidItems);
        }

        _solidItems.Clear();
    }

    private void RegisterFolder(FolderBuilder builder, List<FileItem> items)
    {
        Folder folder = builder.Finish(out long packSize);
        RegisterFolder(folder, packSize, items);
    }

    private void RegisterFolder(Folder folder, long packSize, List<FileItem> items)
    {
        int folderIndex = _folders.Count;
        _folders.Add(folder);
        _packInfo.PackSizes.Add(packSize);
        _packInfo.PackCrcs.Add(null); // Opcional según estándar 7z
        _subStreamCounts.Add(items.Count);

        for (int i = 0; i < items.Count; i++)
        {
            items[i].FolderIndex = folderIndex;
            items[i].SubStreamIndex = i;
        }
    }

    /// <summary>
    /// Añade un archivo físico de disco. Sin modo sólido y con varios hilos, puede comprimirse en otro
    /// hilo: el archivo se abre ahora (queda protegido contra escritura hasta leerse) y un error al
    /// comprimirlo se informa en un Add posterior o en Finish.
    /// </summary>
    public void Add(string filePath, string entryName)
    {
        var entry = WriterEntry7z.FromFile(filePath, entryName);
        var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        bool queued = false;
        try
        {
            queued = AddChecked(entry, fs, deferrable: fs);
        }
        finally
        {
            if (!queued)
            {
                fs.Dispose();
            }
        }
    }

    /// <summary>
    /// Añade una entrada cuyo contenido pasa a ser del escritor, que cierra el stream cuando termina
    /// con él, también si falla. Sin modo sólido y con varios hilos puede comprimirse en otro hilo
    /// (si el stream conoce su largo, CanSeek), como <see cref="Add(string, string)"/>: sirve para
    /// envolver el archivo en un stream propio, por ejemplo para calcular hashes mientras se lee.
    /// El stream se lee de principio a fin una sola vez, desde un único hilo.
    /// </summary>
    public void AddOwned(WriterEntry7z entry, Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        bool queued = false;
        try
        {
            queued = AddChecked(entry, content, deferrable: content.CanSeek ? content : null);
        }
        finally
        {
            if (!queued)
            {
                content.Dispose();
            }
        }
    }

    /// <summary>
    /// Añade un directorio.
    /// </summary>
    public void AddDirectory(string directoryPath, string entryName)
    {
        var entry = WriterEntry7z.FromDirectory(directoryPath, entryName);
        Add(entry, null);
    }

    private uint GetEffectiveDictSize()
    {
        if (_options.DictionarySize > 0)
        {
            return _options.DictionarySize;
        }

        return _options.Level switch
        {
            // Igual que LzmaEncProps_Normalize de 7-Zip: 256 KB, 1, 4, 8, 16, 32, 32, 64, 64 MB.
            <= 0 => 0,
            <= 3 => 1u << (_options.Level * 2 + 16),
            <= 6 => 1u << (_options.Level + 19),
            7 => 32u << 20,
            _ => 64u << 20
        };
    }

    /// <summary>
    /// Serializa los metadatos de las cabeceras, sella el SignatureHeader y finaliza el archivo 7z.
    /// </summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) return;

        if (_faulted)
        {
            throw new InvalidOperationException(
                "No se puede cerrar el archivo 7z: una entrada falló al empaquetarse y el resultado quedaría incompleto.");
        }

        try
        {
            FinishCore();
        }
        catch
        {
            // Sin esto, Dispose volvía a intentar el cierre: con el último bloque sólido ya descartado,
            // el segundo intento terminaba sin error y dejaba un 7z con firma válida y entradas sin datos.
            _faulted = true;
            throw;
        }

        _finished = true;
    }

    private void FinishCore()
    {
        DrainPending();
        CloseSolidFolder();

        // El lector asigna los substreams a las entradas con datos en el orden en que aparecen: esas
        // entradas se reordenan por (folder, substream) dentro de los lugares que ya ocupaban, y las
        // entradas sin datos quedan donde estaban.
        var dataItems = _files.Where(f => !f.IsEmptyStream)
            .OrderBy(f => f.FolderIndex).ThenBy(f => f.SubStreamIndex).ToList();
        var files = new List<FileItem>(_files.Count);
        int next = 0;
        foreach (var f in _files)
        {
            files.Add(f.IsEmptyStream ? f : dataItems[next++]);
        }

        bool anySolid = _subStreamCounts.Any(n => n != 1);
        List<long>? subStreamSizes = anySolid ? dataItems.Select(f => f.Size).ToList() : null;
        List<uint?> subStreamCrcs = dataItems.Select(f => f.Crc).ToList();
        byte[] rawHeaderBytes = ArchiveHeaderWriter.WriteHeader(
            _folders, files, _packInfo, _subStreamCounts, subStreamSizes, subStreamCrcs);

        if (_options.EncryptHeaders)
        {
            // EncryptedHeader (-mhe=on): Cifrar el bloque de cabecera con AES-256
            long encStartPos = _outputStream.Position;
            using var aesEncoder = NewFolderCipher()!;
            using var headerInMs = new MemoryStream(rawHeaderBytes);
            long encPackSize = aesEncoder.Encode(headerInMs, _outputStream);
            long encPackPos = encStartPos - SignatureHeader.HeaderSize;

            var encFolder = new Folder();
            encFolder.Coders.Add(new CoderInfo
            {
                MethodId = MethodId.Aes256,
                Properties = aesEncoder.GetProperties(),
                NumInStreams = 1,
                NumOutStreams = 1
            });
            encFolder.UnpackSizes.Add(rawHeaderBytes.Length);
            encFolder.Crc = Crc32Algorithm.Compute(rawHeaderBytes);

            var encPackInfo = new PackInfo { PackPos = encPackPos };
            encPackInfo.PackSizes.Add(encPackSize);

            byte[] encodedHeaderBytes = ArchiveHeaderWriter.WriteEncodedHeader(encFolder, encPackInfo);

            long nextHeaderOffset = _outputStream.Position - SignatureHeader.HeaderSize;
            long nextHeaderSize = encodedHeaderBytes.Length;
            uint nextHeaderCrc = Crc32Algorithm.Compute(encodedHeaderBytes);

            _outputStream.Write(encodedHeaderBytes);
            _outputStream.Flush();

            SealSignatureHeader(nextHeaderOffset, nextHeaderSize, nextHeaderCrc);
        }
        else
        {
            // Cabecera plana estándar sin cifrar
            long nextHeaderOffset = _outputStream.Position - SignatureHeader.HeaderSize;
            long nextHeaderSize = rawHeaderBytes.Length;
            uint nextHeaderCrc = Crc32Algorithm.Compute(rawHeaderBytes);

            _outputStream.Write(rawHeaderBytes);
            _outputStream.Flush();

            SealSignatureHeader(nextHeaderOffset, nextHeaderSize, nextHeaderCrc);
        }
    }

    private void SealSignatureHeader(long nextHeaderOffset, long nextHeaderSize, uint nextHeaderCrc)
    {
        var sigHeader = new SignatureHeader(
            majorVersion: 0,
            minorVersion: 4,
            startHeaderCrc: 0,
            nextHeaderOffset: nextHeaderOffset,
            nextHeaderSize: nextHeaderSize,
            nextHeaderCrc: nextHeaderCrc,
            baseOffset: 0);

        Span<byte> sigBuf = stackalloc byte[SignatureHeader.HeaderSize];
        sigHeader.Write(sigBuf);

        _outputStream.Position = 0;
        _outputStream.Write(sigBuf);
        _outputStream.Flush();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                if (!_finished && !_faulted)
                {
                    Finish();
                }
            }
            finally
            {
                // También si Finish falló (por ejemplo, un archivo que se comprimía en otro hilo): el
                // destino se cierra y el 7z a medias se borra igual.
                AbandonPending();
                _workers?.Dispose();
                _aesKey?.Dispose();

                if (_ownsStream && !_options.LeaveStreamOpen)
                {
                    _outputStream.Dispose();
                }

                // Un archivo propio que no se pudo completar no debe quedar en disco con forma de 7z.
                if (_faulted && _ownedFilePath != null)
                {
                    if (_options.LeaveStreamOpen)
                    {
                        _outputStream.SetLength(0);
                    }
                    else
                    {
                        File.Delete(_ownedFilePath);
                    }
                }

                _disposed = true;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
