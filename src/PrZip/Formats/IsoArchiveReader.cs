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
using System.Text;
using PrZip.Exceptions;
using PrZip.Primitives.IO;

namespace PrZip.Formats;

/// <summary>
/// Imágenes ISO 9660 (ECMA-119), con los nombres largos de Joliet cuando el disco los trae. Recorre el árbol
/// de directorios al abrir, con defensas ante imágenes hostiles: directorios que se repiten (bucles),
/// profundidad, extensiones que salen de la imagen y registros malformados. Los archivos de varias
/// extensiones se leen completos. Rock Ridge y UDF no se interpretan: un disco solo UDF se rechaza.
/// </summary>
internal sealed class IsoArchiveReader : ArchiveReader
{
    private const int SectorSize = 2048;
    private const int MaxDepth = 64;
    private const int MaxEntries = 10_000_000;
    private const long MaxDirectorySize = 64L << 20;

    /// <summary>
    /// Cuánto pueden sumar los archivos respecto de la imagen. Varias entradas pueden apuntar a la misma
    /// extensión (es legítimo: así se deduplican archivos idénticos), pero sin tope una imagen de 1 GB con un
    /// millón de registros sobre la misma extensión "contenía" un petabyte y llenaba el disco al extraerla.
    /// </summary>
    private const long MaxExpansionFactor = 100;

    /// <summary>Piso del tope, para no molestar a imágenes chicas con algunos duplicados.</summary>
    private const long MinExpansionLimit = 64L << 20;

    private readonly IRandomAccessSource _source;
    private readonly bool _ownsSource;
    private readonly List<IsoEntry> _entries = [];
    private int _index = -1;

    public IsoArchiveReader(IRandomAccessSource source, bool ownsSource)
    {
        _source = source;
        _ownsSource = ownsSource;
        try
        {
            var (rootLba, rootLength, joliet) = ReadVolumeDescriptors();
            FormatName = joliet ? "iso (Joliet)" : "iso";
            ReadTree(rootLba, rootLength, joliet);
            CheckExpansion();
        }
        catch
        {
            if (ownsSource)
            {
                source.Dispose();
            }

            throw;
        }
    }

    public override string FormatName { get; }

    public override bool MoveNext()
    {
        if (++_index >= _entries.Count)
        {
            return false;
        }

        var e = _entries[_index];
        Current = new ArchiveEntryInfo(e.Path, e.Kind, e.Size, e.Time);
        return true;
    }

    public override void CopyCurrentTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var e = _entries[_index];
        if (e.Kind != ArchiveEntryKind.File)
        {
            return;
        }

