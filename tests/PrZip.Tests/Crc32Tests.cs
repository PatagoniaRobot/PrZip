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
using PrZip.Primitives.Checksum;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

public class Crc32Tests
{
    [Fact]
    public void StandardVectors_MatchExactValues()
    {
        // 1. Buffer vacío -> 0x00000000
        Assert.Equal(0x00000000u, Crc32Algorithm.Compute([]));

        // 2. "123456789" -> 0xCBF43926
        byte[] digits = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, Crc32Algorithm.Compute(digits));

        // 3. "The quick brown fox jumps over the lazy dog" -> 0x414FA339
        byte[] fox = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog");
        Assert.Equal(0x414FA339u, Crc32Algorithm.Compute(fox));
    }

    [Fact]
    public void IncrementalUpdate_MatchesSingleCompute()
    {
        byte[] data = new byte[10000];
        new Random(42).NextBytes(data);

        uint fullCrc = Crc32Algorithm.Compute(data);

        // Actualización en trozos de diferentes tamaños
        uint chunkedCrc = 0;
        int offset = 0;
        int[] chunkSizes = [1, 7, 15, 16, 17, 32, 100, 512, 1024, 2048];
        int chunkIdx = 0;

        while (offset < data.Length)
        {
            int size = Math.Min(chunkSizes[chunkIdx % chunkSizes.Length], data.Length - offset);
            chunkedCrc = Crc32Algorithm.Update(chunkedCrc, data.AsSpan(offset, size));
            offset += size;
            chunkIdx++;
        }

        Assert.Equal(fullCrc, chunkedCrc);
    }

    [Fact]
    public void Combine_MatchesConcatenatedBuffer()
    {
        byte[] part1 = new byte[321];
        byte[] part2 = new byte[654];
        var rand = new Random(123);
        rand.NextBytes(part1);
        rand.NextBytes(part2);

        byte[] combined = new byte[part1.Length + part2.Length];
        part1.CopyTo(combined, 0);
        part2.CopyTo(combined, part1.Length);

        uint crc1 = Crc32Algorithm.Compute(part1);
        uint crc2 = Crc32Algorithm.Compute(part2);
        uint fullCrc = Crc32Algorithm.Compute(combined);

        uint combinedCrc = Crc32Algorithm.Combine(crc1, crc2, part2.Length);

        Assert.Equal(fullCrc, combinedCrc);
    }

    [Fact]
    public void InteropWithOfficial7z_ProducesIdenticalCrc()
    {
        if (!Official7zFinder.IsAvailable)
        {
            return; // Omitir si 7z no está disponible
        }

        string tempFile = Path.Combine(Path.GetTempPath(), $"test_crc_{Guid.NewGuid():N}.bin");
        try
        {
            // Generar archivo de prueba con 64 KB de datos pseudoaleatorios
            byte[] data = new byte[65536];
            new Random(777).NextBytes(data);
            File.WriteAllBytes(tempFile, data);

            uint expectedCrc = Crc32Algorithm.Compute(data);
            string expectedHex = expectedCrc.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

            // Ejecutar: 7z h -scrcCRC32 tempFile
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Official7zFinder.ExecutablePath!,
                    Arguments = $"h -scrcCRC32 \"{tempFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            Assert.Equal(0, proc.ExitCode);

            // Verificar que el hash hexadecimal de 7-Zip coincide con nuestro cálculo
            Assert.Contains(expectedHex, output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
