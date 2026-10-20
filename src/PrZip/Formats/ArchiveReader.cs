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

using System.IO.Compression;
using PrZip.Codecs.BZip2;
using PrZip.Exceptions;
using PrZip.Primitives.IO;

namespace PrZip.Formats;

/// <summary>Qué es una entrada de un archivo.</summary>
public enum ArchiveEntryKind
{
    File,
    Directory,
    SymbolicLink,
    HardLink,

    /// <summary>Dispositivo, FIFO, archivo disperso u otro tipo que no se materializa como archivo común.</summary>
    Other,

    /// <summary>
    /// Flujo de datos alternativo de NTFS guardado junto a un archivo o carpeta (RAR con -os), nombrado
    /// "archivo:flujo"; por ejemplo "foto.jpg:Zone.Identifier", que dice de dónde se descargó. Se informa con
    /// su tamaño, no se extrae.
    /// </summary>
    AlternateStream
}

/// <summary>
/// Comentario de un archivo comprimido o de una entrada: el texto, o el motivo por el que estaba pero no se
/// pudo leer (dañado, cifrado sin contraseña, método desconocido).
/// </summary>
public sealed record ArchiveComment(string? Text, string? Problem);

/// <summary>Una entrada tal como la describe el archivo. El nombre usa '/' como separador.</summary>
public sealed record ArchiveEntryInfo(string Name, ArchiveEntryKind Kind, long Size, DateTimeOffset? LastWriteTime, string? LinkTarget = null,
    ArchiveComment? Comment = null);

/// <summary>
/// Lectura uniforme de los formatos que abre el motor: 7z, TAR (también dentro de GZip, BZip2 o XZ), un
/// archivo suelto comprimido con GZip, BZip2 o XZ, e imágenes ISO 9660 (con Joliet). El formato se reconoce
/// por su firma, no por la extensión. Las entradas se recorren en orden (<see cref="MoveNext"/>), que es lo
/// único posible en un .tar.gz; el contenido de la actual se copia con <see cref="CopyCurrentTo"/>.
/// </summary>
public abstract class ArchiveReader : IDisposable
{
    private bool _disposed;

    /// <summary>Formato reconocido, para mostrar: "7z", "tar", "tar.gz", "gz", "xz", "iso"…</summary>
    public abstract string FormatName { get; }

    /// <summary>Tamaño del archivo abierto, en bytes (de todos sus volúmenes, si los tiene).</summary>
    public long ArchiveLength { get; private set; }

    private static ArchiveFormatProvider[] s_providers = [];
    private static readonly Lock s_providersLock = new();

    /// <summary>
    /// Suma un formato que vive fuera del núcleo (RAR, en PrZip.Rar). Registrar otra vez un proveedor del
    /// mismo tipo no tiene efecto.
    /// </summary>
    public static void RegisterFormat(ArchiveFormatProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (s_providersLock)
        {
            if (!s_providers.Any(p => p.GetType() == provider.GetType()))
            {
                s_providers = [.. s_providers, provider];
            }
        }
    }

    /// <summary>Tamaño a informar en <see cref="ArchiveLength"/>: un formato con volúmenes devuelve la suma.</summary>
    protected virtual long MeasureLength(string path) => new FileInfo(path).Length;

    /// <summary>
    /// Bytes del archivo leídos hasta ahora: sirve de progreso cuando el total descomprimido no se conoce
    /// de antemano (un .tar.gz). Puede adelantarse un poco a lo entregado por la lectura anticipada.
    /// </summary>
    public long BytesRead => _counter?.Value ?? 0;

    private ReadCounter? _counter;

    /// <summary>Entrada actual; válida después de un <see cref="MoveNext"/> que devolvió true.</summary>
    public ArchiveEntryInfo Current { get; protected set; } = null!;

    /// <summary>Comentario del archivo comprimido (hoy, solo RAR), o null si no tiene.</summary>
    public virtual ArchiveComment? Comment => null;

    /// <summary>Avanza a la próxima entrada. Lo que no se copió de la anterior se descarta.</summary>
    public abstract bool MoveNext();

    /// <summary>Copia el contenido de la entrada actual (solo archivos comunes).</summary>
    public abstract void CopyCurrentTo(Stream destination);

