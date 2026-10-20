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
using PrZip.Exceptions;
using PrZip.Primitives.Checksum;
using PrZip.Primitives.Numeric;

namespace PrZip.Container.Headers;

/// <summary>
/// Serializador de cabeceras 7z según la especificación oficial DOC/7zFormat.txt de Igor Pavlov.
/// Escribe las estructuras Header, MainStreamsInfo, PackInfo, UnPackInfo, SubStreamsInfo y FilesInfo.
/// </summary>
internal static class ArchiveHeaderWriter
{
    public static byte[] WriteHeader(
        IReadOnlyList<Folder> folders,
        IReadOnlyList<FileItem> files,
        PackInfo packInfo,
        IReadOnlyList<int>? numSubStreamsPerFolder = null,
        IReadOnlyList<long>? subStreamSizes = null,
        IReadOnlyList<uint?>? subStreamCrcs = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(packInfo);

        using var ms = new MemoryStream();

        // 1. Tag inicial: kHeader (0x01)
        ms.WriteByte((byte)PropertyId.Header);

        // 2. MainStreamsInfo (0x04) si existen carpetas con datos
        if (folders.Count > 0)
        {
            WriteMainStreamsInfo(ms, folders, packInfo, numSubStreamsPerFolder, subStreamSizes, subStreamCrcs);
        }

        // 3. FilesInfo (0x05)
        WriteFilesInfo(ms, files);

        // 4. Tag final del Header: kEnd (0x00)
        ms.WriteByte((byte)PropertyId.End);

        return ms.ToArray();
    }

    public static byte[] WriteEncodedHeader(Folder folder, PackInfo packInfo)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(packInfo);

        using var ms = new MemoryStream();
        ms.WriteByte((byte)PropertyId.EncodedHeader);

        WritePackInfo(ms, packInfo);
        WriteUnPackInfo(ms, [folder]);

