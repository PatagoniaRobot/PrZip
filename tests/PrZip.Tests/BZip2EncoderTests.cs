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
using PrZip.Codecs.BZip2;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

/// <summary>
/// Compresor BZip2 propio, arbitrado por el decodificador propio y por 7z.exe (que abre un .bz2 suelto).
/// </summary>
public class BZip2EncoderTests
{
    public static TheoryData<string> Inputs => new()
    {
        "vacio", "un_byte", "texto", "azar", "ceros_1mb", "codigo", "mezcla_frontera", "periodico", "corridas", "periodo_997"
    };

    private static byte[] Make(string name)
    {
        if (name == "periodo_997")
        {
            // 997 bytes al azar repetidos: todas las rotaciones de una cubeta comparten prefijos enormes,
            // se agota el presupuesto del ordenamiento por cubetas y entra la duplicación de prefijos.
            var unit = new byte[997];
            new Random(9).NextBytes(unit);
            return Enumerable.Range(0, 1_000_000).Select(i => unit[i % unit.Length]).ToArray();
        }

        if (name != "corridas")
        {
            return DeflateEncoderTests.Make(name);
        }

        // Corridas justo en los bordes de la RLE inicial (4 iguales + contador, tope 255).
        var ms = new MemoryStream();
        byte c = 0;
        foreach (int run in new[] { 1, 2, 3, 4, 5, 6, 254, 255, 256, 259, 510, 511, 1000, 4, 4, 3 })
        {
            for (int i = 0; i < run; i++) ms.WriteByte(c);
            c = (byte)(c + 1);
        }

        // Y la misma letra en corridas seguidas separadas por una distinta.
        for (int k = 0; k < 200; k++)
        {
            for (int i = 0; i < 4 + k % 7; i++) ms.WriteByte(0xAA);
            ms.WriteByte(0xAB);
        }

        return ms.ToArray();
    }

    private static byte[] Compress(byte[] data, int blockSize100k, int threads, int writeChunk = 65536)
    {
        using var ms = new MemoryStream();
        using (var enc = new BZip2EncoderStream(ms, blockSize100k, threads))
        {
            for (int i = 0; i < data.Length; i += writeChunk)
            {
                enc.Write(data, i, Math.Min(writeChunk, data.Length - i));
            }

            enc.Finish();
        }

        return ms.ToArray();
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void RoundTrip_OwnDecoder_AndSameOutputForAnyThreadCount(string name)
    {
        byte[] data = Make(name);
        foreach (int blockSize in new[] { 1, 9 })
        {
            byte[] single = Compress(data, blockSize, threads: 1);
            byte[] parallel = Compress(data, blockSize, threads: 4, writeChunk: 777);
            Assert.True(single.AsSpan().SequenceEqual(parallel), $"{name} bloque {blockSize}: la salida depende de los hilos");

            using var output = new MemoryStream();
            BZip2Decoder.Decode(new MemoryStream(single), output, data.Length);
            Assert.True(data.AsSpan().SequenceEqual(output.ToArray()), $"{name} bloque {blockSize}: no reconstruye lo mismo");
        }
    }

    [Requires7zFact]
    public void Official7Zip_DecompressesWhatWeWrote()
    {
        Official7zFinder.EnsureAvailable();
        string dir = Path.Combine(Path.GetTempPath(), $"bz2_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string name in new[] { "codigo", "corridas", "ceros_1mb", "texto", "azar", "periodo_997" })
            {
                byte[] data = Make(name);
                string bz2 = Path.Combine(dir, name + ".bz2");
                File.WriteAllBytes(bz2, Compress(data, name == "codigo" ? 1 : 9, threads: 4));

                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = Official7zFinder.ExecutablePath!,
                    Arguments = $"e \"{bz2}\" -o\"{Path.Combine(dir, "out")}\" -y",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;
                string log = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(60000);
                Assert.True(proc.ExitCode == 0, name + ": " + log);
                Assert.True(data.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "out", name))), name + ": 7-Zip reconstruye otra cosa");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Ratio_Text_BeatsDeflate_AsBZip2Should()
    {
        byte[] text = Make("texto");
        byte[] bz = Compress(text, 9, threads: 1);
        byte[] deflate = DeflateEncoderTests.Compress(text, 9);
        Assert.True(bz.Length < deflate.Length, $"bzip2 {bz.Length:N0} contra deflate {deflate.Length:N0}");
    }

    /// <summary>
    /// Bloques medianos que obligan a varias vueltas de Larsson–Sadakane y a partir grupos grandes: alfabeto
    /// binario y una zona periódica con una perturbación en el medio.
    /// </summary>
    [Fact]
    public void SortRotations_StructuredBlocks_MatchNaiveSort()
    {
        var rnd = new Random(12);
        var binary = new byte[5000];
        for (int i = 0; i < binary.Length; i++) binary[i] = (byte)rnd.Next(2);

        var unit = new byte[37];
        rnd.NextBytes(unit);
        var periodic = Enumerable.Range(0, 5000).Select(i => unit[i % 37]).ToArray();
        periodic[2500] ^= 1;

        foreach (var block in new[] { binary, periodic })
        {
            int n = block.Length;
            int[] order = BZip2EncoderStream.SortRotations(block, n);
            Assert.Equal(Enumerable.Range(0, n), order.Order());
            for (int i = 1; i < n; i++)
            {
                Assert.True(CompareRotations(block, order[i - 1], order[i]) <= 0, $"posiciones {i - 1} y {i} fuera de orden");
            }
        }
    }

    private static int CompareRotations(byte[] block, int a, int b)
    {
        int n = block.Length;
        for (int k = 0; k < n; k++)
        {
            int c = block[(a + k) % n] - block[(b + k) % n];
            if (c != 0) return c;
        }

        return 0;
    }

    [Fact]
    public void SortRotations_MatchesNaiveSort()
    {
        var rnd = new Random(5);
        foreach (int n in new[] { 1, 2, 3, 17, 300 })
        {
            foreach (int alphabet in new[] { 1, 2, 256 })
            {
                var block = new byte[n];
                for (int i = 0; i < n; i++) block[i] = (byte)rnd.Next(alphabet);

                int[] order = BZip2EncoderStream.SortRotations(block, n);
                string Rotation(int s) => Convert.ToHexString(block.Skip(s).Concat(block.Take(s)).ToArray());
                var expected = Enumerable.Range(0, n).Select(Rotation).Order(StringComparer.Ordinal).ToList();
                Assert.Equal(expected, order.Select(Rotation).ToList());
            }
        }
    }
}
