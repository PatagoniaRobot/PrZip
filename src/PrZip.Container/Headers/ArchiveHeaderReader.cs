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
using PrZip.Codecs.Crypto;
using PrZip.Container.Extraction;
using PrZip.Exceptions;
using PrZip.Primitives.Checksum;
using PrZip.Primitives.IO;
using PrZip.Primitives.Numeric;

namespace PrZip.Container.Headers;

/// <summary>
/// Deserializador completo de cabeceras 7z según la especificación 7zFormat.txt.
/// Soporta cabeceras codificadas (kEncodedHeader comprimidas con LZMA/LZMA2),
/// grafos de Folders, BindPairs, SubStreamsInfo y FilesInfo.
/// </summary>
internal static class ArchiveHeaderReader
{
    public static ArchiveMetadata Read(IRandomAccessSource source, string? password = null, Aes7zKeyCache? keys = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var signature = SignatureHeader.Read(source);
        var metadata = new ArchiveMetadata
        {
            Signature = signature
        };

        if (signature.NextHeaderSize == 0)
        {
            return metadata; // Archivo 7z vacío
        }

        // Leer el bloque NextHeader
        long nextHeaderAbsOffset = signature.BaseOffset + SignatureHeader.HeaderSize + signature.NextHeaderOffset;
        if (signature.NextHeaderOffset < 0 || signature.NextHeaderSize < 0
            || nextHeaderAbsOffset > source.Length || signature.NextHeaderSize > source.Length - nextHeaderAbsOffset)
        {
            throw new ArchiveTruncatedException(
                $"La cabecera declarada ({signature.NextHeaderSize} bytes en el offset {nextHeaderAbsOffset}) excede el archivo ({source.Length} bytes).");
        }

        if (signature.NextHeaderSize > ReaderLimits.MaxHeaderSize)
        {
            throw new LimitExceededException($"Cabecera de {signature.NextHeaderSize} bytes: supera el máximo admitido ({ReaderLimits.MaxHeaderSize}).");
        }

        byte[] nextHeaderBytes = new byte[signature.NextHeaderSize];
        source.ReadExact(nextHeaderAbsOffset, nextHeaderBytes);

        // Validar CRC del NextHeader
        uint computedCrc = Crc32Algorithm.Compute(nextHeaderBytes);
        if (computedCrc != signature.NextHeaderCrc)
        {
            throw new CorruptArchiveException(
                $"CRC de NextHeader inválido: esperado 0x{signature.NextHeaderCrc:X8}, calculado 0x{computedCrc:X8}.");
        }

        var reader = new ByteReader(nextHeaderBytes);
        ReadHeaderCore(ref reader, source, signature.BaseOffset, metadata, password, keys, depth: 0);

        return metadata;
    }

    private static void ReadHeaderCore(
        ref ByteReader reader,
        IRandomAccessSource source,
        long baseOffset,
        ArchiveMetadata metadata,
        string? password,
        Aes7zKeyCache? keys,
        int depth)
    {
        PropertyId id = (PropertyId)reader.ReadByte();

        if (id == PropertyId.EncodedHeader)
        {
            // La cabecera real está comprimida/codificada en un Folder
            if (depth >= ReaderLimits.MaxEncodedHeaderDepth)
            {
                throw new CorruptArchiveException($"Más de {ReaderLimits.MaxEncodedHeaderDepth} cabeceras codificadas anidadas.");
            }

            byte[] decompressedHeader = ReadEncodedHeader(ref reader, source, baseOffset, password, keys);
            var innerReader = new ByteReader(decompressedHeader);
            ReadHeaderCore(ref innerReader, source, baseOffset, metadata, password, keys, depth + 1);
            return;
        }

        if (id != PropertyId.Header)
        {
            throw new CorruptArchiveException($"Se esperaba tag kHeader (0x01) o kEncodedHeader (0x17), pero se encontró: 0x{(byte)id:X2}.");
        }

        // Parsear tag kHeader (0x01)
        while (!reader.IsEmpty)
        {
            PropertyId pid = (PropertyId)reader.ReadByte();
            if (pid == PropertyId.End)
            {
                break;
            }

            switch (pid)
            {
                case PropertyId.ArchiveProperties:
                case PropertyId.AdditionalStreamsInfo:
                    SkipProperty(ref reader);
                    break;

                case PropertyId.MainStreamsInfo:
                    ReadStreamsInfo(ref reader, metadata);
                    break;

                case PropertyId.FilesInfo:
                    ReadFilesInfo(ref reader, metadata);
                    break;

                default:
                    SkipProperty(ref reader);
                    break;
            }
        }

        // Vincular archivos a sus Folders y SubStreams correspondientes
        LinkFilesToStreams(metadata);
    }

