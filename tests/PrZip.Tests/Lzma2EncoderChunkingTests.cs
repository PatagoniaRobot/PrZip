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
using System.Security.Cryptography;
using System.Text;
using PrZip.Codecs.Lzma;
using PrZip.Codecs.Lzma2;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

/// <summary>
/// Codificador LZMA2 con diccionario continuo: chunks de hasta 2 MB / 64 KB sin reiniciar el modelo,
/// chunks sin comprimir que restauran el estado, bloques independientes y salida determinista.
/// </summary>
public class Lzma2EncoderChunkingTests
{
    /// <summary>
    /// Texto repetitivo, azar incompresible y ceros alternados: obliga a pasar de chunks LZMA con
    /// modelo continuo (0x80) a chunks sin comprimir (0x02) y de vuelta, dentro del mismo bloque.
    /// </summary>
    private static byte[] MixedPayload()
    {
        var rnd = new Random(1234);
        using var ms = new MemoryStream();
        byte[] text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 4000).Select(i => $"linea {i % 97} de evidencia;")));
        for (int round = 0; round < 3; round++)
        {
            ms.Write(text);
            byte[] noise = new byte[300_000];
            rnd.NextBytes(noise);
            ms.Write(noise);
            ms.Write(new byte[2_500_000]);
        }

        return ms.ToArray();
    }

    private static byte[] Encode(byte[] input, int threads, int blockSize, LzmaMode mode)
    {
        var encoder = new Lzma2Encoder(1 << 20, threads) { BlockSize = blockSize, Mode = mode };
        using var outMs = new MemoryStream();
        encoder.Encode(new MemoryStream(input), outMs);
        return outMs.ToArray();
    }

    private static byte[] Decode(byte[] encoded, long size)
    {
        var decoder = new Lzma2Decoder();
        decoder.SetDictionarySize(1 << 20);
        using var outMs = new MemoryStream();
        decoder.Decode(new MemoryStream(encoded), outMs, size);
        return outMs.ToArray();
    }

    [Theory]
    [InlineData(nameof(LzmaMode.Fast))]
    [InlineData(nameof(LzmaMode.Optimal))]
    public void MixedData_RoundTripsThroughOwnDecoder(string modeName)
    {
        // El modo va por nombre: LzmaMode es interno y un test público no puede recibirlo.
        var mode = Enum.Parse<LzmaMode>(modeName);
        byte[] input = MixedPayload();
        byte[] encoded = Encode(input, threads: 1, blockSize: 4 << 20, mode);
        Assert.Equal(input, Decode(encoded, input.Length));

        // Debe haber chunks sin comprimir (el azar) y chunks LZMA que continúan el modelo (0x80..0x9F).
        var controls = ChunkControls(encoded);
        Assert.Contains(controls, c => c is 0x01 or 0x02);
        Assert.Contains(controls, c => c is >= 0x80 and < 0xA0);
    }

    [Fact]
    public void SameOutput_ForAnyThreadCount()
    {
        byte[] input = MixedPayload();
        byte[] one = Encode(input, threads: 1, blockSize: 1 << 20, LzmaMode.Fast);
        byte[] four = Encode(input, threads: 4, blockSize: 1 << 20, LzmaMode.Fast);
        Assert.Equal(SHA256.HashData(one), SHA256.HashData(four));
        Assert.Equal(input, Decode(four, input.Length));
    }

    [Fact]
    public void ContinuousDictionary_ReachesBackAcrossChunks()
    {
        // 512 KB de azar repetido dos veces: la segunda copia solo comprime si el diccionario
        // conserva la primera a través de los chunks (antes se reiniciaba cada 64 KB).
        byte[] noise = new byte[1 << 19];
        new Random(5).NextBytes(noise);
        byte[] input = [.. noise, .. noise];

        byte[] encoded = Encode(input, threads: 1, blockSize: 4 << 20, LzmaMode.Fast);
        Assert.Equal(input, Decode(encoded, input.Length));
        Assert.True(encoded.Length < noise.Length + noise.Length / 10,
            $"La segunda copia no se comprimió contra la primera: {encoded.Length} bytes para {input.Length}.");
    }

    [Requires7zFact]
    public void Official7z_AcceptsMixedDataFromOurWriter()
    {
        Official7zFinder.EnsureAvailable();
        string dir = Path.Combine(Path.GetTempPath(), $"7z_l2chunks_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            byte[] input = MixedPayload();
            string archivePath = Path.Combine(dir, "mixto.7z");
            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z { Method = CompressionMethod.Lzma2, Level = 9, Threads = 4 }))
            {
                writer.Add(new WriterEntry7z { Name = "mixto.bin" }, new MemoryStream(input));
                writer.Finish();
            }

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = Official7zFinder.ExecutablePath!,
                Arguments = $"x -y \"{archivePath}\" -o\"{dir}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(60000);
            Assert.True(proc.ExitCode == 0, output);
            Assert.Equal(SHA256.HashData(input), SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "mixto.bin"))));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SmallInput_DoesNotReserveFullDictionaryTables()
    {
        byte[] input = Encoding.UTF8.GetBytes("archivo chico");
        long before = GC.GetAllocatedBytesForCurrentThread();
        var encoder = new Lzma2Encoder(64u << 20, threads: 1) { Mode = LzmaMode.Optimal };
        using var outMs = new MemoryStream();
        encoder.Encode(new MemoryStream(input), outMs);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(input, Decode(outMs.ToArray(), input.Length));
        Assert.True(allocated < 16L << 20, $"Se asignaron {allocated >> 20} MB para comprimir {input.Length} bytes.");
    }

    /// <summary>Recorre la secuencia de chunks LZMA2 y devuelve sus bytes de control.</summary>
    private static List<int> ChunkControls(byte[] data)
    {
        var controls = new List<int>();
        int p = 0;
        while (p < data.Length)
        {
            int c = data[p];
            controls.Add(c);
            if (c == 0x00)
            {
                break;
            }

            if (c is 0x01 or 0x02)
            {
                p += 3 + ((data[p + 1] << 8) | data[p + 2]) + 1;
            }
            else
            {
                int pack = ((data[p + 3] << 8) | data[p + 4]) + 1;
                p += (c >= 0xC0 ? 6 : 5) + pack;
            }
        }

        return controls;
    }
}
