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

using PrZip.Codecs.Filters;
using Xunit;

namespace PrZip.Tests;

public class FilterTests
{
    [Fact]
    public void BcjX86_Roundtrip_ProducesExactOriginalBytes()
    {
        // Generar un buffer con patrones de código máquina x86 que incluyan instrucciones CALL (0xE8) y JMP (0xE9)
        byte[] original = new byte[1024];
        var rand = new Random(42);
        rand.NextBytes(original);

        // Insertar instrucciones de salto x86 realistas
        original[10] = 0xE8; // CALL
        original[11] = 0x12;
        original[12] = 0x34;
        original[13] = 0x56;
        original[14] = 0x00; // Most significant byte 0x00 (válido)

        original[100] = 0xE9; // JMP
        original[101] = 0xFE;
        original[102] = 0xDC;
        original[103] = 0xBA;
        original[104] = 0xFF; // Most significant byte 0xFF (válido)

        byte[] encoded = (byte[])original.Clone();

        var encFilter = new BcjX86Filter(initialIp: 0);
        encFilter.Encode(encoded);

        // Verificar que el filtro efectivamente modificó los bytes de salto
        Assert.False(original.AsSpan().SequenceEqual(encoded));

        // Reconvertir con decodificador
        byte[] decoded = (byte[])encoded.Clone();
        var decFilter = new BcjX86Filter(initialIp: 0);
        decFilter.Decode(decoded);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void BcjX86_BufferPartitionTest_ProducesIdenticalOutput()
    {
        // Test fundamental (§4.3): Decodificar en trozos arbitrarios debe dar el MISMO resultado que procesar todo el buffer
        byte[] original = new byte[500];
        var rand = new Random(99);
        rand.NextBytes(original);

        original[50] = 0xE8;
        original[51] = 0x10;
        original[52] = 0x20;
        original[53] = 0x30;
        original[54] = 0x00;

        original[200] = 0xE9;
        original[201] = 0xAA;
        original[202] = 0xBB;
        original[203] = 0xCC;
        original[204] = 0xFF;

        byte[] encoded = (byte[])original.Clone();
        var encFilter = new BcjX86Filter(0);
        encFilter.Encode(encoded);

        // Decodificación directa en 1 bloque
        byte[] decodedWhole = (byte[])encoded.Clone();
        var decFilterWhole = new BcjX86Filter(0);
        decFilterWhole.Decode(decodedWhole);

        // Decodificación particionada con avance por bytes procesados (streaming idiom)
        byte[] decodedChunked = (byte[])encoded.Clone();
        var decFilterChunked = new BcjX86Filter(0);

        int pos = 0;
        int[] chunkSizes = [64, 128, 93, 77, 120];
        int chunkIdx = 0;

        while (pos < decodedChunked.Length)
        {
            int available = decodedChunked.Length - pos;
            if (available < 5)
            {
                // Los últimos < 5 bytes al final del stream no pueden contener una instrucción de 5 bytes
                break;
            }

            int size = Math.Min(chunkSizes[chunkIdx % chunkSizes.Length], available);
            int processed = decFilterChunked.Decode(decodedChunked.AsSpan(pos, size));
            if (processed == 0)
            {
                break;
            }

            pos += processed;
            chunkIdx++;
        }

        Assert.Equal(decodedWhole, decodedChunked);
    }

    [Fact]
    public void BcjArm_Roundtrip_ProducesExactOriginalBytes()
    {
        byte[] original = new byte[64];
        var rand = new Random(101);
        rand.NextBytes(original);

        // Instrucción BL (Branch with Link): byte 3 = 0xEB
        original[4] = 0x10;
        original[5] = 0x20;
        original[6] = 0x30;
        original[7] = 0xEB;

        byte[] encoded = (byte[])original.Clone();
        var armEnc = new BcjArmFilter(0);
        armEnc.Encode(encoded);

        Assert.False(original.AsSpan().SequenceEqual(encoded));

        byte[] decoded = (byte[])encoded.Clone();
        var armDec = new BcjArmFilter(0);
        armDec.Decode(decoded);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void BcjArm64_Roundtrip_ProducesExactOriginalBytes()
    {
        byte[] original = new byte[64];
        var rand = new Random(202);
        rand.NextBytes(original);

        // Instrucción BL en ARM64 (0x94)
        original[8] = 0x01;
        original[9] = 0x02;
        original[10] = 0x03;
        original[11] = 0x94;

        byte[] encoded = (byte[])original.Clone();
        var arm64Enc = new BcjArm64Filter(0);
        arm64Enc.Encode(encoded);

        Assert.False(original.AsSpan().SequenceEqual(encoded));

        byte[] decoded = (byte[])encoded.Clone();
        var arm64Dec = new BcjArm64Filter(0);
        arm64Dec.Decode(decoded);

        Assert.Equal(original, decoded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    public void DeltaFilter_Roundtrip_Success(int distance)
    {
        byte[] original = new byte[1000];
        var rand = new Random(555);
        rand.NextBytes(original);

        byte[] encoded = (byte[])original.Clone();
        var deltaEnc = new DeltaFilter(distance);
        deltaEnc.Encode(encoded);

        byte[] decoded = (byte[])encoded.Clone();
        var deltaDec = new DeltaFilter(distance);
        deltaDec.Decode(decoded);

        Assert.Equal(original, decoded);
    }
}