    private static byte[] ReadEncodedHeader(
        ref ByteReader reader,
        IRandomAccessSource source,
        long baseOffset,
        string? password,
        Aes7zKeyCache? keys)
    {
        // En EncodedHeader viene una estructura StreamsInfo que describe el bloque de datos empaquetado
        var tempMeta = new ArchiveMetadata();
        ReadStreamsInfo(ref reader, tempMeta);

        if (tempMeta.Folders.Count == 0 || tempMeta.PackInfo == null)
        {
            throw new CorruptArchiveException("La cabecera codificada no contiene carpetas o información de empaquetado.");
        }

        Folder folder = tempMeta.Folders[0];
        long packOffset = baseOffset + SignatureHeader.HeaderSize + tempMeta.PackInfo.PackPos;
        long packSize = tempMeta.PackInfo.PackSizes.Count > 0 ? tempMeta.PackInfo.PackSizes[0] : source.Length - packOffset;

        if (packOffset < 0 || packSize < 0 || packOffset > source.Length || packSize > source.Length - packOffset)
        {
            throw new ArchiveTruncatedException(
                $"La cabecera codificada ({packSize} bytes en el offset {packOffset}) excede el archivo ({source.Length} bytes).");
        }

        long declaredHeaderSize = folder.GetUnpackSize();
        if (declaredHeaderSize < 0 || declaredHeaderSize > ReaderLimits.MaxHeaderSize)
        {
            throw new LimitExceededException(
                $"Cabecera codificada que declara {declaredHeaderSize} bytes: supera el máximo admitido ({ReaderLimits.MaxHeaderSize}).");
        }

        byte[] packedBytes = new byte[packSize];
        source.ReadExact(packOffset, packedBytes);

        using var packedStream = new MemoryStream(packedBytes, writable: false);
        using var outStream = new MemoryStream();

        long unpackSize = folder.GetUnpackSize();
        FolderDecoder.Decode(folder, packedStream, outStream, unpackSize, password, keys);

        byte[] decompressed = outStream.ToArray();

        if (folder.Crc.HasValue)
        {
            uint crc = Crc32Algorithm.Compute(decompressed);
            if (crc != folder.Crc.Value)
            {
                bool isEncrypted = folder.Coders.Any(c => MethodId.Equals(c.MethodId, MethodId.Aes256));
                if (isEncrypted)
                {
                    throw new InvalidPasswordOrCorruptDataException(
                        "Contraseña incorrecta: el CRC de la cabecera cifrada (-mhe) no coincide.");
                }

                throw new CorruptArchiveException($"CRC de cabecera codificada inválido: esperado 0x{folder.Crc.Value:X8}, calculado 0x{crc:X8}.");
            }
        }

        return decompressed;
    }

    private static void ReadStreamsInfo(ref ByteReader reader, ArchiveMetadata metadata)
    {
        List<long>? subStreamSizes = null;
        List<uint?>? subStreamCrcs = null;
        List<int>? numSubStreamsPerFolder = null;

        while (!reader.IsEmpty)
        {
            PropertyId pid = (PropertyId)reader.ReadByte();
            if (pid == PropertyId.End)
            {
                break;
            }

            switch (pid)
            {
                case PropertyId.PackInfo:
                    metadata.PackInfo = ReadPackInfo(ref reader);
                    break;

                case PropertyId.UnPackInfo:
                    ReadUnPackInfo(ref reader, metadata.Folders);
                    break;

                case PropertyId.SubStreamsInfo:
                    ReadSubStreamsInfo(ref reader, metadata.Folders, out numSubStreamsPerFolder, out subStreamSizes, out subStreamCrcs);
                    break;

                default:
                    SkipProperty(ref reader);
                    break;
            }
        }

        BuildFolderSubStreams(metadata.Folders, numSubStreamsPerFolder, subStreamSizes, subStreamCrcs);
    }

