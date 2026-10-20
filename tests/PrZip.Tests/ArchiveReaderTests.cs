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
using System.Security.Cryptography;
using System.Text;
using PrZip.Exceptions;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

public class ArchiveReaderTests
{
    [Requires7zFact]
    public void OpenAndExtract_Lzma2Archive_CreatedByOfficial7z_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_lzma2_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string extractDir = Path.Combine(tempDir, "extracted");
        string archivePath = Path.Combine(tempDir, "test.7z");

        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(extractDir);

        try
        {
            // 1. Crear archivo de prueba
            string testContent = "Contenido de prueba para el motor PrZip puro en .NET 10.\n" +
                                 "Desarrollado bajo principios forenses por Claudio Andino.\n" +
                                 string.Concat(Enumerable.Repeat("Línea repetitiva para probar compresión real LZMA2.\n", 100));
            string testFilePath = Path.Combine(inputDir, "documento.txt");
            File.WriteAllText(testFilePath, testContent, new UTF8Encoding(false));

            // 2. Comprimir con el 7z.exe oficial usando LZMA2
            Run7z($"a -y \"{archivePath}\" \"{testFilePath}\" -m0=lzma2");

            Assert.True(File.Exists(archivePath));

            // 3. Abrir y extraer con NUESTRO motor Archive7z
            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.Single(archive.Entries);
                var entry = archive.Entries[0];
                Assert.Equal("documento.txt", entry.Name);
                Assert.False(entry.IsDirectory);
                Assert.Equal(new FileInfo(testFilePath).Length, entry.Size);

                // Extraer a memoria
                using var mem = new MemoryStream();
                archive.Extract(entry, mem);
                string extractedText = Encoding.UTF8.GetString(mem.ToArray());
                Assert.Equal(testContent, extractedText);

                // Extraer a disco
                archive.ExtractAll(extractDir);
            }

            // 4. Comparar hash SHA-256 de los archivos
            string extractedFile = Path.Combine(extractDir, "documento.txt");
            Assert.True(File.Exists(extractedFile));

