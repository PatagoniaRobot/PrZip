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

using System.Formats.Tar;
using PrZip.Exceptions;

namespace PrZip.Formats;

/// <summary>
/// TAR (v7, ustar, pax y GNU, incluidos nombres y enlaces largos) sobre el TarReader de .NET, leído de
/// corrido: sirve igual para un .tar que para el stream descomprimido de un .tar.gz/.tar.bz2/.tar.xz.
/// Enlaces, dispositivos y archivos dispersos se informan como tales: no se materializan.
/// </summary>
internal sealed class TarArchiveReader : ArchiveReader
{
    private readonly Stream _stream;
    private readonly TarReader _reader;
    private readonly string _format;
    private TarEntry? _entry;

    public TarArchiveReader(Stream stream, string format)
    {
        _stream = stream;
        _reader = new TarReader(stream, leaveOpen: true);
        _format = format;
    }

    public override string FormatName => _format;

    public override bool MoveNext()
    {
        while (true)
        {
            try
            {
                _entry = _reader.GetNextEntry(copyData: false);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException or ArgumentException)
            {
                throw new CorruptArchiveException($"TAR: cabecera inválida ({ex.Message}).", ex);
            }

            if (_entry == null)
            {
                DrainToEnd();
                return false;
            }

            // Atributos globales pax: metadatos del archivo, no una entrada.
            if (_entry.EntryType == TarEntryType.GlobalExtendedAttributes)
            {
                continue;
            }

            // En v7 no hay tipo directorio: es una entrada común cuyo nombre termina en '/'.
            bool v7Directory = _entry.EntryType == TarEntryType.V7RegularFile && _entry.Name.EndsWith('/');
            var kind = v7Directory ? ArchiveEntryKind.Directory : _entry.EntryType switch
            {
                TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => ArchiveEntryKind.File,
                TarEntryType.Directory => ArchiveEntryKind.Directory,
                TarEntryType.SymbolicLink => ArchiveEntryKind.SymbolicLink,
                TarEntryType.HardLink => ArchiveEntryKind.HardLink,
                _ => ArchiveEntryKind.Other
            };

            string name = NormalizeName(_entry.Name);
            if (name.Length == 0)
            {
                continue; // la entrada "./" es la carpeta raíz misma: no hay nada que crear
            }

            string? link = kind is ArchiveEntryKind.SymbolicLink or ArchiveEntryKind.HardLink ? _entry.LinkName : null;
            Current = new ArchiveEntryInfo(name, kind, kind == ArchiveEntryKind.File ? _entry.Length : 0, _entry.ModificationTime, link);
            return true;
        }
    }

    public override void CopyCurrentTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_entry?.DataStream == null)
        {
            return;
        }

        try
        {
            _entry.DataStream.CopyTo(destination);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new CorruptArchiveException($"TAR: contenido truncado o dañado ({ex.Message}).", ex);
        }
    }

    /// <summary>
    /// Quita lo que no cambia el significado de un nombre TAR: los "./" del comienzo (los deja "tar -c .")
    /// y la barra final de los directorios. Lo demás, incluido lo peligroso, queda tal cual para quien extrae.
    /// </summary>
    internal static string NormalizeName(string name)
    {
        while (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        if (name == ".")
        {
            return string.Empty;
        }

        return name.TrimEnd('/');
    }

    /// <summary>
    /// Lee hasta el final lo que queda después de la marca de fin del TAR. En un .tar.gz, .tar.bz2 o .tar.xz
    /// la verificación del comprimido (CRC de GZip, de bloque en BZip2, de bloque en XZ) está al final:
    /// sin llegar ahí, un bit alterado en el contenido pasaría sin aviso.
    /// </summary>
    private void DrainToEnd()
    {
        var buffer = new byte[1 << 16];
        try
        {
            while (_stream.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }
        catch (InvalidDataException ex)
        {
            throw new CorruptArchiveException($"{_format}: datos comprimidos dañados ({ex.Message}).", ex);
        }
    }

    /// <summary>
    /// ¿Es una cabecera TAR? Con la marca "ustar" (ustar, pax, GNU) o, en el formato v7 que no la tiene,
    /// con la suma de control correcta y un nombre.
    /// </summary>
    internal static bool LooksLikeTar(ReadOnlySpan<byte> header)
    {
        if (header.Length < 512 || header[0] == 0)
        {
            return false;
        }

        long sum = 0;
        for (int i = 0; i < 512; i++)
        {
            sum += i >= 148 && i < 156 ? (byte)' ' : header[i];
        }

        long stored = 0;
        bool digits = false;
        for (int i = 148; i < 156; i++)
        {
            byte c = header[i];
            if (c >= '0' && c <= '7')
            {
                stored = stored * 8 + (c - '0');
                digits = true;
            }
            else if (c == 0 || c == ' ')
            {
                if (digits) break;
            }
            else
            {
                return false;
            }
        }

        bool checksumOk = digits && stored == sum;
        bool ustar = header.Slice(257, 5).SequenceEqual("ustar"u8);
        return checksumOk && (ustar || header[156] is 0 or (byte)'0' or (byte)'5' or (byte)'1' or (byte)'2');
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reader.Dispose();
            _stream.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Un único archivo comprimido con GZip, BZip2 o XZ (sin TAR adentro).</summary>
internal sealed class SingleFileReader(Stream content, string format, string name, DateTimeOffset? lastWrite, long size) : ArchiveReader
{
    private bool _done;

    public SingleFileReader(Stream content, string format, string name, DateTime lastWriteUtc)
        : this(content, format, name, new DateTimeOffset(lastWriteUtc, TimeSpan.Zero), content is ArchiveReader.PrefixedStream p && p.Inner is XzReadStream xz ? xz.UncompressedLength : -1)
    {
    }

    public override string FormatName => format;

    public override bool MoveNext()
    {
        if (_done)
        {
            return false;
        }

        _done = true;
        Current = new ArchiveEntryInfo(name, ArchiveEntryKind.File, size, lastWrite);
        return true;
    }

    public override void CopyCurrentTo(Stream destination) => content.CopyTo(destination);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            content.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Las entradas de un 7z por la misma interfaz.</summary>
internal sealed class Archive7zReader(Archive7z archive) : ArchiveReader
{
    private int _index = -1;

    public override string FormatName => "7z";

    public override bool MoveNext()
    {
        if (++_index >= archive.Entries.Count)
        {
            return false;
        }

        var e = archive.Entries[_index];
        Current = new ArchiveEntryInfo(
            e.Name.Replace('\\', '/'),
            e.IsDirectory ? ArchiveEntryKind.Directory : ArchiveEntryKind.File,
            e.IsDirectory ? 0 : e.Size,
            e.LastWriteTime);
        return true;
    }

    public override void CopyCurrentTo(Stream destination)
    {
        var e = archive.Entries[_index];
        if (!e.IsDirectory)
        {
            archive.Extract(e, destination);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            archive.Dispose();
        }

        base.Dispose(disposing);
    }
}