    /// <summary>
    /// Lee una cantidad declarada por el archivo y la acota antes de usarla para reservar o recorrer.
    /// </summary>
    private static int ReadCount(ref ByteReader reader, string what, long max)
    {
        ulong value = VarInt.Read(ref reader);
        long limit = Math.Min(max, int.MaxValue);
        if (value > (ulong)Math.Max(limit, 0))
        {
            throw new CorruptArchiveException($"Cantidad inválida de {what}: {value} (máximo admisible {Math.Max(limit, 0)}).");
        }

        return (int)value;
    }

    private static long ReadSize(ref ByteReader reader, string what)
    {
        ulong value = VarInt.Read(ref reader);
        if (value > long.MaxValue / 2)
        {
            throw new CorruptArchiveException($"Valor inválido para {what}: {value}.");
        }

        return (long)value;
    }

    private static PackInfo ReadPackInfo(ref ByteReader reader)
    {
        var packInfo = new PackInfo
        {
            PackPos = ReadSize(ref reader, "PackPos")
        };

        // Cada stream empaquetado necesita al menos un byte de tamaño.
        int numPackStreams = ReadCount(ref reader, "streams empaquetados", reader.Remaining);

        while (!reader.IsEmpty)
        {
            PropertyId pid = (PropertyId)reader.ReadByte();
            if (pid == PropertyId.End)
            {
                break;
            }

            if (pid == PropertyId.Size)
            {
                for (int i = 0; i < numPackStreams; i++)
                {
                    packInfo.PackSizes.Add(ReadSize(ref reader, "tamaño empaquetado"));
                }
            }
            else if (pid == PropertyId.CRC)
            {
                var crcs = ReadCrcs(ref reader, numPackStreams);
                packInfo.PackCrcs.AddRange(crcs);
            }
            else
            {
                SkipProperty(ref reader);
            }
        }

        return packInfo;
    }

    private static void ReadUnPackInfo(ref ByteReader reader, List<Folder> folders)
    {
        while (!reader.IsEmpty)
        {
            PropertyId pid = (PropertyId)reader.ReadByte();
            if (pid == PropertyId.End)
            {
                break;
            }

            if (pid == PropertyId.Folder)
            {
                int numFolders = ReadCount(ref reader, "folders", reader.Remaining);
                byte external = reader.ReadByte();
                if (external != 0)
                {
                    throw new UnsupportedMethodException("Carpetas externas no soportadas.");
                }

                for (int f = 0; f < numFolders; f++)
                {
                    folders.Add(ReadFolder(ref reader));
                }
            }
            else if (pid == PropertyId.CodersUnPackSize)
            {
                foreach (var folder in folders)
                {
                    int totalOutStreams = 0;
                    foreach (var coder in folder.Coders)
                    {
                        totalOutStreams += coder.NumOutStreams;
                    }

                    for (int i = 0; i < totalOutStreams; i++)
                    {
                        folder.UnpackSizes.Add(ReadSize(ref reader, "tamaño desempaquetado"));
                    }
                }
            }
            else if (pid == PropertyId.CRC)
            {
                var crcs = ReadCrcs(ref reader, folders.Count);
                for (int i = 0; i < folders.Count; i++)
                {
                    folders[i].Crc = crcs[i];
                }
            }
            else
            {
                SkipProperty(ref reader);
            }
        }
    }