            byte[] originalHash = SHA256.HashData(File.ReadAllBytes(testFilePath));
            byte[] extractedHash = SHA256.HashData(File.ReadAllBytes(extractedFile));
            Assert.Equal(originalHash, extractedHash);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Requires7zFact]
    public void OpenAndExtract_Lzma1Archive_CreatedByOfficial7z_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_lzma1_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "test_lzma1.7z");

        Directory.CreateDirectory(inputDir);

        try
        {
            string testContent = "Prueba de compatibilidad con algoritmo clásico LZMA1 en contenedor 7z.";
            string testFilePath = Path.Combine(inputDir, "lzma1.txt");
            File.WriteAllText(testFilePath, testContent, new UTF8Encoding(false));

            Run7z($"a -y \"{archivePath}\" \"{testFilePath}\" -m0=lzma");

            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.Single(archive.Entries);

                using var mem = new MemoryStream();
                archive.Extract(archive.Entries[0], mem);

                string result = Encoding.UTF8.GetString(mem.ToArray());
                Assert.Equal(testContent, result);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Requires7zFact]
    public void OpenAndExtract_CopyArchive_NoCompression_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_copy_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "test_copy.7z");

        Directory.CreateDirectory(inputDir);

        try
        {
            byte[] rawBytes = new byte[8192];
            new Random(1234).NextBytes(rawBytes);
            string testFilePath = Path.Combine(inputDir, "data.bin");
            File.WriteAllBytes(testFilePath, rawBytes);

            // -mx=0 indica sin compresión (Copy)
            Run7z($"a -y \"{archivePath}\" \"{testFilePath}\" -mx=0");

            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.Single(archive.Entries);

                using var mem = new MemoryStream();
                archive.Extract(archive.Entries[0], mem);

                Assert.Equal(rawBytes, mem.ToArray());
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Requires7zFact]
    public void OpenAndExtract_EmptyFilesAndDirectories_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_empty_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "test_empty.7z");

        Directory.CreateDirectory(Path.Combine(inputDir, "carpeta_vacia"));
        File.WriteAllBytes(Path.Combine(inputDir, "vacio.txt"), []);
        File.WriteAllText(Path.Combine(inputDir, "con_datos.txt"), "Hola mundo");

        try
        {
            Run7z($"a -y -r \"{archivePath}\" \"{inputDir}\\*\"");

            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.True(archive.Entries.Count >= 3);

                var emptyFile = archive.Entries.FirstOrDefault(e => e.Name.EndsWith("vacio.txt", StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(emptyFile);
                Assert.True(emptyFile.IsEmptyStream);
                Assert.Equal(0, emptyFile.Size);

                var dirEntry = archive.Entries.FirstOrDefault(e => e.Name.Contains("carpeta_vacia", StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(dirEntry);
                Assert.True(dirEntry.IsDirectory);

                var dataEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith("con_datos.txt", StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(dataEntry);
                using var mem = new MemoryStream();
                archive.Extract(dataEntry, mem);
                Assert.Equal("Hola mundo", Encoding.UTF8.GetString(mem.ToArray()));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Requires7zFact]
    public void OpenAndExtract_BcjX86ExecutableFilter_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_bcj_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "test_bcj.7z");

        Directory.CreateDirectory(inputDir);

        try
        {
            byte[] binary = new byte[4096];
            new Random(77).NextBytes(binary);
            binary[100] = 0xE8;
            binary[101] = 0x50;
            binary[102] = 0x60;
            binary[103] = 0x70;
            binary[104] = 0x00;

            string binFile = Path.Combine(inputDir, "programa.exe");
            File.WriteAllBytes(binFile, binary);

            // -m0=bcj -m1=lzma2
            Run7z($"a -y \"{archivePath}\" \"{binFile}\" -m0=bcj -m1=lzma2");

            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.Single(archive.Entries);

                using var mem = new MemoryStream();
                archive.Extract(archive.Entries[0], mem);

                Assert.Equal(binary, mem.ToArray());
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Requires7zFact]
    public void OpenAndExtract_DeflateArchive_CreatedByOfficial7z_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_deflate_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "test_deflate.7z");

        Directory.CreateDirectory(inputDir);

        try
        {
            string testContent = "Prueba de compatibilidad con algoritmo Deflate estándar RFC 1951 en contenedor 7z.\n" +
                                 string.Concat(Enumerable.Repeat("Línea repetitiva para probar compresión Deflate.\n", 30));
            string testFilePath = Path.Combine(inputDir, "deflate.txt");
            File.WriteAllText(testFilePath, testContent, new UTF8Encoding(false));

            Run7z($"a -y \"{archivePath}\" \"{testFilePath}\" -m0=deflate");

            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.Single(archive.Entries);
                using var mem = new MemoryStream();
                archive.Extract(archive.Entries[0], mem);

                string result = Encoding.UTF8.GetString(mem.ToArray());
                Assert.Equal(testContent, result);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Requires7zFact]
    public void OpenAndExtract_BZip2Archive_CreatedByOfficial7z_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_bzip2_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "test_bzip2.7z");

        Directory.CreateDirectory(inputDir);

        try
        {
            string testContent = "Prueba de compatibilidad con algoritmo BZip2 (BWT + Huffman) en contenedor 7z.\n" +
                                 string.Concat(Enumerable.Repeat("Línea repetitiva para verificar transformación BWT y RLE.\n", 50));
            string testFilePath = Path.Combine(inputDir, "bzip2.txt");
            File.WriteAllText(testFilePath, testContent, new UTF8Encoding(false));

            Run7z($"a -y \"{archivePath}\" \"{testFilePath}\" -m0=bzip2");

            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.Single(archive.Entries);
                using var mem = new MemoryStream();
                archive.Extract(archive.Entries[0], mem);

                string result = Encoding.UTF8.GetString(mem.ToArray());
                Assert.Equal(testContent, result);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Requires7zFact]
    public void OpenAndExtract_Bcj2Archive_CreatedByOfficial7z_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_bcj2_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "test_bcj2.7z");

        Directory.CreateDirectory(inputDir);

        try
        {
            byte[] binary = new byte[8192];
            for (int i = 0; i < binary.Length - 10; i += 8)
            {
                binary[i] = 0xE8;
                BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(i + 1, 4), 1000 - i);
                binary[i + 5] = 0x90;
                binary[i + 6] = 0x90;
                binary[i + 7] = 0x90;
            }

            string binFile = Path.Combine(inputDir, "test_bcj2.exe");
            File.WriteAllBytes(binFile, binary);

            Run7z($"a -y \"{archivePath}\" \"{binFile}\" -m0=bcj2");

            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.Single(archive.Entries);
                using var mem = new MemoryStream();
                archive.Extract(archive.Entries[0], mem);

                Assert.Equal(binary, mem.ToArray());
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Requires7zFact]
    public void OpenAndExtract_Bcj2Lzma2Archive_CreatedByOfficial7z_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_bcj2_lzma_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "test_bcj2_lzma.7z");

        Directory.CreateDirectory(inputDir);

        try
        {
            byte[] binary = new byte[10000];
            for (int i = 0; i < binary.Length - 10; i += 10)
            {
                binary[i] = 0xE8;
                BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(i + 1, 4), 1000 - i);
                binary[i + 5] = 0xE9;
                BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(i + 6, 4), 2000 - i);
            }

            string binFile = Path.Combine(inputDir, "test_bcj2_lzma.exe");
            File.WriteAllBytes(binFile, binary);

            Run7z($"a -y \"{archivePath}\" \"{binFile}\" -m0=bcj2 -m1=lzma2 -m2=lzma2 -m3=lzma2");

            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.Single(archive.Entries);
                using var mem = new MemoryStream();
                archive.Extract(archive.Entries[0], mem);

                Assert.Equal(binary, mem.ToArray());
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Requires7zFact]
    public void OpenAndExtract_PpmdArchive_ExtractsIdentical()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_ppmd_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "test_ppmd.7z");

        Directory.CreateDirectory(inputDir);

        try
        {
            string testContent = "Prueba de algoritmo PPMd7.";
            string testFilePath = Path.Combine(inputDir, "ppmd.txt");
            File.WriteAllText(testFilePath, testContent, new UTF8Encoding(false));

            Run7z($"a -y \"{archivePath}\" \"{testFilePath}\" -m0=ppmd:o2");

            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.Single(archive.Entries);
                using var mem = new MemoryStream();
                archive.Extract(archive.Entries[0], mem);
                Assert.Equal(testContent, Encoding.UTF8.GetString(mem.ToArray()));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Requires7zFact]
    public void OpenAndExtract_SolidArchive_MultipleFiles_Success_AndCrcVerified()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_solid_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "test_solid.7z");

        Directory.CreateDirectory(inputDir);

        try
        {
            string file1 = Path.Combine(inputDir, "a.txt");
            string file2 = Path.Combine(inputDir, "b.txt");
            string file3 = Path.Combine(inputDir, "c.txt");

            string content1 = "Primer archivo en bloque sólido con texto repetido.\n" + string.Concat(Enumerable.Repeat("Línea 1...\n", 40));
            string content2 = "Segundo archivo completamente diferente en bloque sólido.\n" + string.Concat(Enumerable.Repeat("Línea 2...\n", 60));
            string content3 = "Tercer archivo pequeño para verificar offsets.";

            File.WriteAllText(file1, content1, new UTF8Encoding(false));
            File.WriteAllText(file2, content2, new UTF8Encoding(false));
            File.WriteAllText(file3, content3, new UTF8Encoding(false));

            // Crear archivo sólido oficial con -ms=on
            Run7z($"a -y \"{archivePath}\" \"{file1}\" \"{file2}\" \"{file3}\" -ms=on");

            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.Equal(3, archive.Entries.Count);

                // Todos los archivos deben tener su CRC-32 válido no nulo
                foreach (var entry in archive.Entries)
                {
                    Assert.NotNull(entry.Crc);
                    Assert.True(entry.Size > 0);
                }

                // Extraer individualmente cada archivo y verificar contenido exacto
                var entryA = archive.Entries.First(e => e.Name.EndsWith("a.txt", StringComparison.Ordinal));
                var entryB = archive.Entries.First(e => e.Name.EndsWith("b.txt", StringComparison.Ordinal));
                var entryC = archive.Entries.First(e => e.Name.EndsWith("c.txt", StringComparison.Ordinal));

                using var memA = new MemoryStream();
                archive.Extract(entryA, memA);
                Assert.Equal(content1, Encoding.UTF8.GetString(memA.ToArray()));

                using var memB = new MemoryStream();
                archive.Extract(entryB, memB);
                Assert.Equal(content2, Encoding.UTF8.GetString(memB.ToArray()));

                using var memC = new MemoryStream();
                archive.Extract(entryC, memC);
                Assert.Equal(content3, Encoding.UTF8.GetString(memC.ToArray()));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Requires7zFact]
    public void OpenAndExtract_Deflate64Archive_ExtractsIdentical()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_d64_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "test_d64.7z");

        Directory.CreateDirectory(inputDir);

        try
        {
            string testFilePath = Path.Combine(inputDir, "d64.txt");
            File.WriteAllText(testFilePath, "Contenido de prueba para Deflate64", new UTF8Encoding(false));

            Run7z($"a -y \"{archivePath}\" \"{testFilePath}\" -m0=deflate64");

            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.Single(archive.Entries);
                using var mem = new MemoryStream();
                archive.Extract(archive.Entries[0], mem);
                Assert.Equal("Contenido de prueba para Deflate64", Encoding.UTF8.GetString(mem.ToArray()));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ExtractAll_ZipSlipDirectoryTraversal_ThrowsCorruptArchiveException()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_zipslip_{Guid.NewGuid():N}");
        string archivePath = Path.Combine(tempDir, "slip.7z");
        string extractDir = Path.Combine(tempDir, "out");

        Directory.CreateDirectory(tempDir);

        try
        {
            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z { Method = CompressionMethod.Copy }))
            {
                using var ms = new MemoryStream("malicious"u8.ToArray());
                writer.Add(new WriterEntry7z { Name = "zz/evil.txt" }, ms);
                writer.Finish();
            }

            // El escritor rechaza "../": el nombre hostil se pone después, en la cabecera.
            HostileArchive.ReplaceName(archivePath, "zz/evil.txt", "../evil.txt");

            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.Throws<CorruptArchiveException>(() => archive.ExtractAll(extractDir));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void Run7z(string arguments)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Official7zFinder.ExecutablePath!,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        proc.Start();
        string output = proc.StandardOutput.ReadToEnd();
        string error = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000);

        Assert.True(proc.ExitCode == 0, $"7z.exe falló con código {proc.ExitCode}.\nSalida: {output}\nError: {error}");
    }
}
