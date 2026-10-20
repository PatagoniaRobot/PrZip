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
using System.Diagnostics;
using System.Text;
using PrZip.Codecs.Lzma;
using Xunit;

namespace PrZip.Tests;

public class LzmaDecoderTests
{
    [Fact]
    public void LzmaProperties_DecodeAndEncode_MatchesOriginal()
    {
        byte[] original = [0x5D, 0x00, 0x00, 0x80, 0x00]; // lc=3, lp=0, pb=2, dict=8MB
        var props = LzmaProperties.Decode(original);

        Assert.Equal(3, props.Lc);
        Assert.Equal(0, props.Lp);
        Assert.Equal(2, props.Pb);
        Assert.Equal(8388608u, props.DictionarySize);

        byte[] encoded = new byte[5];
        props.Encode(encoded);

        Assert.Equal(original, encoded);
    }

    [Theory]
    [InlineData("Hola mundo desde el nuevo motor de 7z en .NET 10!", true)]
    [InlineData("Hola mundo desde el nuevo motor de 7z en .NET 10!", false)]
    [InlineData("AAAABBBBCCCCDDDDEEEEFFFFGGGGHHHHIIIIJJJJKKKKLLLLMMMMNNNNOOOOPPPPQQQQRRRR", true)]
    [InlineData("AAAABBBBCCCCDDDDEEEEFFFFGGGGHHHHIIIIJJJJKKKKLLLLMMMMNNNNOOOOPPPPQQQQRRRR", false)]
    public void DecodeCanonicalLzmaStream_MatchesOriginalData(string text, bool specifyUnpackSize)
    {
        byte[] originalBytes = Encoding.UTF8.GetBytes(text);
        byte[] lzmaFileBytes = GenerateLzmaStream(originalBytes);

        // Cabecera LZMA Alone (13 bytes):
        // 5 bytes de propiedades + 8 bytes de tamaño desempaquetado (o -1)
        var props = LzmaProperties.Decode(lzmaFileBytes.AsSpan(0, 5));
        long unpackSizeHeader = (long)BinaryPrimitives.ReadUInt64LittleEndian(lzmaFileBytes.AsSpan(5, 8));

        long targetUnpackSize = specifyUnpackSize ? originalBytes.Length : -1;

        using var inStream = new MemoryStream(lzmaFileBytes, 13, lzmaFileBytes.Length - 13);
        using var outStream = new MemoryStream();

        var decoder = new LzmaDecoder();
        decoder.SetProperties(props);
        decoder.Decode(inStream, outStream, targetUnpackSize);

        byte[] decompressed = outStream.ToArray();
        Assert.Equal(originalBytes, decompressed);
    }

    [Fact]
    public void DecodeLargeRepetitiveStream_MatchesOriginal()
    {
        // 100 KB de texto repetitivo estructurado
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Línea {i}: Patagonia Robot Forensics and Compression Engine 2026 - Registro #{i * 17}");
        }

        byte[] originalBytes = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] lzmaFileBytes = GenerateLzmaStream(originalBytes);

        var props = LzmaProperties.Decode(lzmaFileBytes.AsSpan(0, 5));

        using var inStream = new MemoryStream(lzmaFileBytes, 13, lzmaFileBytes.Length - 13);
        using var outStream = new MemoryStream();

        var decoder = new LzmaDecoder();
        decoder.SetProperties(props);
        decoder.Decode(inStream, outStream, originalBytes.Length);

        byte[] decompressed = outStream.ToArray();
        Assert.Equal(originalBytes.Length, decompressed.Length);
        Assert.Equal(originalBytes, decompressed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(512)]
    public void LzmaStream_ChunkedReading_ProducesIdenticalData(int chunkSize)
    {
        string text = "Prueba de streaming con LzmaStream y lecturas en trozos pequeños para verificar continuidad de rep0 y de diccionario circular!";
        byte[] originalBytes = Encoding.UTF8.GetBytes(text);
        byte[] lzmaFileBytes = GenerateLzmaStream(originalBytes);

        var props = LzmaProperties.Decode(lzmaFileBytes.AsSpan(0, 5));

        using var rawInStream = new MemoryStream(lzmaFileBytes, 13, lzmaFileBytes.Length - 13);
        using var lzmaStream = new LzmaStream(rawInStream, props, originalBytes.Length);
        using var output = new MemoryStream();

        byte[] buffer = new byte[chunkSize];
        int read;
        while ((read = lzmaStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        byte[] result = output.ToArray();
        Assert.Equal(originalBytes, result);
    }

    private static byte[] GenerateLzmaStream(byte[] rawData)
    {
        // Usar Python con el módulo estándar lzma (liblzma oficial de Igor Pavlov) para generar el stream de referencia
        string tempInput = Path.Combine(Path.GetTempPath(), $"lzma_in_{Guid.NewGuid():N}.bin");
        string tempOutput = Path.Combine(Path.GetTempPath(), $"lzma_out_{Guid.NewGuid():N}.lzma");

        try
        {
            File.WriteAllBytes(tempInput, rawData);

            string script = $"import lzma; data = open(r'{tempInput}', 'rb').read(); " +
                            $"open(r'{tempOutput}', 'wb').write(lzma.compress(data, format=lzma.FORMAT_ALONE))";

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

            return File.ReadAllBytes(tempOutput);
        }
        finally
        {
            if (File.Exists(tempInput)) File.Delete(tempInput);
            if (File.Exists(tempOutput)) File.Delete(tempOutput);
        }
    }
}
