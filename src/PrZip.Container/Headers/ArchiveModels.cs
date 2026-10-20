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

namespace PrZip.Container.Headers;

/// <summary>
/// Representa un códec o filtro dentro de una carpeta (Folder) de 7z.
/// </summary>
internal sealed class CoderInfo
{
    public byte[] MethodId { get; set; } = [];
    public int NumInStreams { get; set; } = 1;
    public int NumOutStreams { get; set; } = 1;
    public byte[]? Properties { get; set; }
}

/// <summary>
/// Representa una conexión entre un stream de salida y un stream de entrada dentro de un Folder.
/// </summary>
internal readonly struct BindPair(int inIndex, int outIndex)
{
    public int InIndex { get; } = inIndex;
    public int OutIndex { get; } = outIndex;
}

/// <summary>
/// Información de un substream individual dentro de una carpeta (archivo individual en carpeta sólida).
/// </summary>
internal sealed class SubStreamInfo
{
    public long UnpackSize { get; set; }
    public uint? Crc { get; set; }
}

/// <summary>
/// Representa una carpeta de compresión (bloque continuo o sólido) en 7z.
/// Agrupa uno o más flujos codificados a través de un grafo de coders y bind pairs.
/// </summary>
internal sealed class Folder
{
    public List<CoderInfo> Coders { get; } = [];
    public List<BindPair> BindPairs { get; } = [];
    public List<int> PackedStreamIndices { get; } = [];
    public List<long> UnpackSizes { get; } = [];
    public List<SubStreamInfo> SubStreams { get; } = [];
    public uint? Crc { get; set; }

    /// <summary>
    /// Retorna el tamaño final desempaquetado del Folder (el stream de salida que no está bindeado).
    /// </summary>
    public long GetUnpackSize()
    {
        if (UnpackSizes.Count == 0)
        {
            return 0;
        }

        // Buscar el índice del coder cuyo stream de salida no forma parte de ningún BindPair
        for (int i = 0; i < UnpackSizes.Count; i++)
        {
            bool isBound = false;
            foreach (var bp in BindPairs)
            {
                if (bp.OutIndex == i)
                {
                    isBound = true;
                    break;
                }
            }

            if (!isBound)
            {
                return UnpackSizes[i];
            }
        }

        return UnpackSizes[^1];
    }
}

/// <summary>
/// Información de streams empaquetados en el archivo.
/// </summary>
internal sealed class PackInfo
{
    public long PackPos { get; set; }
    public List<long> PackSizes { get; } = [];
    public List<uint?> PackCrcs { get; } = [];
}

/// <summary>
/// Metadatos de un archivo o directorio contenido en el archivo 7z.
/// </summary>
internal sealed class FileItem
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public uint? Crc { get; set; }

    public ulong? MTime { get; set; } // FILETIME crudo de 100 ns
    public ulong? CTime { get; set; }
    public ulong? ATime { get; set; }

    public uint? Attributes { get; set; }

    public bool IsEmptyStream { get; set; } // True para archivos de 0 bytes o carpetas
    public bool IsEmptyFile { get; set; }   // True para archivo de 0 bytes, False para directorio
    public bool IsAnti { get; set; }

    public bool IsDirectory => IsEmptyStream && !IsEmptyFile;

    public int FolderIndex { get; set; } = -1;
    public int SubStreamIndex { get; set; } = -1;
    public long StreamOffset { get; set; }
}

/// <summary>
/// Estructura completa de metadatos deserializados de un archivo 7z.
/// </summary>
internal sealed class ArchiveMetadata
{
    public SignatureHeader Signature { get; set; }
    public PackInfo? PackInfo { get; set; }
    public List<Folder> Folders { get; } = [];
    public List<FileItem> Files { get; } = [];
}
