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

using System.Text;
using PrZip.Codecs.Lzma;
using Xunit;

namespace PrZip.Tests;

public class LzmaEncoderTests
{
    [Theory]
    [InlineData("A")]
    [InlineData("HOLA!")]
    [InlineData("Probando el codificador LZMA en C# .NET 10 sin dependencias externas.")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("123456789012345678901234567890123456789012345678901234567890")]
    public void LzmaEncoder_LzmaDecoder_RoundTrip_ShortTexts(string text)
    {
        byte[] original = Encoding.UTF8.GetBytes(text);

        var props = new LzmaProperties(3, 0, 2, 65536);
        var encoder = new LzmaEncoder(props);

        using var compressedStream = new MemoryStream();
        encoder.Encode(original, compressedStream, writeEos: true);

        byte[] compressedData = compressedStream.ToArray();

        // Verificar que descomprime idéntico con LzmaDecoder
        using var inStream = new MemoryStream(compressedData);
        using var outStream = new MemoryStream();

        var decoder = new LzmaDecoder();
        decoder.SetProperties(props);
        decoder.Decode(inStream, outStream, original.Length);

        byte[] decompressed = outStream.ToArray();
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void LzmaEncoder_CompressesRepetitiveDataSignificantly()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Línea de repetición forense #{i} con texto predecible y redundante.");
        }
        byte[] original = Encoding.UTF8.GetBytes(sb.ToString());

        var props = new LzmaProperties(3, 0, 2, 1048576);
        var encoder = new LzmaEncoder(props);

        using var compressedStream = new MemoryStream();
        encoder.Encode(original, compressedStream, writeEos: true);

        byte[] compressedData = compressedStream.ToArray();

        // Debe comprimir más del 70% de reducción
        Assert.True(compressedData.Length < original.Length / 3, $"El tamaño comprimido {compressedData.Length} no fue menor que {original.Length / 3}.");

        // Y debe descomprimir con total fidelidad
        using var inStream = new MemoryStream(compressedData);
        using var outStream = new MemoryStream();

        var decoder = new LzmaDecoder();
        decoder.SetProperties(props);
        decoder.Decode(inStream, outStream, original.Length);

        byte[] decompressed = outStream.ToArray();
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void LzmaEncoder_OptimalMode_BT4_CompressesAndDecompressesSuccessfully()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Bloque de prueba BT4 en modo óptimo #{i} verificando árboles binarios.");
        }
        byte[] original = Encoding.UTF8.GetBytes(sb.ToString());

        var props = new LzmaProperties(3, 0, 2, 1048576);
        var encoder = new LzmaEncoder(props) { Mode = LzmaMode.Optimal };

        using var compressedStream = new MemoryStream();
        encoder.Encode(original, compressedStream, writeEos: true);

        byte[] compressedData = compressedStream.ToArray();
        Assert.True(compressedData.Length < original.Length / 3);

        using var inStream = new MemoryStream(compressedData);
        using var outStream = new MemoryStream();

        var decoder = new LzmaDecoder();
        decoder.SetProperties(props);
        decoder.Decode(inStream, outStream, original.Length);

        Assert.Equal(original, outStream.ToArray());
    }
}
