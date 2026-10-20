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

using PrZip.Codecs.Lzma;
using Xunit;

namespace PrZip.Tests;

public class RangeCoderTests
{
    [Fact]
    public void RangeEncoder_RangeDecoder_RoundTrip_RandomBits()
    {
        var random = new Random(42);
        ushort[] encodeProbs = new ushort[256];
        ushort[] decodeProbs = new ushort[256];

        for (int i = 0; i < encodeProbs.Length; i++)
        {
            encodeProbs[i] = (ushort)(LzmaConstants.BitModelTotal >> 1);
            decodeProbs[i] = (ushort)(LzmaConstants.BitModelTotal >> 1);
        }

        var sequence = new (int probIdx, uint bit)[10000];
        for (int i = 0; i < sequence.Length; i++)
        {
            sequence[i] = (random.Next(encodeProbs.Length), (uint)random.Next(2));
        }

        using var memoryStream = new MemoryStream();
        var encoder = new RangeEncoder();
        encoder.Init(memoryStream);

        foreach (var item in sequence)
        {
            encoder.EncodeBit(ref encodeProbs[item.probIdx], item.bit);
        }
        encoder.FlushData();

        memoryStream.Position = 0;
        var decoder = new RangeDecoder();
        decoder.Init(memoryStream);

        foreach (var item in sequence)
        {
            uint decodedBit = decoder.DecodeBit(ref decodeProbs[item.probIdx]);
            Assert.Equal(item.bit, decodedBit);
        }
    }

    [Fact]
    public void RangeEncoder_RangeDecoder_RoundTrip_DirectBits()
    {
        var random = new Random(1234);
        var directBitsSeq = new (uint val, int numBits)[5000];

        for (int i = 0; i < directBitsSeq.Length; i++)
        {
            int numBits = random.Next(1, 16);
            uint val = (uint)random.Next(1 << numBits);
            directBitsSeq[i] = (val, numBits);
        }

        using var memoryStream = new MemoryStream();
        var encoder = new RangeEncoder();
        encoder.Init(memoryStream);

        foreach (var item in directBitsSeq)
        {
            encoder.EncodeDirectBits(item.val, item.numBits);
        }
        encoder.FlushData();

        memoryStream.Position = 0;
        var decoder = new RangeDecoder();
        decoder.Init(memoryStream);

        foreach (var item in directBitsSeq)
        {
            uint decodedVal = decoder.DecodeDirectBits(item.numBits);
            Assert.Equal(item.val, decodedVal);
        }
    }

    [Fact]
    public void RangeEncoder_RangeDecoder_RoundTrip_BitTree()
    {
        var random = new Random(9876);
        ushort[] encodeProbs = new ushort[64];
        ushort[] decodeProbs = new ushort[64];

        for (int i = 0; i < encodeProbs.Length; i++)
        {
            encodeProbs[i] = (ushort)(LzmaConstants.BitModelTotal >> 1);
            decodeProbs[i] = (ushort)(LzmaConstants.BitModelTotal >> 1);
        }

        const int numBits = 5;
        var symbols = new uint[2000];
        for (int i = 0; i < symbols.Length; i++)
        {
            symbols[i] = (uint)random.Next(1 << numBits);
        }

        using var memoryStream = new MemoryStream();
        var encoder = new RangeEncoder();
        encoder.Init(memoryStream);

        foreach (var sym in symbols)
        {
            encoder.EncodeBitTree(encodeProbs.AsSpan(0, 1 << numBits), numBits, sym);
        }
        encoder.FlushData();

        memoryStream.Position = 0;
        var decoder = new RangeDecoder();
        decoder.Init(memoryStream);

        foreach (var sym in symbols)
        {
            uint decodedSym = decoder.DecodeBitTree(decodeProbs.AsSpan(0, 1 << numBits), numBits);
            Assert.Equal(sym, decodedSym);
        }
    }

    [Fact]
    public void RangeEncoder_RangeDecoder_RoundTrip_ReverseBitTree()
    {
        var random = new Random(5555);
        ushort[] encodeProbs = new ushort[64];
        ushort[] decodeProbs = new ushort[64];

        for (int i = 0; i < encodeProbs.Length; i++)
        {
            encodeProbs[i] = (ushort)(LzmaConstants.BitModelTotal >> 1);
            decodeProbs[i] = (ushort)(LzmaConstants.BitModelTotal >> 1);
        }

        const int numBits = 4;
        var symbols = new uint[2000];
        for (int i = 0; i < symbols.Length; i++)
        {
            symbols[i] = (uint)random.Next(1 << numBits);
        }

        using var memoryStream = new MemoryStream();
        var encoder = new RangeEncoder();
        encoder.Init(memoryStream);

        foreach (var sym in symbols)
        {
            encoder.EncodeReverseBitTree(encodeProbs.AsSpan(0, 1 << numBits), numBits, sym);
        }
        encoder.FlushData();

        memoryStream.Position = 0;
        var decoder = new RangeDecoder();
        decoder.Init(memoryStream);

        foreach (var sym in symbols)
        {
            uint decodedSym = decoder.DecodeReverseBitTree(decodeProbs.AsSpan(0, 1 << numBits), numBits);
            Assert.Equal(sym, decodedSym);
        }
    }
}
