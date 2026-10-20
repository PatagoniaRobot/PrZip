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

using PrZip.Exceptions;
using PrZip.Primitives.IO;
using PrZip.Primitives.Numeric;
using Xunit;

namespace PrZip.Tests;

public class VarIntTests
{
    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(1UL, 1)]
    [InlineData(127UL, 1)]
    [InlineData(128UL, 2)]
    [InlineData(300UL, 2)]
    [InlineData(16383UL, 2)]
    [InlineData(16384UL, 3)]
    [InlineData(2097151UL, 3)]
    [InlineData(2097152UL, 4)]
    [InlineData(268435455UL, 4)]
    [InlineData(268435456UL, 5)]
    [InlineData(34359738367UL, 5)]
    [InlineData(34359738368UL, 6)]
    [InlineData(4398046511103UL, 6)]
    [InlineData(4398046511104UL, 7)]
    [InlineData(562949953421311UL, 7)]
    [InlineData(562949953421312UL, 8)]
    [InlineData(72057594037927935UL, 8)]
    [InlineData(72057594037927936UL, 9)]
    [InlineData(ulong.MaxValue, 9)]
    public void WriteAndRead_MatchesOriginalValueAndExpectedSize(ulong value, int expectedSize)
    {
        byte[] buffer = new byte[16];
        int written = VarInt.Write(buffer, value);

        Assert.Equal(expectedSize, written);
        Assert.Equal(expectedSize, VarInt.GetEncodedSize(value));

        // Probar lectura con ByteReader
        var reader = new ByteReader(buffer.AsSpan(0, written));
        ulong readValue = VarInt.Read(ref reader);

        Assert.Equal(value, readValue);
        Assert.True(reader.IsEmpty);

        // Probar lectura con ReadOnlySpan
        ulong readValueSpan = VarInt.Read(buffer.AsSpan(0, written), out int bytesRead);
        Assert.Equal(value, readValueSpan);
        Assert.Equal(written, bytesRead);
    }

    [Fact]
    public void ReadTruncated_ThrowsArchiveTruncatedException()
    {
        // 300 se codifica en 2 bytes: si pasamos solo 1 byte, debe fallar por truncamiento
        byte[] buffer = new byte[16];
        VarInt.Write(buffer, 300);

        Assert.Throws<ArchiveTruncatedException>(() =>
        {
            VarInt.Read(buffer.AsSpan(0, 1), out _);
        });
    }

    [Fact]
    public void RandomValues_Roundtrip_Success()
    {
        var random = new Random(42);
        byte[] dest = new byte[16];

        for (int i = 0; i < 1000; i++)
        {
            byte[] bytes = new byte[8];
            random.NextBytes(bytes);
            ulong value = BitConverter.ToUInt64(bytes);

            int written = VarInt.Write(dest, value);
            ulong restored = VarInt.Read(dest.AsSpan(0, written), out int readCount);

            Assert.Equal(written, readCount);
            Assert.Equal(value, restored);
        }
    }
}
