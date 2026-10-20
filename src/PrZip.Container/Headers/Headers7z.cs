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
/// Identificadores de propiedad (Property IDs / Tags TLV) del formato de contenedor 7z.
/// Según la especificación oficial 7zFormat.txt de Igor Pavlov.
/// </summary>
internal enum PropertyId : byte
{
    End = 0x00,
    Header = 0x01,
    ArchiveProperties = 0x02,
    AdditionalStreamsInfo = 0x03,
    MainStreamsInfo = 0x04,
    FilesInfo = 0x05,
    PackInfo = 0x06,
    UnPackInfo = 0x07,
    SubStreamsInfo = 0x08,
    Size = 0x09,
    CRC = 0x0A,
    Folder = 0x0B,
    CodersUnPackSize = 0x0C,
    NumUnPackStream = 0x0D,
    EmptyStream = 0x0E,
    EmptyFile = 0x0F,
    Anti = 0x10,
    Names = 0x11,
    CTime = 0x12,
    ATime = 0x13,
    MTime = 0x14,
    WinAttributes = 0x15,
    Comment = 0x16,
    EncodedHeader = 0x17,
    StartPos = 0x18,
    Dummy = 0x19
}

/// <summary>
/// Identificadores canónicos de métodos de compresión y filtros en 7z.
/// </summary>
internal static class MethodId
{
    public static readonly byte[] Copy = [0x00];
    public static readonly byte[] Delta = [0x03];
    public static readonly byte[] Lzma2 = [0x21];
    public static readonly byte[] Lzma = [0x03, 0x01, 0x01];
    public static readonly byte[] Ppmd = [0x03, 0x04, 0x01];
    public static readonly byte[] BcjX86 = [0x03, 0x03, 0x01, 0x03];
    public static readonly byte[] BcjArm = [0x03, 0x03, 0x05, 0x01];
    public static readonly byte[] BcjArm64 = [0x0A];
    public static readonly byte[] BcjArmThumb = [0x03, 0x03, 0x07, 0x01];
    public static readonly byte[] BcjPpc = [0x03, 0x03, 0x02, 0x05];
    public static readonly byte[] BcjSparc = [0x03, 0x03, 0x08, 0x05];
    public static readonly byte[] BcjIa64 = [0x03, 0x03, 0x04, 0x01];
    public static readonly byte[] BcjRiscV = [0x0B];
    public static readonly byte[] Bcj2 = [0x03, 0x03, 0x01, 0x1B];
    public static readonly byte[] Aes256 = [0x06, 0xF1, 0x07, 0x01];
    public static readonly byte[] BZip2 = [0x04, 0x02, 0x02];
    public static readonly byte[] Deflate = [0x04, 0x01, 0x08];
    public static readonly byte[] Deflate64 = [0x04, 0x01, 0x09];

    public static bool Equals(ReadOnlySpan<byte> id1, ReadOnlySpan<byte> id2)
    {
        return id1.SequenceEqual(id2);
    }
}