    private static Folder ReadFolder(ref ByteReader reader)
    {
        var folder = new Folder();
        int numCoders = ReadCount(ref reader, "coders por folder", ReaderLimits.MaxCodersPerFolder);
        if (numCoders == 0)
        {
            throw new CorruptArchiveException("Folder sin coders.");
        }

        int totalInStreams = 0;
        int totalOutStreams = 0;

        for (int i = 0; i < numCoders; i++)
        {
            byte b = reader.ReadByte();
            int idSize = b & 0x0F;
            bool isComplex = (b & 0x10) != 0;
            bool hasAttributes = (b & 0x20) != 0;

            var coder = new CoderInfo
            {
                MethodId = reader.ReadBytes(idSize).ToArray()
            };

            if (isComplex)
            {
                coder.NumInStreams = ReadCount(ref reader, "entradas de un coder", ReaderLimits.MaxStreamsPerCoder);
                coder.NumOutStreams = ReadCount(ref reader, "salidas de un coder", ReaderLimits.MaxStreamsPerCoder);
            }
            else
            {
                coder.NumInStreams = 1;
                coder.NumOutStreams = 1;
            }

            totalInStreams += coder.NumInStreams;
            totalOutStreams += coder.NumOutStreams;

            if (hasAttributes)
            {
                int propsSize = ReadCount(ref reader, "bytes de propiedades", reader.Remaining);
                coder.Properties = reader.ReadBytes(propsSize).ToArray();
            }

            folder.Coders.Add(coder);
        }

        int numBindPairs = totalOutStreams - 1;
        for (int i = 0; i < numBindPairs; i++)
        {
            int inIndex = ReadCount(ref reader, "índice de entrada de BindPair", totalInStreams - 1);
            int outIndex = ReadCount(ref reader, "índice de salida de BindPair", totalOutStreams - 1);
            folder.BindPairs.Add(new BindPair(inIndex, outIndex));
        }

        int numPackedStreams = totalInStreams - numBindPairs;
        if (numBindPairs < 0 || numPackedStreams < 1)
        {
            throw new CorruptArchiveException($"Folder con un grafo de coders inválido ({totalInStreams} entradas, {totalOutStreams} salidas).");
        }

        if (numPackedStreams == 1)
        {
            // Un solo stream empaquetado: por especificación es la única entrada que no está ligada
            // por un BindPair (con BCJ + LZMA2 de 7-Zip es la entrada 1, no la 0).
            int unbound = -1;
            for (int i = 0; i < totalInStreams && unbound < 0; i++)
            {
                if (!folder.BindPairs.Any(bp => bp.InIndex == i))
                {
                    unbound = i;
                }
            }

            folder.PackedStreamIndices.Add(unbound);
        }
        else
        {
            for (int i = 0; i < numPackedStreams; i++)
            {
                folder.PackedStreamIndices.Add(ReadCount(ref reader, "índice de stream empaquetado", totalInStreams - 1));
            }
        }

        return folder;
    }

    private static void ReadSubStreamsInfo(
        ref ByteReader reader,
        List<Folder> folders,
        out List<int>? numSubStreamsPerFolder,
        out List<long>? subStreamSizes,
        out List<uint?>? subStreamCrcs)
    {
        numSubStreamsPerFolder = null;
        subStreamSizes = null;
        subStreamCrcs = null;

        while (!reader.IsEmpty)
        {
            PropertyId pid = (PropertyId)reader.ReadByte();
            if (pid == PropertyId.End)
            {
                break;
            }

            if (pid == PropertyId.NumUnPackStream)
            {
                // Cada substream más allá del primero de su folder necesita un tamaño (al menos un
                // byte) más adelante: la suma no puede superar los bytes que quedan de cabecera.
                numSubStreamsPerFolder = new List<int>(folders.Count);
                long extraStreams = 0;
                for (int i = 0; i < folders.Count; i++)
                {
                    int n = ReadCount(ref reader, "substreams por folder", reader.Remaining + 1L);
                    extraStreams += Math.Max(0, n - 1);
                    if (extraStreams > reader.Remaining)
                    {
                        throw new CorruptArchiveException("La cabecera declara más substreams de los que puede describir.");
                    }

                    numSubStreamsPerFolder.Add(n);
                }
            }
            else if (pid == PropertyId.Size)
            {
                subStreamSizes = new List<long>();
                for (int f = 0; f < folders.Count; f++)
                {
                    int subStreams = numSubStreamsPerFolder != null ? numSubStreamsPerFolder[f] : 1;
                    if (subStreams <= 0) continue;

                    long sum = 0;
                    for (int s = 0; s < subStreams - 1; s++)
                    {
                        long size = ReadSize(ref reader, "tamaño de substream");
                        subStreamSizes.Add(size);
                        sum += size;
                    }

                    // El último substream deduce su tamaño del UnpackSize del Folder
                    long lastSize = folders[f].GetUnpackSize() - sum;
                    if (sum < 0 || lastSize < 0)
                    {
                        throw new CorruptArchiveException($"Los substreams del folder {f} suman más que el folder.");
                    }
                    subStreamSizes.Add(lastSize);
                }
            }
            else if (pid == PropertyId.CRC)
            {
                int totalStreamsWithCrc = 0;
                for (int f = 0; f < folders.Count; f++)
                {
                    int subStreams = numSubStreamsPerFolder != null ? numSubStreamsPerFolder[f] : 1;
                    if (subStreams != 1 || !folders[f].Crc.HasValue)
                    {
                        totalStreamsWithCrc += subStreams;
                    }
                }

                subStreamCrcs = ReadCrcs(ref reader, totalStreamsWithCrc);
            }
            else
            {
                SkipProperty(ref reader);
            }
        }
    }

