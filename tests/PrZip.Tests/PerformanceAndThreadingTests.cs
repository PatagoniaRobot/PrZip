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
using PrZip.Codecs.Lzma2;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

public class PerformanceAndThreadingTests
{
    [Fact]
    public void Lzma2Encoder_Direct_MultiThreaded_Roundtrip()
    {
        // 512 KB de datos con patrones repetitivos
        byte[] inputData = new byte[512 * 1024];
        for (int i = 0; i < inputData.Length; i++)
        {
            inputData[i] = (byte)((i * 31 + (i / 100)) & 0xFF);
        }

        using var inMs = new MemoryStream(inputData);
        using var outMs = new MemoryStream();

        // Codificar con 4 hilos
        var encoder = new Lzma2Encoder(dictSize: 1048576, threads: 4);
        encoder.Encode(inMs, outMs);

        Assert.True(outMs.Length > 0);
        Assert.True(outMs.Length < inputData.Length); // Debe haber comprimido

        // Decodificar con Lzma2Decoder
        outMs.Position = 0;
        using var decompressedMs = new MemoryStream();
        var decoder = new Lzma2Decoder();
        decoder.SetDictionarySize(1048576);
        decoder.Decode(outMs, decompressedMs, inputData.Length);

        byte[] result = decompressedMs.ToArray();
        Assert.Equal(inputData.Length, result.Length);
        Assert.True(inputData.AsSpan().SequenceEqual(result));
    }

