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

// Código propio, no derivado de unrar: adapta RarArchive al lector común del núcleo.

using PrZip.Formats;

namespace PrZip.Rar;

/// <summary>
/// RAR (1.4 a 7) a través de <see cref="RarArchive"/>, el port de unrar de este ensamblado. Este adaptador
/// solo traduce las entradas al modelo común. Enlaces y copias de archivo se informan; no se materializan.
/// </summary>
internal sealed class RarArchiveReader : ArchiveReader
{
    private readonly RarArchive _archive;

    public RarArchiveReader(Stream stream, string? password, string path, Func<string, Stream> openVolume)
    {
        _archive = new RarArchive(stream, password, ownsStream: true, path, openVolume);
    }

    /// <summary>
    /// Tamaño total del juego de volúmenes (o del archivo, si no es un volumen), para el progreso: se suman
    /// los volúmenes que existen a partir del que se está leyendo.
    /// </summary>
    protected override long MeasureLength(string path)
    {
        string? name = _archive.VolumeName;
        if (name == null)
        {
            return 0;
        }

        long total = new FileInfo(name).Length;
        if (!_archive.IsVolume)
        {
            return total;
        }

        for (int i = 0; i < 100_000; i++)
        {
            string next = RarVolumes.NextVolumeName(name, !_archive.NewNumbering);
            if (!File.Exists(next))
            {
                break;
            }

            total += new FileInfo(next).Length;
            name = next;
        }

        return total;
    }

    public override string FormatName => "rar";

    public override ArchiveComment? Comment => ToComment(_archive.Comment);

    private static ArchiveComment? ToComment(RarComment? c) => c == null ? null : new ArchiveComment(c.Text, c.Problem);

    public override bool MoveNext()
    {
        while (_archive.MoveNext())
        {
            var e = _archive.Current;
            string name = e.Name.Trim('/');
            if (name.Length == 0)
            {
                continue;
            }

            var kind = e.IsAlternateStream ? ArchiveEntryKind.AlternateStream : e.Redirection switch
            {
                RarRedirection.None => e.IsDirectory ? ArchiveEntryKind.Directory : ArchiveEntryKind.File,
                RarRedirection.UnixSymlink or RarRedirection.WindowsSymlink or RarRedirection.Junction => ArchiveEntryKind.SymbolicLink,
                RarRedirection.HardLink => ArchiveEntryKind.HardLink,
                _ => ArchiveEntryKind.Other
            };

            long size = kind is ArchiveEntryKind.File or ArchiveEntryKind.AlternateStream ? Math.Max(e.Size, 0) : 0;
            Current = new ArchiveEntryInfo(name, kind, size, e.LastWriteTime, e.RedirectionTarget, ToComment(e.Comment));
            return true;
        }

        return false;
    }

    public override void CopyCurrentTo(Stream destination)
    {
        if (Current.Kind == ArchiveEntryKind.File)
        {
            _archive.ExtractCurrent(destination);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _archive.Dispose();
        }

        base.Dispose(disposing);
    }
}