    private static void BuildFolderSubStreams(
        List<Folder> folders,
        List<int>? numSubStreamsPerFolder,
        List<long>? subStreamSizes,
        List<uint?>? subStreamCrcs)
    {
        int sizeIndex = 0;
        int crcIndex = 0;

        for (int f = 0; f < folders.Count; f++)
        {
            var folder = folders[f];
            folder.SubStreams.Clear();

            int numSubStreams = numSubStreamsPerFolder != null ? numSubStreamsPerFolder[f] : 1;
            if (numSubStreams <= 0)
            {
                continue;
            }

            if (numSubStreams == 1)
            {
                long size = folder.GetUnpackSize();
                uint? crc = folder.Crc;
                if (!crc.HasValue && subStreamCrcs != null && crcIndex < subStreamCrcs.Count)
                {
                    crc = subStreamCrcs[crcIndex++];
                }
                folder.SubStreams.Add(new SubStreamInfo { UnpackSize = size, Crc = crc });
            }
            else
            {
                for (int s = 0; s < numSubStreams; s++)
                {
                    long size;
                    if (subStreamSizes != null && sizeIndex < subStreamSizes.Count)
                    {
                        size = subStreamSizes[sizeIndex++];
                    }
                    else
                    {
                        size = 0;
                    }

                    uint? crc = null;
                    if (subStreamCrcs != null && crcIndex < subStreamCrcs.Count)
                    {
                        crc = subStreamCrcs[crcIndex++];
                    }

                    folder.SubStreams.Add(new SubStreamInfo { UnpackSize = size, Crc = crc });
                }
            }
        }
    }

