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
using Xunit;

namespace PrZip.Tests;

public class ByteReaderTests
{
    [Fact]
    public void ReadPrimitives_LittleEndian_Success()
    {
        byte[] data = new byte[15];
        data[0] = 0x42;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(1, 2), 0x1234);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(3, 4), 0x89ABCDEF);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(7, 8), 0x0102030405060708);

        var reader = new ByteReader(data);

        Assert.Equal(0, reader.Position);
        Assert.Equal(15, reader.Remaining);
        Assert.False(reader.IsEmpty);

        Assert.Equal(0x42, reader.ReadByte());
        Assert.Equal(0x1234, reader.ReadUInt16LittleEndian());
        Assert.Equal(0x89ABCDEF, reader.ReadUInt32LittleEndian());
        Assert.Equal(0x0102030405060708UL, reader.ReadUInt64LittleEndian());

        Assert.Equal(15, reader.Position);
        Assert.Equal(0, reader.Remaining);
        Assert.True(reader.IsEmpty);
    }

    [Fact]
    public void ReadPastEnd_ThrowsArchiveTruncatedException()
    {
        byte[] data = [1, 2, 3];
        var reader = new ByteReader(data);

        reader.ReadByte();
        reader.ReadByte();
        reader.ReadByte();

        bool threw = false;
        try
        {
            reader.ReadByte();
        }
        catch (ArchiveTruncatedException)
        {
            threw = true;
        }

        Assert.True(threw, "Debe lanzar ArchiveTruncatedException al leer más allá del límite.");
    }

    [Fact]
    public void ReadUtf16Strings_Success()
    {
        string expected = "Patagonia7z";
        byte[] bytes = Encoding.Unicode.GetBytes(expected);

        var reader = new ByteReader(bytes);
        string actual = reader.ReadUtf16String(expected.Length);

        Assert.Equal(expected, actual);
        Assert.True(reader.IsEmpty);
    }

    [Fact]
    public void ReadNullTerminatedUtf16String_Success()
    {
        string expected = "archivo_prueba.txt";
        byte[] nameBytes = Encoding.Unicode.GetBytes(expected);
        byte[] buffer = new byte[nameBytes.Length + 4]; // Incluye terminador nulo (2 bytes) y 2 bytes extra
        nameBytes.CopyTo(buffer, 0);

        var reader = new ByteReader(buffer);
        string actual = reader.ReadNullTerminatedUtf16String();

        Assert.Equal(expected, actual);
        Assert.Equal(nameBytes.Length + 2, reader.Position);
        Assert.Equal(2, reader.Remaining);
    }
}
