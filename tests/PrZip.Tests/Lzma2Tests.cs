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

using System.Diagnostics;
using System.Text;
using PrZip.Codecs.Lzma2;
using PrZip.Exceptions;
using Xunit;

namespace PrZip.Tests;

public class Lzma2Tests
{
    [Theory]
    [InlineData(0, 4096u)] // 4 KB
    [InlineData(1, 6144u)] // 6 KB
    [InlineData(2, 8192u)] // 8 KB
    [InlineData(10, 131072u)] // 128 KB
    [InlineData(20, 4194304u)] // 4 MB
    [InlineData(40, 0xFFFFFFFFu)] // 4 GB
    public void Lzma2Properties_DictionarySizes_AreCorrect(byte prop, uint expectedDictSize)
    {
        var properties = new Lzma2Properties(prop);
        Assert.Equal(prop, properties.PropertyByte);
        Assert.Equal(expectedDictSize, properties.DictionarySize);
    }

    [Fact]
    public void Lzma2Properties_InvalidProp_ThrowsCorruptArchiveException()
    {
        Assert.Throws<CorruptArchiveException>(() => new Lzma2Properties(41));
    }

    [Fact]
    public void UncompressedChunk_Decode_Success()
    {
        // 0x01: chunk sin comprimir con reset de diccionario
        // 0x00, 0x04: longitud 5 bytes (0x0004 + 1)
        // Datos: 'H', 'O', 'L', 'A', '!'
        // 0x00: Fin de flujo LZMA2
        byte[] lzma2Data = [0x01, 0x00, 0x04, (byte)'H', (byte)'O', (byte)'L', (byte)'A', (byte)'!', 0x00];

        using var inStream = new MemoryStream(lzma2Data);
        using var outStream = new MemoryStream();

        var decoder = new Lzma2Decoder();
        decoder.SetDictionarySize(65536);
        decoder.Decode(inStream, outStream, 5);

        string result = Encoding.ASCII.GetString(outStream.ToArray());
        Assert.Equal("HOLA!", result);
    }

    [Fact]
    public void InvalidControlByte_ThrowsCorruptArchiveException()
    {
        byte[] invalidData = [0x05, 0x00, 0x00];

        using var inStream = new MemoryStream(invalidData);
        using var outStream = new MemoryStream();

        var decoder = new Lzma2Decoder();
        decoder.SetDictionarySize(65536);

        Assert.Throws<CorruptArchiveException>(() => decoder.Decode(inStream, outStream, 10));
    }

    [Theory]
    [InlineData("Texto corto para probar LZMA2 en .NET 10.")]
    [InlineData("Patagonia Robot — Herramientas periciales y forenses de alta fidelidad.")]
    public void CanonicalLzma2Stream_DecompressesSuccessfully(string text)
    {
        byte[] original = Encoding.UTF8.GetBytes(text);
        byte[] lzma2Stream = GenerateRawLzma2Stream(original);

        using var inStream = new MemoryStream(lzma2Stream);
        using var outStream = new MemoryStream();

        var decoder = new Lzma2Decoder();
        decoder.SetDictionarySize(1048576); // 1 MB
        decoder.Decode(inStream, outStream, original.Length);

        byte[] decompressed = outStream.ToArray();
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void LargeLzma2Stream_MultiBlock_DecompressesSuccessfully()
    {
        // 50 KB de texto con patrones repetidos para forzar compresión real LZMA2
        var sb = new StringBuilder();
        for (int i = 0; i < 1500; i++)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Bloque #{i}: Registro forense de prueba LZMA2 Patagonia Robot {i * 31}");
        }

        byte[] original = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] lzma2Stream = GenerateRawLzma2Stream(original);

        // Verificar que el stream generado es un chunk comprimido (control byte >= 0x80)
        Assert.True(lzma2Stream[0] >= 0x80, "El primer byte de un stream comprimido LZMA2 debe ser >= 0x80.");

        using var inStream = new MemoryStream(lzma2Stream);
        using var outStream = new MemoryStream();

        var decoder = new Lzma2Decoder();
        decoder.SetDictionarySize(1048576); // 1 MB
        decoder.Decode(inStream, outStream, original.Length);

        byte[] decompressed = outStream.ToArray();
        Assert.Equal(original.Length, decompressed.Length);
        Assert.Equal(original, decompressed);
    }

    private static byte[] GenerateRawLzma2Stream(byte[] rawData)
    {
        string tempIn = Path.Combine(Path.GetTempPath(), $"lzma2_in_{Guid.NewGuid():N}.bin");
        string tempOut = Path.Combine(Path.GetTempPath(), $"lzma2_out_{Guid.NewGuid():N}.raw");

        try
        {
            File.WriteAllBytes(tempIn, rawData);

            string script = $"import lzma; data = open(r'{tempIn}', 'rb').read(); " +
                            $"raw = lzma.compress(data, format=lzma.FORMAT_RAW, filters=[{{'id': lzma.FILTER_LZMA2, 'dict_size': 1048576}}]); " +
                            $"open(r'{tempOut}', 'wb').write(raw)";

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"-c \"{script}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            proc.WaitForExit(5000);
            Assert.Equal(0, proc.ExitCode);

            return File.ReadAllBytes(tempOut);
        }
        finally
        {
            if (File.Exists(tempIn)) File.Delete(tempIn);
            if (File.Exists(tempOut)) File.Delete(tempOut);
        }
    }
}