        ms.WriteByte((byte)PropertyId.End);
        return ms.ToArray();
    }

    private static void WriteMainStreamsInfo(
        MemoryStream ms,
        IReadOnlyList<Folder> folders,
        PackInfo packInfo,
        IReadOnlyList<int>? numSubStreamsPerFolder,
        IReadOnlyList<long>? subStreamSizes,
        IReadOnlyList<uint?>? subStreamCrcs)
    {
        ms.WriteByte((byte)PropertyId.MainStreamsInfo);

        WritePackInfo(ms, packInfo);
        WriteUnPackInfo(ms, folders);

        // 2.3 SubStreamsInfo (0x08)
        // 7-Zip toma de aquí los CRC por archivo que verifica al extraer: sin esta sección,
        // un archivo con un folder por entrada queda sin ningún CRC visible para 7z.exe.
        bool hasMultipleSubStreams = (numSubStreamsPerFolder != null && numSubStreamsPerFolder.Any(n => n != 1))
                                     || (subStreamSizes != null && subStreamSizes.Count > 0)
                                     || (subStreamCrcs != null && subStreamCrcs.Count > 0);

        if (hasMultipleSubStreams)
        {
            WriteSubStreamsInfo(ms, folders, numSubStreamsPerFolder, subStreamSizes, subStreamCrcs);
        }

        ms.WriteByte((byte)PropertyId.End);
    }

    private static void WritePackInfo(MemoryStream ms, PackInfo packInfo)
    {
        ms.WriteByte((byte)PropertyId.PackInfo);
        WriteVarInt(ms, (ulong)packInfo.PackPos);
        WriteVarInt(ms, (ulong)packInfo.PackSizes.Count);

        // kSize (0x09)
        ms.WriteByte((byte)PropertyId.Size);
        foreach (long pSize in packInfo.PackSizes)
        {
            WriteVarInt(ms, (ulong)pSize);
        }

        // kCRC opcional para pack streams si están definidos
        if (packInfo.PackCrcs.Count > 0 && packInfo.PackCrcs.Count == packInfo.PackSizes.Count)
        {
            ms.WriteByte((byte)PropertyId.CRC);
            ms.WriteByte(1); // AllAreDefined = 1
            Span<byte> crcBuf = stackalloc byte[4];
            foreach (uint? crc in packInfo.PackCrcs)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, crc ?? 0);
                ms.Write(crcBuf);
            }
        }

        ms.WriteByte((byte)PropertyId.End);
    }

    private static void WriteUnPackInfo(MemoryStream ms, IReadOnlyList<Folder> folders)
    {
        ms.WriteByte((byte)PropertyId.UnPackInfo);
        ms.WriteByte((byte)PropertyId.Folder);
        WriteVarInt(ms, (ulong)folders.Count);
        ms.WriteByte(0x00); // External = 0

        foreach (var folder in folders)
        {
            WriteVarInt(ms, (ulong)folder.Coders.Count);
            foreach (var coder in folder.Coders)
            {
                byte idSize = (byte)(coder.MethodId.Length & 0x0F);
                bool isComplex = coder.NumInStreams != 1 || coder.NumOutStreams != 1;
                bool hasProps = coder.Properties is { Length: > 0 };

                byte headerByte = idSize;
                if (isComplex) headerByte |= 0x10;
                if (hasProps) headerByte |= 0x20;

                ms.WriteByte(headerByte);
                ms.Write(coder.MethodId);

                if (isComplex)
                {
                    WriteVarInt(ms, (ulong)coder.NumInStreams);
                    WriteVarInt(ms, (ulong)coder.NumOutStreams);
                }

                if (hasProps)
                {
                    WriteVarInt(ms, (ulong)coder.Properties!.Length);
                    ms.Write(coder.Properties);
                }
            }

            // BindPairs
            int numBindPairs = folder.BindPairs.Count;
            for (int i = 0; i < numBindPairs; i++)
            {
                WriteVarInt(ms, (ulong)folder.BindPairs[i].InIndex);
                WriteVarInt(ms, (ulong)folder.BindPairs[i].OutIndex);
            }

            // PackedStreams si hay más de 1
            if (folder.PackedStreamIndices.Count > 1)
            {
                foreach (int idx in folder.PackedStreamIndices)
                {
                    WriteVarInt(ms, (ulong)idx);
                }
            }
        }

        // kCodersUnPackSize (0x0C)
        ms.WriteByte((byte)PropertyId.CodersUnPackSize);
        foreach (var folder in folders)
        {
            foreach (long uSize in folder.UnpackSizes)
            {
                WriteVarInt(ms, (ulong)uSize);
            }
        }

        // kCRC de las carpetas si están definidos
        bool anyFolderCrc = folders.Any(f => f.Crc.HasValue);
        if (anyFolderCrc)
        {
            ms.WriteByte((byte)PropertyId.CRC);
            bool allDefined = folders.All(f => f.Crc.HasValue);
            if (allDefined)
            {
                ms.WriteByte(1); // AllAreDefined = 1
            }
            else
            {
                ms.WriteByte(0);
                WriteBitMask(ms, folders.Select(f => f.Crc.HasValue).ToArray());
            }

            Span<byte> crcBuf = stackalloc byte[4];
            foreach (var folder in folders)
            {
                if (folder.Crc.HasValue)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, folder.Crc.Value);
                    ms.Write(crcBuf);
                }
            }
        }

        ms.WriteByte((byte)PropertyId.End);
    }

    private static void WriteSubStreamsInfo(
        MemoryStream ms,
        IReadOnlyList<Folder> folders,
        IReadOnlyList<int>? numSubStreamsPerFolder,
        IReadOnlyList<long>? subStreamSizes,
        IReadOnlyList<uint?>? subStreamCrcs)
    {
        ms.WriteByte((byte)PropertyId.SubStreamsInfo);

        if (numSubStreamsPerFolder != null && numSubStreamsPerFolder.Any(n => n != 1))
        {
            ms.WriteByte((byte)PropertyId.NumUnPackStream);
            foreach (int count in numSubStreamsPerFolder)
            {
                WriteVarInt(ms, (ulong)count);
            }
        }

        if (subStreamSizes != null && subStreamSizes.Count > 0)
        {
            ms.WriteByte((byte)PropertyId.Size);
            int offset = 0;
            for (int f = 0; f < folders.Count; f++)
            {
                int count = numSubStreamsPerFolder != null ? numSubStreamsPerFolder[f] : 1;
                for (int s = 0; s < count - 1; s++)
                {
                    WriteVarInt(ms, (ulong)subStreamSizes[offset + s]);
                }
                offset += count;
            }
        }

        if (subStreamCrcs != null && subStreamCrcs.Count > 0)
        {
            ms.WriteByte((byte)PropertyId.CRC);
            bool allDefined = subStreamCrcs.All(c => c.HasValue);
            if (allDefined)
            {
                ms.WriteByte(1);
            }
            else
            {
                ms.WriteByte(0);
                WriteBitMask(ms, subStreamCrcs.Select(c => c.HasValue).ToArray());
            }

            Span<byte> crcBuf = stackalloc byte[4];
            foreach (var crc in subStreamCrcs)
            {
                if (crc.HasValue)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, crc.Value);
                    ms.Write(crcBuf);
                }
            }
        }

        ms.WriteByte((byte)PropertyId.End);
    }

    private static void WriteFilesInfo(MemoryStream ms, IReadOnlyList<FileItem> files)
    {
        ms.WriteByte((byte)PropertyId.FilesInfo);
        WriteVarInt(ms, (ulong)files.Count);

        if (files.Count == 0)
        {
            ms.WriteByte((byte)PropertyId.End);
            return;
        }

        // 3.1 kEmptyStream (0x0E)
        bool hasEmptyStream = files.Any(f => f.IsEmptyStream);
        if (hasEmptyStream)
        {
            ms.WriteByte((byte)PropertyId.EmptyStream);
            bool[] emptyStreamBits = files.Select(f => f.IsEmptyStream).ToArray();
            int emptyStreamBytes = (emptyStreamBits.Length + 7) >> 3;
            WriteVarInt(ms, (ulong)emptyStreamBytes);
            WriteBitVectorRaw(ms, emptyStreamBits);

            // 3.2 kEmptyFile (0x0F) - entre los que son EmptyStream, cuáles son archivos vs directorios
            var emptyStreams = files.Where(f => f.IsEmptyStream).ToList();
            bool hasEmptyFile = emptyStreams.Any(f => !f.IsDirectory);
            if (hasEmptyFile)
            {
                ms.WriteByte((byte)PropertyId.EmptyFile);
                bool[] emptyFileBits = emptyStreams.Select(f => !f.IsDirectory).ToArray();
                int emptyFileBytes = (emptyFileBits.Length + 7) >> 3;
                WriteVarInt(ms, (ulong)emptyFileBytes);
                WriteBitVectorRaw(ms, emptyFileBits);
            }
        }

        // 3.3 kNames (0x11)
        ms.WriteByte((byte)PropertyId.Names);
        using (var namesMs = new MemoryStream())
        {
            namesMs.WriteByte(0x00); // External = 0
            Span<byte> charBuf = stackalloc byte[2];

            foreach (var file in files)
            {
                foreach (char c in file.Name)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(charBuf, (ushort)c);
                    namesMs.Write(charBuf);
                }
                // Terminador nulo UTF-16
                BinaryPrimitives.WriteUInt16LittleEndian(charBuf, 0);
                namesMs.Write(charBuf);
            }

            byte[] namesData = namesMs.ToArray();
            WriteVarInt(ms, (ulong)namesData.Length);
            ms.Write(namesData);
        }

        // 3.4 kMTime (0x14)
        bool hasMTime = files.Any(f => f.MTime.HasValue);
        if (hasMTime)
        {
            ms.WriteByte((byte)PropertyId.MTime);
            using var timeMs = new MemoryStream();
            bool allDefined = files.All(f => f.MTime.HasValue);
            if (allDefined)
            {
                timeMs.WriteByte(1); // AllAreDefined = 1
            }
            else
            {
                timeMs.WriteByte(0);
                WriteBitMask(timeMs, files.Select(f => f.MTime.HasValue).ToArray());
            }

            timeMs.WriteByte(0x00); // External = 0
            Span<byte> timeBuf = stackalloc byte[8];
            foreach (var file in files)
            {
                if (file.MTime.HasValue)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(timeBuf, file.MTime.Value);
                    timeMs.Write(timeBuf);
                }
            }

            byte[] timeData = timeMs.ToArray();
            WriteVarInt(ms, (ulong)timeData.Length);
            ms.Write(timeData);
        }

        // 3.5 kWinAttributes (0x15)
        bool hasAttributes = files.Any(f => f.Attributes.HasValue);
        if (hasAttributes)
        {
            ms.WriteByte((byte)PropertyId.WinAttributes);
            using var attrMs = new MemoryStream();
            bool allDefined = files.All(f => f.Attributes.HasValue);
            if (allDefined)
            {
                attrMs.WriteByte(1); // AllAreDefined = 1
            }
            else
            {
                attrMs.WriteByte(0);
                WriteBitMask(attrMs, files.Select(f => f.Attributes.HasValue).ToArray());
            }

            attrMs.WriteByte(0x00); // External = 0
            Span<byte> attrBuf = stackalloc byte[4];
            foreach (var file in files)
            {
                if (file.Attributes.HasValue)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(attrBuf, file.Attributes.Value);
                    attrMs.Write(attrBuf);
                }
            }

            byte[] attrData = attrMs.ToArray();
            WriteVarInt(ms, (ulong)attrData.Length);
            ms.Write(attrData);
        }

        ms.WriteByte((byte)PropertyId.End);
    }

    private static void WriteVarInt(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[9];
        int count = VarInt.Write(buffer, value);
        stream.Write(buffer[..count]);
    }

    private static void WriteBitVectorRaw(Stream stream, ReadOnlySpan<bool> bits)
    {
        int byteCount = (bits.Length + 7) >> 3;
        Span<byte> bytes = stackalloc byte[byteCount];
        bytes.Clear();

        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
        }

        stream.Write(bytes);
    }

    private static void WriteBitMask(Stream stream, ReadOnlySpan<bool> bits)
    {
        WriteBitVectorRaw(stream, bits);
    }
}