    /// <summary>Abre el archivo reconociendo su formato. Devuelve null si no es ninguno de los soportados.</summary>
    public static ArchiveReader? TryOpen(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var (kind, external) = Detect(path);
        var counter = new ReadCounter();
        ArchiveReader? reader = kind switch
        {
            DetectedKind.PrZip => new Archive7zReader(Archive7z.Open(path, password)),
            DetectedKind.Iso => new IsoArchiveReader(new CountingSource(new FileRandomAccessSource(path), counter), ownsSource: true),
            DetectedKind.Tar => new TarArchiveReader(OpenFile(path, counter), "tar"),
            DetectedKind.GZip => OpenCompressed(path, "gz", OpenGZip(path, counter)),
            DetectedKind.BZip2 => OpenCompressed(path, "bz2", OpenBZip2(path, counter)),
            DetectedKind.Xz => OpenCompressed(path, "xz", new XzReadStream(new CountingSource(new FileRandomAccessSource(path), counter), ownsSource: true)),
            DetectedKind.External => external!.Open(path, password, p => OpenSeekable(p, counter)),
            _ => null
        };

        if (reader != null)
        {
            reader._counter = counter;
            reader.ArchiveLength = reader.MeasureLength(path);
        }

        return reader;
    }

    /// <summary>Formato por la firma, sin abrir el contenido; null si no se reconoce.</summary>
    public static string? DetectFormat(string path) => Detect(path) switch
    {
        (DetectedKind.External, var external) => external!.Name,
        (DetectedKind.PrZip, _) => "7z",
        (DetectedKind.Iso, _) => "iso",
        (DetectedKind.Tar, _) => "tar",
        (DetectedKind.GZip, _) => "gz",
        (DetectedKind.BZip2, _) => "bz2",
        (DetectedKind.Xz, _) => "xz",
        _ => null
    };

    internal enum DetectedKind
    {
        Unknown,
        PrZip,
        Iso,
        Tar,
        GZip,
        BZip2,
        Xz,

        /// <summary>Un formato registrado con <see cref="RegisterFormat"/>.</summary>
        External
    }

    internal static (DetectedKind Kind, ArchiveFormatProvider? External) Detect(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
        Span<byte> head = stackalloc byte[512];
        int read = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..read];

