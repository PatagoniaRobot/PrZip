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
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

public class ArchiveWriterTests
{
    [Requires7zFact]
    public void CreateArchive_CopyMode_Official7z_TestsAndExtracts_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_writer_copy_{Guid.NewGuid():N}");
        string extractDir7z = Path.Combine(tempDir, "extracted_7z");
        string extractDirOur = Path.Combine(tempDir, "extracted_our");
        string archivePath = Path.Combine(tempDir, "salida_copy.7z");

        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir7z);
        Directory.CreateDirectory(extractDirOur);

        try
        {
            // 1. Preparar datos
            string textContent = "Texto escrito por el nuevo Writer7z en .NET 10.\n" +
                                 string.Concat(Enumerable.Repeat("Línea de prueba sin compresión (Copy).\n", 50));
            byte[] textBytes = Encoding.UTF8.GetBytes(textContent);

            byte[] binBytes = new byte[8192];
            new Random(42).NextBytes(binBytes);

            // 2. Crear archivo 7z con nuestro motor
            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z { Method = CompressionMethod.Copy }))
            {
                using var textMs = new MemoryStream(textBytes);
                writer.Add(new WriterEntry7z { Name = "carpeta/documento.txt" }, textMs);

                using var binMs = new MemoryStream(binBytes);
                writer.Add(new WriterEntry7z { Name = "datos.bin" }, binMs);

                writer.Add(new WriterEntry7z { Name = "vacio.txt" }, Stream.Null);
                writer.Add(new WriterEntry7z { Name = "subcarpeta/", IsDirectory = true });

                writer.Finish();
            }

            Assert.True(File.Exists(archivePath));

            // 3. Validar con el 7z.exe oficial: 7z t (probar integridad)
            Run7z($"t -y \"{archivePath}\"");

            // 4. Extraer con el 7z.exe oficial: 7z x
            Run7z($"x -y \"{archivePath}\" -o\"{extractDir7z}\"");

            // Verificar hashes de archivos extraídos por 7z.exe
            string doc7z = Path.Combine(extractDir7z, "carpeta", "documento.txt");
            string bin7z = Path.Combine(extractDir7z, "datos.bin");
            string vacio7z = Path.Combine(extractDir7z, "vacio.txt");

            Assert.True(File.Exists(doc7z));
            Assert.True(File.Exists(bin7z));
            Assert.True(File.Exists(vacio7z));

            Assert.Equal(SHA256.HashData(textBytes), SHA256.HashData(File.ReadAllBytes(doc7z)));
            Assert.Equal(SHA256.HashData(binBytes), SHA256.HashData(File.ReadAllBytes(bin7z)));
            Assert.Equal(0, new FileInfo(vacio7z).Length);

            // 5. Round-trip completo: Abrir y extraer también con NUESTRO lector Archive7z
            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.True(archive.Entries.Count >= 4);
                archive.ExtractAll(extractDirOur);
            }

            string docOur = Path.Combine(extractDirOur, "carpeta", "documento.txt");
            string binOur = Path.Combine(extractDirOur, "datos.bin");
            Assert.Equal(SHA256.HashData(textBytes), SHA256.HashData(File.ReadAllBytes(docOur)));
            Assert.Equal(SHA256.HashData(binBytes), SHA256.HashData(File.ReadAllBytes(binOur)));
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
    public void CreateArchive_Lzma2UncompressedChunks_Official7z_TestsAndExtracts_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_writer_lzma2_{Guid.NewGuid():N}");
        string extractDir7z = Path.Combine(tempDir, "extracted_7z");
        string archivePath = Path.Combine(tempDir, "salida_lzma2.7z");

        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir7z);

        try
        {
            // 1. Preparar archivo de más de 64 KB (150 KB) para forzar múltiples chunks LZMA2 (0x01 + 0x02)
            byte[] largeBytes = new byte[150 * 1024];
            new Random(777).NextBytes(largeBytes);

            string textContent = "Prueba de chunks no comprimidos LZMA2 según el estándar 7z.\n";
            byte[] textBytes = Encoding.UTF8.GetBytes(textContent);

            // 2. Crear archivo con Writer7z en modo Lzma2
            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z { Method = CompressionMethod.Lzma2 }))
            {
                using var largeMs = new MemoryStream(largeBytes);
                writer.Add(new WriterEntry7z { Name = "payload_grande.bin" }, largeMs);

                using var textMs = new MemoryStream(textBytes);
                writer.Add(new WriterEntry7z { Name = "nota.txt" }, textMs);

                writer.Finish();
            }

            Assert.True(File.Exists(archivePath));

            // 3. Validar con 7z.exe oficial (7z t)
            Run7z($"t -y \"{archivePath}\"");

            // 4. Extraer con 7z.exe oficial (7z x)
            Run7z($"x -y \"{archivePath}\" -o\"{extractDir7z}\"");

            string largeExtracted = Path.Combine(extractDir7z, "payload_grande.bin");
            string noteExtracted = Path.Combine(extractDir7z, "nota.txt");

            Assert.True(File.Exists(largeExtracted));
            Assert.True(File.Exists(noteExtracted));

            Assert.Equal(SHA256.HashData(largeBytes), SHA256.HashData(File.ReadAllBytes(largeExtracted)));
            Assert.Equal(SHA256.HashData(textBytes), SHA256.HashData(File.ReadAllBytes(noteExtracted)));

            // 5. Round-trip: Abrir con Archive7z
            using var archive = Archive7z.Open(archivePath);
            Assert.Equal(2, archive.Entries.Count);

            using var mem = new MemoryStream();
            archive.Extract(archive.Entries[0], mem);
            Assert.Equal(largeBytes, mem.ToArray());
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
    public void CreateArchive_Lzma2Compressed_Official7z_TestsAndExtracts_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_writer_lzma2_{Guid.NewGuid():N}");
        string extractDir7z = Path.Combine(tempDir, "extracted_7z");
        string archivePath = Path.Combine(tempDir, "salida_lzma2.7z");

        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir7z);

        try
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 1500; i++)
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Patagonia Robot registro #{i} con datos estructurados LZMA2.");
            }
            byte[] textBytes = Encoding.UTF8.GetBytes(sb.ToString());

            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z { Method = CompressionMethod.Lzma2, Level = 5 }))
            {
                using var textMs = new MemoryStream(textBytes);
                writer.Add(new WriterEntry7z { Name = "registro.log" }, textMs);
                writer.Finish();
            }

            Assert.True(File.Exists(archivePath));

            // Verificar compresión real: el archivo 7z debe ser mucho más chico que el texto original
            var fileInfo = new FileInfo(archivePath);
            Assert.True(fileInfo.Length < textBytes.Length / 3, $"Tamaño del archivo ({fileInfo.Length}) no redujo al menos un tercio de {textBytes.Length}.");

            // 7z t
            Run7z($"t -y \"{archivePath}\"");

            // 7z x
            Run7z($"x -y \"{archivePath}\" -o\"{extractDir7z}\"");

            string extractedLog = Path.Combine(extractDir7z, "registro.log");
            Assert.True(File.Exists(extractedLog));
            Assert.Equal(SHA256.HashData(textBytes), SHA256.HashData(File.ReadAllBytes(extractedLog)));

            // Round-trip con Archive7z
            using var archive = Archive7z.Open(archivePath);
            Assert.Single(archive.Entries);
            using var mem = new MemoryStream();
            archive.Extract(archive.Entries[0], mem);
            Assert.Equal(textBytes, mem.ToArray());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Requires7zFact]
    public void CreateArchive_Lzma1Compressed_Official7z_TestsAndExtracts_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_writer_lzma1_{Guid.NewGuid():N}");
        string extractDir7z = Path.Combine(tempDir, "extracted_7z");
        string archivePath = Path.Combine(tempDir, "salida_lzma1.7z");

        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir7z);

        try
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 1000; i++)
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"LZMA1 clásico bloque repetitivo #{i} para validación cruzada.");
            }
            byte[] textBytes = Encoding.UTF8.GetBytes(sb.ToString());

            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z { Method = CompressionMethod.Lzma, Level = 5 }))
            {
                using var textMs = new MemoryStream(textBytes);
                writer.Add(new WriterEntry7z { Name = "documento_lzma1.txt" }, textMs);
                writer.Finish();
            }

            Assert.True(File.Exists(archivePath));

            Run7z($"t -y \"{archivePath}\"");
            Run7z($"x -y \"{archivePath}\" -o\"{extractDir7z}\"");

            string extractedDoc = Path.Combine(extractDir7z, "documento_lzma1.txt");
            Assert.True(File.Exists(extractedDoc));
            Assert.Equal(SHA256.HashData(textBytes), SHA256.HashData(File.ReadAllBytes(extractedDoc)));

            using var archive = Archive7z.Open(archivePath);
            using var mem = new MemoryStream();
            archive.Extract(archive.Entries[0], mem);
            Assert.Equal(textBytes, mem.ToArray());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Requires7zFact]
    public void CreateArchive_AesEncrypted_Official7z_TestsAndExtracts_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_writer_aes_{Guid.NewGuid():N}");
        string extractDir7z = Path.Combine(tempDir, "extracted_7z");
        string archivePath = Path.Combine(tempDir, "salida_cifrada.7z");
        const string password = "ClaveSecreta7z#2026";

        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir7z);

        try
        {
            byte[] secretBytes = Encoding.UTF8.GetBytes("Información altamente confidencial protegida con AES-256-CBC en 7z.");

            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z
            {
                Method = CompressionMethod.Lzma2,
                Password = password
            }))
            {
                using var secMs = new MemoryStream(secretBytes);
                writer.Add(new WriterEntry7z { Name = "confidencial.txt" }, secMs);
                writer.Finish();
            }

            Assert.True(File.Exists(archivePath));

            // Probar integridad con la clave correcta
            Run7z($"t -y -p{password} \"{archivePath}\"");

            // Extraer con 7z oficial
            Run7z($"x -y -p{password} \"{archivePath}\" -o\"{extractDir7z}\"");

            string extractedSecret = Path.Combine(extractDir7z, "confidencial.txt");
            Assert.True(File.Exists(extractedSecret));
            Assert.Equal(SHA256.HashData(secretBytes), SHA256.HashData(File.ReadAllBytes(extractedSecret)));

            // Round-trip con Archive7z
            using var archive = Archive7z.Open(archivePath, password);
            using var mem = new MemoryStream();
            archive.Extract(archive.Entries[0], mem);
            Assert.Equal(secretBytes, mem.ToArray());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Requires7zFact]
    public void CreateArchive_EncryptedHeaders_Official7z_TestsAndExtracts_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_writer_mhe_{Guid.NewGuid():N}");
        string extractDir7z = Path.Combine(tempDir, "extracted_7z");
        string archivePath = Path.Combine(tempDir, "salida_mhe.7z");
        const string password = "UltraPassword!2026";

        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir7z);

        try
        {
            byte[] sensitiveBytes = Encoding.UTF8.GetBytes("Archivo cuyos nombres de fichero también deben estar ocultos (-mhe=on).");

            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z
            {
                Method = CompressionMethod.Lzma2,
                Password = password,
                EncryptHeaders = true
            }))
            {
                using var secMs = new MemoryStream(sensitiveBytes);
                writer.Add(new WriterEntry7z { Name = "top_secret_filename.txt" }, secMs);
                writer.Finish();
            }

            Assert.True(File.Exists(archivePath));

            // Con la clave correcta: probar y extraer
            Run7z($"t -y -p{password} \"{archivePath}\"");
            Run7z($"x -y -p{password} \"{archivePath}\" -o\"{extractDir7z}\"");

            string extractedTopSecret = Path.Combine(extractDir7z, "top_secret_filename.txt");
            Assert.True(File.Exists(extractedTopSecret));
            Assert.Equal(SHA256.HashData(sensitiveBytes), SHA256.HashData(File.ReadAllBytes(extractedTopSecret)));

            // Round-trip con Archive7z
            using var archive = Archive7z.Open(archivePath, password);
            Assert.Single(archive.Entries);
            Assert.Equal("top_secret_filename.txt", archive.Entries[0].Name);

            using var mem = new MemoryStream();
            archive.Extract(archive.Entries[0], mem);
            Assert.Equal(sensitiveBytes, mem.ToArray());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Requires7zFact]
    public void CreateArchive_BcjExecutableFilter_Official7z_TestsAndExtracts_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_writer_bcj_{Guid.NewGuid():N}");
        string extractDir7z = Path.Combine(tempDir, "extracted_7z");
        string archivePath = Path.Combine(tempDir, "salida_bcj.7z");

        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir7z);

        try
        {
            // Simular código máquina x86 con saltos CALL 0xE8 y JMP 0xE9
            byte[] exeBytes = new byte[8192];
            for (int i = 0; i < exeBytes.Length - 10; i += 6)
            {
                exeBytes[i] = 0xE8; // CALL rel32
                BinaryPrimitives.WriteInt32LittleEndian(exeBytes.AsSpan(i + 1, 4), 0x00001000 - i);
                exeBytes[i + 5] = 0x90; // NOP
            }

            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z
            {
                Method = CompressionMethod.Lzma2,
                AutoDetectFilters = true
            }))
            {
                using var exeMs = new MemoryStream(exeBytes);
                writer.Add(new WriterEntry7z { Name = "programa.exe" }, exeMs);
                writer.Finish();
            }

            Assert.True(File.Exists(archivePath));

            Run7z($"t -y \"{archivePath}\"");
            Run7z($"x -y \"{archivePath}\" -o\"{extractDir7z}\"");

            string extractedExe = Path.Combine(extractDir7z, "programa.exe");
            Assert.True(File.Exists(extractedExe));
            Assert.Equal(SHA256.HashData(exeBytes), SHA256.HashData(File.ReadAllBytes(extractedExe)));

            using var archive = Archive7z.Open(archivePath);
            using var mem = new MemoryStream();
            archive.Extract(archive.Entries[0], mem);
            Assert.Equal(exeBytes, mem.ToArray());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Requires7zFact]
    public void CreateArchive_BcjAndAes_Official7z_TestsAndExtracts_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_writer_bcj_aes_{Guid.NewGuid():N}");
        string extractDir7z = Path.Combine(tempDir, "extracted_7z");
        string archivePath = Path.Combine(tempDir, "salida_bcj_aes.7z");
        const string password = "BcjAesPassword2026!";

        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir7z);

        try
        {
            byte[] exeBytes = new byte[8192];
            for (int i = 0; i < exeBytes.Length - 10; i += 6)
            {
                exeBytes[i] = 0xE8; // CALL rel32
                BinaryPrimitives.WriteInt32LittleEndian(exeBytes.AsSpan(i + 1, 4), 0x00001000 - i);
                exeBytes[i + 5] = 0x90; // NOP
            }

            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z
            {
                Method = CompressionMethod.Lzma2,
                AutoDetectFilters = true,
                Password = password
            }))
            {
                using var exeMs = new MemoryStream(exeBytes);
                writer.Add(new WriterEntry7z { Name = "programa_protegido.exe" }, exeMs);
                writer.Finish();
            }

            Assert.True(File.Exists(archivePath));

            Run7z($"t -y -p{password} \"{archivePath}\"");
            Run7z($"x -y -p{password} \"{archivePath}\" -o\"{extractDir7z}\"");

            string extractedExe = Path.Combine(extractDir7z, "programa_protegido.exe");
            Assert.True(File.Exists(extractedExe));
            Assert.Equal(SHA256.HashData(exeBytes), SHA256.HashData(File.ReadAllBytes(extractedExe)));

            using var archive = Archive7z.Open(archivePath, password);
            using var mem = new MemoryStream();
            archive.Extract(archive.Entries[0], mem);
            Assert.Equal(exeBytes, mem.ToArray());
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
        proc.WaitForExit(15000);

        Assert.True(proc.ExitCode == 0, $"7z.exe falló con código {proc.ExitCode}.\nSalida: {output}\nError: {error}");
    }
}
