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

using PrZip.Primitives.IO;
using PrZip.Primitives.Numeric;
using Xunit;

namespace PrZip.Tests;

public class BitVectorTests
{
    [Fact]
    public void AllAreDefined_ReturnsTrueForAllIndices()
    {
        var vec = new BitVector(100, allAreDefined: true);

        Assert.Equal(100, vec.Length);
        Assert.True(vec.AllAreDefined);
        Assert.Equal(100, vec.CountTrue());

        for (int i = 0; i < 100; i++)
        {
            Assert.True(vec[i]);
        }
    }

    [Fact]
    public void MsbFirst_BitOrdering_IsCorrect()
    {
        // 0b10000001 (0x81): bit 0 y bit 7 son true, 1..6 son false
        byte[] bytes = [0x81];
        var vec = new BitVector(bytes, 8);

        Assert.True(vec[0]);
        Assert.False(vec[1]);
        Assert.False(vec[2]);
        Assert.False(vec[3]);
        Assert.False(vec[4]);
        Assert.False(vec[5]);
        Assert.False(vec[6]);
        Assert.True(vec[7]);
        Assert.Equal(2, vec.CountTrue());
    }

    [Fact]
    public void ReadWithAllDefined_ReadsByteCorrectly()
    {
        // Byte inicial = 1 (AllAreDefined = true)
        byte[] allDefinedData = [1];
        var reader1 = new ByteReader(allDefinedData);
        var vec1 = BitVector.ReadWithAllDefined(ref reader1, 50);
        Assert.True(vec1.AllAreDefined);
        Assert.Equal(50, vec1.CountTrue());

        // Byte inicial = 0 (siguen bits explícitos)
        // 0x00, 0xAA (0b10101010)
        byte[] explicitData = [0, 0xAA];
        var reader2 = new ByteReader(explicitData);
        var vec2 = BitVector.ReadWithAllDefined(ref reader2, 8);
        Assert.False(vec2.AllAreDefined);
        Assert.True(vec2[0]);
        Assert.False(vec2[1]);
        Assert.True(vec2[2]);
        Assert.False(vec2[3]);
        Assert.True(vec2[4]);
        Assert.False(vec2[5]);
        Assert.True(vec2[6]);
        Assert.False(vec2[7]);
        Assert.Equal(4, vec2.CountTrue());
    }

    [Fact]
    public void WriteAndRead_Roundtrip_Success()
    {
        bool[] bits = [true, false, true, true, false, false, true, false, true, true]; // 10 bits
        byte[] buffer = new byte[16];

        int written = BitVector.Write(buffer, bits, includeAllDefinedByte: true);
        Assert.Equal(1 + 2, written); // 1 byte control + 2 bytes para 10 bits

        var reader = new ByteReader(buffer.AsSpan(0, written));
        var vec = BitVector.ReadWithAllDefined(ref reader, bits.Length);

        Assert.Equal(bits.Length, vec.Length);
        for (int i = 0; i < bits.Length; i++)
        {
            Assert.Equal(bits[i], vec[i]);
        }
    }
}
