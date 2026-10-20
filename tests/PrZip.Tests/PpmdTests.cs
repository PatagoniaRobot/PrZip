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
using PrZip.Codecs.Ppmd;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

/// <summary>
/// PPMd var.H (port de Ppmd7.c / Ppmd7Dec.c / Ppmd7Enc.c), arbitrado por 7z.exe.
/// </summary>
public class PpmdTests
{
    private static byte[] SampleText()
    {
        var sb = new StringBuilder();
        var rnd = new Random(21);
        string[] words = ["evidencia", "pericia", "sobre", "cadena", "custodia", "hash", "acta", "caso", "fecha", "firma"];
        for (int i = 0; i < 60_000; i++)
        {
            sb.Append(words[rnd.Next(words.Length)]).Append(i % 13 == 0 ? ".\n" : " ");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Run7z(string arguments)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = Official7zFinder.ExecutablePath!,
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(120000);
        Assert.True(proc.ExitCode == 0, $"7z.exe falló:\n{output}");
        return output;
    }

    [Requires7zTheory]
    [InlineData("-m0=PPMd")]
    [InlineData("-m0=PPMd:o=2:mem=1m")]
    [InlineData("-m0=PPMd:o=32:mem=16m")]
    [InlineData("-m0=PPMd:o=16:mem=64k")] // memoria mínima: el modelo se reinicia muchas veces
    [InlineData("-m0=PPMd -mx9")]
    public void Official7z_Ppmd_OurReaderExtractsIdentical(string switches)
    {
        Official7zFinder.EnsureAvailable();
        string dir = Path.Combine(Path.GetTempPath(), $"7z_ppmd_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = SampleText();
            string input = Path.Combine(dir, "texto.txt");
            File.WriteAllBytes(input, data);
            string archivePath = Path.Combine(dir, "ppmd.7z");
            Run7z($"a \"{archivePath}\" \"{input}\" {switches}");

            using var archive = Archive7z.Open(archivePath);
            using var mem = new MemoryStream();
            archive.Extract(archive.Entries.Single(), mem);
            Assert.Equal(SHA256.HashData(data), SHA256.HashData(mem.ToArray()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Requires7zFact]
    public void OurEncoder_ProducesExactlyTheSameStreamAs7Zip()
    {
        // PPMd no tiene decisiones de parsing: con el mismo orden y memoria, un port fiel emite los
        // mismos bytes que 7-Zip.
        Official7zFinder.EnsureAvailable();
        string dir = Path.Combine(Path.GetTempPath(), $"7z_ppmd_enc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = SampleText();
            string input = Path.Combine(dir, "texto.txt");
            File.WriteAllBytes(input, data);

            string official = Path.Combine(dir, "oficial.7z");
            Run7z($"a \"{official}\" \"{input}\" -m0=PPMd:o=6:mem=16m");
            byte[] officialFile = File.ReadAllBytes(official);
            long officialPack = BitConverter.ToInt64(officialFile, 12);

            using var ours = new MemoryStream();
            using (var encoder = new Ppmd7EncoderStream(ours, 6, 16u << 20))
            {
                encoder.Write(data);
                encoder.Finish();
            }

            Assert.Equal(officialFile.AsSpan(32, (int)officialPack).ToArray(), ours.ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Requires7zTheory]
    [InlineData(1, false)]
    [InlineData(9, true)]
    public void OurWriter_Ppmd_Official7zExtractsIdentical(int level, bool solid)
    {
        Official7zFinder.EnsureAvailable();
        string dir = Path.Combine(Path.GetTempPath(), $"7z_ppmd_w_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            byte[] text = SampleText();
            byte[] bin = new byte[50_000];
            new Random(4).NextBytes(bin);
            string archivePath = Path.Combine(dir, "nuestro.7z");
            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z { Method = CompressionMethod.Ppmd, Level = level, Solid = solid }))
            {
                writer.Add(new WriterEntry7z { Name = "texto.txt" }, new MemoryStream(text));
                writer.Add(new WriterEntry7z { Name = "azar.bin" }, new MemoryStream(bin));
                writer.Finish();
            }

            string outDir = Path.Combine(dir, "x");
            Run7z($"x \"{archivePath}\" -o\"{outDir}\" -y");
            Assert.Equal(text, File.ReadAllBytes(Path.Combine(outDir, "texto.txt")));
            Assert.Equal(bin, File.ReadAllBytes(Path.Combine(outDir, "azar.bin")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