    private static void ReadFilesInfo(ref ByteReader reader, ArchiveMetadata metadata)
    {
        // Una entrada sin datos ocupa al menos un bit (vector kEmptyStream); una con datos, un substream.
        long streams = metadata.Folders.Sum(f => (long)Math.Max(1, f.SubStreams.Count));
        int numFiles = ReadCount(ref reader, "entradas", reader.Remaining * 8L + streams);
        var files = new List<FileItem>(numFiles);

        for (int i = 0; i < numFiles; i++)
        {
            files.Add(new FileItem());
        }

        BitVector? emptyStreams = null;
        BitVector? emptyFiles = null;
        BitVector? antiFiles = null;

        while (!reader.IsEmpty)
        {
            PropertyId pid = (PropertyId)reader.ReadByte();
            if (pid == PropertyId.End)
            {
                break;
            }

            ulong propSize = VarInt.Read(ref reader);

            switch (pid)
            {
                case PropertyId.EmptyStream:
                    emptyStreams = BitVector.ReadRaw(ref reader, numFiles);
                    for (int i = 0; i < numFiles; i++)
                    {
                        files[i].IsEmptyStream = emptyStreams.Value[i];
                    }
                    break;

                case PropertyId.EmptyFile:
                    int numEmptyStreams = emptyStreams?.CountTrue() ?? 0;
                    emptyFiles = BitVector.ReadRaw(ref reader, numEmptyStreams);
                    int emptyIdx = 0;
                    for (int i = 0; i < numFiles; i++)
                    {
                        if (files[i].IsEmptyStream)
                        {
                            files[i].IsEmptyFile = emptyFiles.Value[emptyIdx++];
                        }
                    }
                    break;

                case PropertyId.Anti:
                    int numEmptyStreamsAnti = emptyStreams?.CountTrue() ?? 0;
                    antiFiles = BitVector.ReadRaw(ref reader, numEmptyStreamsAnti);
                    int antiIdx = 0;
                    for (int i = 0; i < numFiles; i++)
                    {
                        if (files[i].IsEmptyStream)
                        {
                            files[i].IsAnti = antiFiles.Value[antiIdx++];
                        }
                    }
                    break;

                case PropertyId.Names:
                    byte external = reader.ReadByte();
                    if (external != 0)
                    {
                        throw new UnsupportedMethodException("Nombres de archivo externos no soportados.");
                    }

                    for (int i = 0; i < numFiles; i++)
                    {
                        files[i].Name = reader.ReadNullTerminatedUtf16String();
                    }
                    break;

                case PropertyId.MTime:
                    var mtimeVector = BitVector.ReadWithAllDefined(ref reader, numFiles);
                    byte extMtime = reader.ReadByte();
                    for (int i = 0; i < numFiles; i++)
                    {
                        if (mtimeVector[i])
                        {
                            files[i].MTime = reader.ReadUInt64LittleEndian();
                        }
                    }
                    break;

                case PropertyId.CTime:
                    var ctimeVector = BitVector.ReadWithAllDefined(ref reader, numFiles);
                    byte extCtime = reader.ReadByte();
                    for (int i = 0; i < numFiles; i++)
                    {
                        if (ctimeVector[i])
                        {
                            files[i].CTime = reader.ReadUInt64LittleEndian();
                        }
                    }
                    break;

                case PropertyId.ATime:
                    var atimeVector = BitVector.ReadWithAllDefined(ref reader, numFiles);
                    byte extAtime = reader.ReadByte();
                    for (int i = 0; i < numFiles; i++)
                    {
                        if (atimeVector[i])
                        {
                            files[i].ATime = reader.ReadUInt64LittleEndian();
                        }
                    }
                    break;

                case PropertyId.WinAttributes:
                    var attrVector = BitVector.ReadWithAllDefined(ref reader, numFiles);
                    byte extAttr = reader.ReadByte();
                    for (int i = 0; i < numFiles; i++)
                    {
                        if (attrVector[i])
                        {
                            files[i].Attributes = reader.ReadUInt32LittleEndian();
                        }
                    }
                    break;

                default:
                    reader.Skip((int)propSize);
                    break;
            }
        }

        metadata.Files.AddRange(files);
    }

    private static void LinkFilesToStreams(ArchiveMetadata metadata)
    {
        int folderIdx = 0;
        int subStreamIdx = 0;
        long currentOffsetInFolder = 0;

        foreach (var file in metadata.Files)
        {
            if (file.IsEmptyStream)
            {
                file.Size = 0;
                file.Crc = null;
                file.FolderIndex = -1;
                file.SubStreamIndex = -1;
                file.StreamOffset = 0;
                continue;
            }

            while (folderIdx < metadata.Folders.Count && subStreamIdx >= metadata.Folders[folderIdx].SubStreams.Count)
            {
                folderIdx++;
                subStreamIdx = 0;
                currentOffsetInFolder = 0;
            }

            if (folderIdx >= metadata.Folders.Count)
            {
                file.FolderIndex = -1;
                file.SubStreamIndex = -1;
                file.Size = 0;
                file.Crc = null;
                file.StreamOffset = 0;
                continue;
            }

            var folder = metadata.Folders[folderIdx];
            var subStream = folder.SubStreams[subStreamIdx];

            file.FolderIndex = folderIdx;
            file.SubStreamIndex = subStreamIdx;
            file.StreamOffset = currentOffsetInFolder;
            file.Size = subStream.UnpackSize;
            file.Crc = subStream.Crc;

            currentOffsetInFolder += subStream.UnpackSize;
            subStreamIdx++;
        }
    }

    private static List<uint?> ReadCrcs(ref ByteReader reader, int count)
    {
        var definedVector = BitVector.ReadWithAllDefined(ref reader, count);
        var crcs = new List<uint?>(count);

        for (int i = 0; i < count; i++)
        {
            if (definedVector[i])
            {
                crcs.Add(reader.ReadUInt32LittleEndian());
            }
            else
            {
                crcs.Add(null);
            }
        }

        return crcs;
    }

    private static void SkipProperty(ref ByteReader reader)
    {
        ulong size = VarInt.Read(ref reader);
        reader.Skip((int)size);
    }
}