        if (head.Length >= 6 && head[..6].SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }))
        {
            return (DetectedKind.PrZip, null);
        }

        if (head.Length >= 6 && head[..6].SequenceEqual(new byte[] { 0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00 }))
        {
            return (DetectedKind.Xz, null);
        }

        if (head.Length >= 4 && head[0] == 'B' && head[1] == 'Z' && head[2] == 'h' && head[3] >= '1' && head[3] <= '9')
        {
            return (DetectedKind.BZip2, null);
        }

        if (head.Length >= 3 && head[0] == 0x1F && head[1] == 0x8B && head[2] == 0x08)
        {
            return (DetectedKind.GZip, null);
        }

        var providers = s_providers;
        foreach (var provider in providers)
        {
            if (provider.MatchesHeader(head))
            {
                return (DetectedKind.External, provider);
            }
        }

        if (TarArchiveReader.LooksLikeTar(head))
        {
            return (DetectedKind.Tar, null);
        }

        // ISO 9660: descriptor de volumen en el sector 16 ("CD001" en el byte 1).
        if (fs.Length >= 16 * 2048 + 6)
        {
            Span<byte> vd = stackalloc byte[6];
            fs.Position = 16 * 2048;
            if (fs.ReadAtLeast(vd, 6, throwOnEndOfStream: false) == 6 && vd[1..6].SequenceEqual("CD001"u8))
            {
                return (DetectedKind.Iso, null);
            }
        }

        foreach (var provider in providers)
        {
            fs.Position = 0;
            if (provider.MatchesFile(head, fs))
            {
                return (DetectedKind.External, provider);
            }
        }

        return (DetectedKind.Unknown, null);
    }

    private static Stream OpenFile(string path, ReadCounter? counter)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return counter == null ? fs : new CountingReadStream(fs, counter);
    }

    /// <summary>Archivo con posicionamiento (RAR salta de cabecera en cabecera) que cuenta lo leído.</summary>
    private static CountingSeekableStream OpenSeekable(string path, ReadCounter counter) =>
        new CountingSeekableStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), counter);

    private static GZipStream OpenGZip(string path, ReadCounter counter)
    {
        // .NET descomprime también los miembros concatenados y verifica CRC32 y largo de cada uno.
        return new GZipStream(OpenFile(path, counter), CompressionMode.Decompress);
    }

    private static OwnedProducerStream OpenBZip2(string path, ReadCounter counter)
    {
        var input = OpenFile(path, counter);
        return new OwnedProducerStream(new ProducerStream(output => BZip2Decoder.Decode(input, output), "BZip2"), input);
    }

    /// <summary>
    /// Un comprimido suelto: si los primeros 512 bytes descomprimidos son una cabecera TAR, es un .tar.gz (o
    /// .tar.bz2, .tar.xz); si no, es un único archivo, nombrado como en GZip o por el nombre sin extensión.
    /// </summary>
    private static ArchiveReader OpenCompressed(string path, string format, Stream decompressed)
    {
        try
        {
            var prefix = new byte[512];
            int got = decompressed.ReadAtLeast(prefix, prefix.Length, throwOnEndOfStream: false);
            var whole = new PrefixedStream(prefix.AsMemory(0, got), decompressed);
            if (got == 512 && TarArchiveReader.LooksLikeTar(prefix))
            {
                return new TarArchiveReader(whole, "tar." + format);
            }

            string name = format == "gz" ? GZipOriginalName(path) ?? NameWithoutExtension(path) : NameWithoutExtension(path);
            return new SingleFileReader(whole, format, name, File.GetLastWriteTimeUtc(path));
        }
        catch (InvalidDataException ex)
        {
            decompressed.Dispose();
            throw new CorruptArchiveException($"{format}: datos comprimidos inválidos ({ex.Message}).", ex);
        }
        catch
        {
            decompressed.Dispose();
            throw;
        }
    }

    private static string NameWithoutExtension(string path)
    {
        string name = Path.GetFileName(path);
        foreach (var (ext, replacement) in new[] { (".tgz", ".tar"), (".tbz2", ".tar"), (".tbz", ".tar"), (".txz", ".tar"), (".gz", ""), (".bz2", ""), (".xz", "") })
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && name.Length > ext.Length)
            {
                return name[..^ext.Length] + replacement;
            }
        }

        return name + ".out";
    }

    /// <summary>Nombre original guardado en la cabecera GZip (FNAME), si lo hay y es seguro como nombre.</summary>
    private static string? GZipOriginalName(string path)
    {
        using var fs = OpenFile(path, null);
        Span<byte> h = stackalloc byte[10];
        if (fs.ReadAtLeast(h, 10, throwOnEndOfStream: false) < 10)
        {
            return null;
        }

        byte flags = h[3];
        if ((flags & 0x04) != 0)
        {
            Span<byte> x = stackalloc byte[2];
            if (fs.ReadAtLeast(x, 2, throwOnEndOfStream: false) < 2) return null;
            fs.Seek(x[0] | (x[1] << 8), SeekOrigin.Current);
        }

        if ((flags & 0x08) == 0)
        {
            return null;
        }

        var bytes = new List<byte>();
        int b;
        while ((b = fs.ReadByte()) > 0 && bytes.Count < 1024)
        {
            bytes.Add((byte)b);
        }

        // Latin-1 según RFC 1952; solo el nombre, sin ruta.
        string name = Path.GetFileName(System.Text.Encoding.Latin1.GetString(bytes.ToArray()).Replace('\\', '/').Split('/')[^1]);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    protected virtual void Dispose(bool disposing)
    {
        _disposed = true;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Dispose(true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Contador de bytes leídos del archivo, desde cualquier hilo.</summary>
    internal sealed class ReadCounter
    {
        private long _value;

        public long Value => Interlocked.Read(ref _value);

        public void Add(long bytes) => Interlocked.Add(ref _value, bytes);
    }

    private sealed class CountingReadStream(Stream inner, ReadCounter counter) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            counter.Add(n);
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            counter.Add(n);
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingSeekableStream(Stream inner, ReadCounter counter) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            counter.Add(n);
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingSource(IRandomAccessSource inner, ReadCounter counter) : IRandomAccessSource
    {
        public long Length => inner.Length;

        public int Read(long offset, Span<byte> destination)
        {
            int n = inner.Read(offset, destination);
            counter.Add(n);
            return n;
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            int n = await inner.ReadAsync(offset, destination, cancellationToken).ConfigureAwait(false);
            counter.Add(n);
            return n;
        }

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>Un stream que antes devuelve unos bytes ya leídos y después sigue con el original.</summary>
    internal sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public Stream Inner => rest;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (!_prefix.IsEmpty)
            {
                int n = Math.Min(buffer.Length, _prefix.Length);
                _prefix.Span[..n].CopyTo(buffer);
                _prefix = _prefix[n..];
                return n;
            }

            try
            {
                return rest.Read(buffer);
            }
            catch (InvalidDataException ex)
            {
                throw new CorruptArchiveException($"Datos comprimidos inválidos ({ex.Message}).", ex);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                rest.Dispose();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>ProducerStream que además cierra el archivo de entrada del decodificador.</summary>
    private sealed class OwnedProducerStream(ProducerStream inner, Stream input) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                input.Dispose();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