        var buffer = new byte[1 << 16];
        foreach (var (lba, length) in e.Extents)
        {
            long offset = lba * SectorSize;
            long remaining = length;
            while (remaining > 0)
            {
                int n = (int)Math.Min(buffer.Length, remaining);
                _source.ReadExact(offset, buffer.AsSpan(0, n));
                destination.Write(buffer, 0, n);
                offset += n;
                remaining -= n;
            }
        }
    }

    /// <summary>
    /// Descriptores de volumen desde el sector 16: el primario y, si hay, el suplementario de Joliet
    /// (secuencias de escape %/@, %/C o %/E), que es el que se prefiere por sus nombres completos.
    /// </summary>
    private (long Lba, long Length, bool Joliet) ReadVolumeDescriptors()
    {
        var vd = new byte[SectorSize];
        (long, long)? primary = null, joliet = null;
        for (int sector = 16; sector < 16 + 64; sector++)
        {
            if ((long)(sector + 1) * SectorSize > _source.Length)
            {
                break;
            }

            _source.ReadExact((long)sector * SectorSize, vd);
            if (!vd.AsSpan(1, 5).SequenceEqual("CD001"u8))
            {
                break;
            }

            byte type = vd[0];
            if (type == 255)
            {
                break;
            }

            if (type == 1 && primary == null)
            {
                primary = RootOf(vd);
            }
            else if (type == 2 && vd[88] == '%' && vd[89] == '/' && vd[90] is (byte)'@' or (byte)'C' or (byte)'E')
            {
                joliet ??= RootOf(vd);
            }
        }

        if (joliet is var (jl, jlen))
        {
            return (jl, jlen, true);
        }

        if (primary is var (pl, plen))
        {
            return (pl, plen, false);
        }

        throw new UnsupportedMethodException("ISO: sin descriptor de volumen ISO 9660 (¿disco solo UDF?).");
    }

    private static (long, long) RootOf(byte[] vd)
    {
        // Registro de directorio raíz: 34 bytes desde el byte 156.
        long lba = BinaryPrimitives.ReadUInt32LittleEndian(vd.AsSpan(156 + 2));
        long length = BinaryPrimitives.ReadUInt32LittleEndian(vd.AsSpan(156 + 10));
        return (lba, length);
    }

    private void ReadTree(long rootLba, long rootLength, bool joliet)
    {
        var visited = new HashSet<long>();
        var pending = new Stack<(string Path, long Lba, long Length, int Depth)>();
        pending.Push((string.Empty, rootLba, rootLength, 0));

        while (pending.Count > 0)
        {
            var (dirPath, lba, length, depth) = pending.Pop();
            if (!visited.Add(lba))
            {
                throw new CorruptArchiveException($"ISO: el directorio '{dirPath}' se repite (bucle en la imagen).");
            }

            if (depth > MaxDepth)
            {
                throw new LimitExceededException($"ISO: más de {MaxDepth} niveles de directorios.");
            }

            byte[] data = ReadExtent(lba, length, MaxDirectorySize, dirPath);
            var children = new List<(string Path, long Lba, long Length)>();
            var multi = new List<(long Lba, long Length)>();

            int pos = 0;
            while (pos < data.Length)
            {
                int recordLength = data[pos];
                if (recordLength == 0)
                {
                    // Los registros no cruzan sectores: el resto del sector es relleno.
                    pos = (pos / SectorSize + 1) * SectorSize;
                    continue;
                }

                if (recordLength < 34 || pos + recordLength > data.Length)
                {
                    throw new CorruptArchiveException($"ISO: registro de directorio inválido en '{dirPath}'.");
                }

                var record = data.AsSpan(pos, recordLength);
                pos += recordLength;

                int nameLength = record[32];
                if (33 + nameLength > recordLength)
                {
                    throw new CorruptArchiveException($"ISO: nombre fuera del registro en '{dirPath}'.");
                }

                var rawName = record.Slice(33, nameLength);
                if (nameLength == 1 && rawName[0] is 0 or 1)
                {
                    continue; // "." y ".."
                }

                byte flags = record[25];
                long extentLba = BinaryPrimitives.ReadUInt32LittleEndian(record[2..]);
                long extentLength = BinaryPrimitives.ReadUInt32LittleEndian(record[10..]);
                bool isDirectory = (flags & 0x02) != 0;
                bool moreExtents = (flags & 0x80) != 0;
                bool interleaved = record[26] != 0 || record[27] != 0;

                string name = DecodeName(rawName, joliet, isDirectory);
                string path = dirPath.Length == 0 ? name : dirPath + "/" + name;

                if (_entries.Count >= MaxEntries)
                {
                    throw new LimitExceededException($"ISO: más de {MaxEntries:N0} entradas.");
                }

                if (isDirectory)
                {
                    _entries.Add(new IsoEntry(path, ArchiveEntryKind.Directory, [], 0, ParseTime(record.Slice(18, 7))));
                    children.Add((path, extentLba, extentLength));
                    continue;
                }

                CheckInside(extentLba, extentLength, path);
                multi.Add((extentLba, extentLength));
                if (moreExtents)
                {
                    continue; // las extensiones siguientes vienen en registros con el mismo nombre
                }

                var extents = multi.ToList();
                multi.Clear();
                var kind = interleaved ? ArchiveEntryKind.Other : ArchiveEntryKind.File;
                _entries.Add(new IsoEntry(path, kind, extents, extents.Sum(x => x.Length), ParseTime(record.Slice(18, 7))));
            }

            if (multi.Count > 0)
            {
                throw new CorruptArchiveException($"ISO: archivo de varias extensiones incompleto en '{dirPath}'.");
            }

            // Orden natural al recorrer: el primer subdirectorio sale primero de la pila.
            for (int i = children.Count - 1; i >= 0; i--)
            {
                pending.Push((children[i].Path, children[i].Lba, children[i].Length, depth + 1));
            }
        }
    }

    private void CheckExpansion()
    {
        long total = 0;
        foreach (var e in _entries)
        {
            if (e.Kind == ArchiveEntryKind.File)
            {
                total += e.Size;
            }
        }

        long limit = Math.Max(MaxExpansionFactor * _source.Length, MinExpansionLimit);
        if (total > limit)
        {
            throw new LimitExceededException(
                $"ISO: los archivos suman {total:N0} bytes en una imagen de {_source.Length:N0}: muchas entradas " +
                "apuntan a los mismos datos (imagen armada para llenar el disco).");
        }
    }

    private byte[] ReadExtent(long lba, long length, long limit, string what)
    {
        if (length > limit)
        {
            throw new LimitExceededException($"ISO: el directorio '{what}' declara {length:N0} bytes.");
        }

        CheckInside(lba, length, what);
        var data = new byte[length];
        _source.ReadExact(lba * SectorSize, data);
        return data;
    }

    private void CheckInside(long lba, long length, string what)
    {
        // Un archivo vacío no ocupa sectores: su posición no significa nada (libarchive pone cualquiera).
        if (length == 0)
        {
            return;
        }

        if (lba < 0 || length < 0 || lba * SectorSize + length > _source.Length)
        {
            throw new CorruptArchiveException($"ISO: '{what}' apunta fuera de la imagen.");
        }
    }

    /// <summary>Joliet: UCS-2 big endian. ISO 9660: ASCII, sin el ";1" de versión ni el punto final.</summary>
    private static string DecodeName(ReadOnlySpan<byte> raw, bool joliet, bool isDirectory)
    {
        string name = joliet ? Encoding.BigEndianUnicode.GetString(raw[..(raw.Length & ~1)]) : Encoding.ASCII.GetString(raw);
        if (!isDirectory)
        {
            int semicolon = name.LastIndexOf(';');
            if (semicolon >= 0)
            {
                name = name[..semicolon];
            }

            if (name.EndsWith('.') && name.Length > 1)
            {
                name = name[..^1];
            }
        }

        return name.Length == 0 ? "_" : name;
    }

    /// <summary>Fecha de registro: años desde 1900, mes, día, hora, minuto, segundo y huso en cuartos de hora.</summary>
    private static DateTimeOffset? ParseTime(ReadOnlySpan<byte> t)
    {
        int year = 1900 + t[0], month = t[1], day = t[2], hour = t[3], minute = t[4], second = t[5];
        int offsetQuarters = (sbyte)t[6];
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59
            || offsetQuarters is < -48 or > 52)
        {
            return null;
        }

        return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromMinutes(offsetQuarters * 15));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsSource)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record IsoEntry(string Path, ArchiveEntryKind Kind, List<(long Lba, long Length)> Extents, long Size, DateTimeOffset? Time);
}
