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
using PrZip.Codecs.Lzma2;
using Xunit;

namespace PrZip.Tests;

public class Lzma2EncoderTests
{
    [Theory]
    [InlineData("A")]
    [InlineData("HOLA LZMA2!")]
    [InlineData("Prueba de compresión real LZMA2 con bloques y empaquetado canónico.")]
    [InlineData("Repetición: ----------------------------------------------------")]
    public void Lzma2Encoder_Lzma2Decoder_RoundTrip_Short(string text)
    {
        byte[] original = Encoding.UTF8.GetBytes(text);

        var encoder = new Lzma2Encoder(65536);
        using var compressedMs = new MemoryStream();
        using (var originalMs = new MemoryStream(original))
        {
            encoder.Encode(originalMs, compressedMs);
        }

        byte[] compressedData = compressedMs.ToArray();

        using var inMs = new MemoryStream(compressedData);
        using var decompressedMs = new MemoryStream();

        var decoder = new Lzma2Decoder();
        decoder.SetDictionarySize(65536);
        decoder.Decode(inMs, decompressedMs, original.Length);

        byte[] result = decompressedMs.ToArray();
        Assert.Equal(original, result);
    }

    [Fact]
    public void Lzma2Encoder_Lzma2Decoder_RoundTrip_MultiChunk_Large()
    {
        // 200 KB de texto para forzar múltiples chunks (> 64 KB)
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Línea forense multi-bloque LZMA2 #{i} con datos estructurados Patagonia Robot {i * 17}");
        }

        byte[] original = Encoding.UTF8.GetBytes(sb.ToString());
        Assert.True(original.Length > 100000, "El tamaño original debe ser mayor a 100 KB.");

        var encoder = new Lzma2Encoder(1048576);
        using var compressedMs = new MemoryStream();
        using (var originalMs = new MemoryStream(original))
        {
            encoder.Encode(originalMs, compressedMs);
        }

        byte[] compressedData = compressedMs.ToArray();

        // Verificar que hubo compresión real
        Assert.True(compressedData.Length < original.Length / 3, $"El tamaño comprimido {compressedData.Length} no fue menor que {original.Length / 3}.");

        // Descomprimir con Lzma2Decoder
        using var inMs = new MemoryStream(compressedData);
        using var decompressedMs = new MemoryStream();

        var decoder = new Lzma2Decoder();
        decoder.SetDictionarySize(1048576);
        decoder.Decode(inMs, decompressedMs, original.Length);

        byte[] result = decompressedMs.ToArray();
        Assert.Equal(original, result);
    }

    [Fact]
    public void Lzma2Encoder_IncompressibleData_FallsBackToUncompressedChunks()
    {
        // 10 KB de datos aleatorios completamente incompresibles
        byte[] randomData = new byte[10240];
        new Random(123).NextBytes(randomData);

        var encoder = new Lzma2Encoder(65536);
        using var compressedMs = new MemoryStream();
        using (var originalMs = new MemoryStream(randomData))
        {
            encoder.Encode(originalMs, compressedMs);
        }

        byte[] compressedData = compressedMs.ToArray();

        // Debe haber emitido un chunk sin comprimir (control byte 0x01)
        Assert.Equal(0x01, compressedData[0]);

        // Descomprimir
        using var inMs = new MemoryStream(compressedData);
        using var decompressedMs = new MemoryStream();

        var decoder = new Lzma2Decoder();
        decoder.SetDictionarySize(65536);
        decoder.Decode(inMs, decompressedMs, randomData.Length);

        Assert.Equal(randomData, decompressedMs.ToArray());
    }
}
