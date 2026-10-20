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

using PrZip.Container.Headers;

namespace PrZip;

/// <summary>
/// Representa una entrada (archivo o carpeta) contenida dentro de un archivo 7z.
/// </summary>
public sealed class Entry7z
{
    private readonly FileItem _item;

    internal Entry7z(FileItem item, int index, bool isEncrypted = false)
    {
        _item = item;
        Index = index;
        IsEncrypted = isEncrypted;
    }

    internal FileItem Item => _item;

    public int Index { get; }

    public bool IsEncrypted { get; }

    public string Name => _item.Name;

    public long Size => _item.Size;

    public uint? Crc => _item.Crc;

    public bool IsDirectory => _item.IsDirectory;

    public bool IsEmptyStream => _item.IsEmptyStream;

    public DateTimeOffset? LastWriteTime => _item.MTime.HasValue ? DateTimeOffset.FromFileTime((long)_item.MTime.Value) : null;

    public DateTimeOffset? CreationTime => _item.CTime.HasValue ? DateTimeOffset.FromFileTime((long)_item.CTime.Value) : null;

    public DateTimeOffset? LastAccessTime => _item.ATime.HasValue ? DateTimeOffset.FromFileTime((long)_item.ATime.Value) : null;

    public ulong? RawMTime => _item.MTime;

    public FileAttributes? Attributes => _item.Attributes.HasValue ? (FileAttributes)_item.Attributes.Value : null;

    public override string ToString() => $"{Name} ({(IsDirectory ? "Dir" : $"{Size:N0} bytes")})";
}
