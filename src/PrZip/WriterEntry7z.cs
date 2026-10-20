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

namespace PrZip;

/// <summary>
/// Metadatos de un archivo o directorio que se añadirá a un archivo 7z mediante Writer7z.
/// </summary>
public sealed class WriterEntry7z
{
    public string Name { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public DateTimeOffset? LastWriteTime { get; set; }
    public DateTimeOffset? CreationTime { get; set; }
    public FileAttributes? Attributes { get; set; }

    public static WriterEntry7z FromFile(string filePath, string entryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        var fi = new FileInfo(filePath);
        return new WriterEntry7z
        {
            Name = entryName.Replace('\\', '/'),
            IsDirectory = false,
            LastWriteTime = fi.LastWriteTimeUtc,
            CreationTime = fi.CreationTimeUtc,
            Attributes = fi.Attributes
        };
    }

    public static WriterEntry7z FromDirectory(string directoryPath, string entryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        var di = new DirectoryInfo(directoryPath);
        string normalizedName = entryName.Replace('\\', '/');
        if (!normalizedName.EndsWith('/'))
        {
            normalizedName += "/";
        }

        return new WriterEntry7z
        {
            Name = normalizedName,
            IsDirectory = true,
            LastWriteTime = di.LastWriteTimeUtc,
            CreationTime = di.CreationTimeUtc,
            Attributes = di.Attributes
        };
    }
}