    [Requires7zFact]
    public void CreateArchive_Lzma2_MultiThreaded_1MB_7zInteroperability()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_perf_1mb_{Guid.NewGuid():N}");
        string extractDir7z = Path.Combine(tempDir, "extracted_7z");
        string archivePath = Path.Combine(tempDir, "salida_mt_1mb.7z");

        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir7z);

        try
        {
            // 1 MB de datos estructurados
            byte[] largeData = new byte[1024 * 1024];
            for (int i = 0; i < largeData.Length; i++)
            {
                largeData[i] = (byte)((i ^ (i >> 8)) & 0xFF);
            }

            // Crear archivo 7z con compresión multihilo (Threads = 4)
            var options = new WriterOptions7z
            {
                Method = CompressionMethod.Lzma2,
                Threads = 4,
                Level = 5
            };

            using (var writer = Writer7z.Create(archivePath, options))
            {
                using var ms = new MemoryStream(largeData);
                writer.Add(new WriterEntry7z { Name = "payload_1mb.bin" }, ms);
                writer.Finish();
            }

            Assert.True(File.Exists(archivePath));

            // Validar con el 7z.exe oficial: 7z t
            Run7z($"t -y \"{archivePath}\"");

            // Extraer con 7z.exe oficial y validar bytes
            Run7z($"x -y \"{archivePath}\" -o\"{extractDir7z}\"");
            string extractedFile = Path.Combine(extractDir7z, "payload_1mb.bin");
            Assert.True(File.Exists(extractedFile));

            byte[] extractedBytes = File.ReadAllBytes(extractedFile);
            Assert.Equal(largeData.Length, extractedBytes.Length);
            Assert.True(largeData.AsSpan().SequenceEqual(extractedBytes));

            // Extraer también con nuestra biblioteca
            using var archive = Archive7z.Open(archivePath);
            Assert.Single(archive.Entries);
            var entry = archive.Entries[0];
            using var ourExtracted = new MemoryStream();
            archive.Extract(entry, ourExtracted);

            Assert.True(largeData.AsSpan().SequenceEqual(ourExtracted.ToArray()));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Requires7zFact]
    public void CreateArchive_Lzma2_LargePayload_5MB_MultiThreaded_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_perf_5mb_{Guid.NewGuid():N}");
        string extractDir7z = Path.Combine(tempDir, "extracted_7z");
        string archivePath = Path.Combine(tempDir, "salida_mt_5mb.7z");

        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir7z);

        try
        {
            // 5 archivos de 1 MB cada uno
            var files = new Dictionary<string, byte[]>();
            for (int f = 0; f < 5; f++)
            {
                byte[] data = new byte[1024 * 1024];
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = (byte)((i * (f + 1) + (i >> 4)) & 0xFF);
                }
                files[$"file_{f}.bin"] = data;
            }

            var options = new WriterOptions7z
            {
                Method = CompressionMethod.Lzma2,
                Threads = Environment.ProcessorCount,
                Level = 5
            };

            using (var writer = Writer7z.Create(archivePath, options))
            {
                foreach (var (name, data) in files)
                {
                    using var ms = new MemoryStream(data);
                    writer.Add(new WriterEntry7z { Name = name }, ms);
                }
                writer.Finish();
            }

            Assert.True(File.Exists(archivePath));

            // Validar con el 7z.exe oficial
            Run7z($"t -y \"{archivePath}\"");

            // Extraer con 7z.exe oficial y validar todos los archivos
            Run7z($"x -y \"{archivePath}\" -o\"{extractDir7z}\"");
            foreach (var (name, originalData) in files)
            {
                string extractedPath = Path.Combine(extractDir7z, name);
                Assert.True(File.Exists(extractedPath));
                byte[] extracted = File.ReadAllBytes(extractedPath);
                Assert.True(originalData.AsSpan().SequenceEqual(extracted));
            }

            // Validar lectura interna
            using var archive = Archive7z.Open(archivePath);
            Assert.Equal(5, archive.Entries.Count);
            foreach (var entry in archive.Entries)
            {
                using var ms = new MemoryStream();
                archive.Extract(entry, ms);
                Assert.True(files[entry.Name].AsSpan().SequenceEqual(ms.ToArray()));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Requires7zFact]
    public void DeterministicOutput_SingleVsMultiThreaded_BothExtractIdenticalData()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_perf_det_{Guid.NewGuid():N}");
        string archive1 = Path.Combine(tempDir, "archive_st.7z");
        string archive2 = Path.Combine(tempDir, "archive_mt.7z");

        Directory.CreateDirectory(tempDir);

        try
        {
            byte[] data = new byte[300 * 1024];
            new Random(12345).NextBytes(data);

            // Archivo 1: monohilo
            using (var writer1 = Writer7z.Create(archive1, new WriterOptions7z { Method = CompressionMethod.Lzma2, Threads = 1 }))
            {
                using var ms1 = new MemoryStream(data);
                writer1.Add(new WriterEntry7z { Name = "data.bin" }, ms1);
                writer1.Finish();
            }

            // Archivo 2: multihilo (4 hilos)
            using (var writer2 = Writer7z.Create(archive2, new WriterOptions7z { Method = CompressionMethod.Lzma2, Threads = 4 }))
            {
                using var ms2 = new MemoryStream(data);
                writer2.Add(new WriterEntry7z { Name = "data.bin" }, ms2);
                writer2.Finish();
            }

            // Ambos archivos son válidos para 7z oficial
            Run7z($"t -y \"{archive1}\"");
            Run7z($"t -y \"{archive2}\"");

            // Ambos extraen datos idénticos
            using var arch1 = Archive7z.Open(archive1);
            using var msExtracted1 = new MemoryStream();
            arch1.Extract(arch1.Entries[0], msExtracted1);

            using var arch2 = Archive7z.Open(archive2);
            using var msExtracted2 = new MemoryStream();
            arch2.Extract(arch2.Entries[0], msExtracted2);

            Assert.True(data.AsSpan().SequenceEqual(msExtracted1.ToArray()));
            Assert.True(data.AsSpan().SequenceEqual(msExtracted2.ToArray()));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private static void Run7z(string arguments)
    {
        string? exe = Official7zFinder.ExecutablePath;
        Assert.NotNull(exe);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar 7z.exe");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"7z.exe falló con código {process.ExitCode}.\nSTDOUT: {stdout}\nSTDERR: {stderr}");
    }
}
