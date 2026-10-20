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
using PrZip.Codecs.Crypto;
using PrZip.Exceptions;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

public class Crypto7zTests
{
    [Requires7zFact]
    public void OpenAndExtract_EncryptedLzma2Archive_WithPassword_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_crypto_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "encrypted.7z");
        const string password = "PasswordForensic#2026!";

        Directory.CreateDirectory(inputDir);

        try
        {
            string testContent = "Documento confidencial para probar descifrado AES-256 en .NET 10.\n" +
                                 string.Concat(Enumerable.Repeat("Texto secreto protegido con clave segura.\n", 80));
            string testFilePath = Path.Combine(inputDir, "secreto.txt");
            File.WriteAllText(testFilePath, testContent, new UTF8Encoding(false));

            // Comprimir y cifrar con 7z.exe oficial
            Run7z($"a -y \"{archivePath}\" \"{testFilePath}\" -p{password} -m0=lzma2");

            Assert.True(File.Exists(archivePath));

            // Abrir y extraer con Archive7z
            using (var archive = Archive7z.Open(archivePath, password))
            {
                Assert.Single(archive.Entries);
                var entry = archive.Entries[0];
                Assert.Equal("secreto.txt", entry.Name);
                Assert.True(entry.IsEncrypted);

                using var mem = new MemoryStream();
                archive.Extract(entry, mem);

                string extracted = Encoding.UTF8.GetString(mem.ToArray());
                Assert.Equal(testContent, extracted);
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
    public void OpenAndExtract_EncryptedArchive_WrongPassword_ThrowsInvalidPasswordOrCorruptDataException()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_wrongpass_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "encrypted.7z");
        const string correctPassword = "CorrectPassword123";
        const string wrongPassword = "IncorrectPassword999";

        Directory.CreateDirectory(inputDir);

        try
        {
            string testFilePath = Path.Combine(inputDir, "datos.txt");
            File.WriteAllText(testFilePath, "Datos confidenciales", new UTF8Encoding(false));

            Run7z($"a -y \"{archivePath}\" \"{testFilePath}\" -p{correctPassword}");

            using (var archive = Archive7z.Open(archivePath, wrongPassword))
            {
                Assert.Single(archive.Entries);
                var entry = archive.Entries[0];

                using var mem = new MemoryStream();
                Assert.Throws<InvalidPasswordOrCorruptDataException>(() =>
                {
                    archive.Extract(entry, mem);
                });
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
    public void OpenAndExtract_EncryptedArchive_NoPasswordProvided_ThrowsInvalidPasswordOrCorruptDataException()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_nopass_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "encrypted.7z");

        Directory.CreateDirectory(inputDir);

        try
        {
            string testFilePath = Path.Combine(inputDir, "datos.txt");
            File.WriteAllText(testFilePath, "Información sensible", new UTF8Encoding(false));

            Run7z($"a -y \"{archivePath}\" \"{testFilePath}\" -pProtegido7z");

            using (var archive = Archive7z.Open(archivePath, password: null))
            {
                Assert.Single(archive.Entries);
                var entry = archive.Entries[0];
                Assert.True(entry.IsEncrypted);

                using var mem = new MemoryStream();
                Assert.Throws<InvalidPasswordOrCorruptDataException>(() =>
                {
                    archive.Extract(entry, mem);
                });
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
    public void OpenAndExtract_EncryptedHeadersArchive_mhe_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_mhe_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "encrypted_headers.7z");
        const string password = "SuperSecretHeaderKey!";

        Directory.CreateDirectory(inputDir);

        try
        {
            string testContent = "Contenido con nombres y metadatos totalmente ocultos por -mhe.";
            string testFilePath = Path.Combine(inputDir, "archivo_oculto.txt");
            File.WriteAllText(testFilePath, testContent, new UTF8Encoding(false));

            // -mhe=on cifra las cabeceras completas (nombres, tamaños, estructura)
            Run7z($"a -y \"{archivePath}\" \"{testFilePath}\" -p{password} -mhe=on");

            // Intentar abrir sin contraseña debe fallar inmediatamente
            Assert.Throws<InvalidPasswordOrCorruptDataException>(() =>
            {
                using var _ = Archive7z.Open(archivePath, password: null);
            });

            // Intentar abrir con contraseña incorrecta debe fallar
            Assert.Throws<InvalidPasswordOrCorruptDataException>(() =>
            {
                using var _ = Archive7z.Open(archivePath, password: "ClaveInvalida");
            });

            // Abrir con la contraseña correcta
            using (var archive = Archive7z.Open(archivePath, password))
            {
                Assert.Single(archive.Entries);
                var entry = archive.Entries[0];
                Assert.Equal("archivo_oculto.txt", entry.Name);

                using var mem = new MemoryStream();
                archive.Extract(entry, mem);

                string extracted = Encoding.UTF8.GetString(mem.ToArray());
                Assert.Equal(testContent, extracted);
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
    public void OpenAndExtract_EncryptedCopyArchive_NoCompression_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_crypto_copy_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "encrypted_copy.7z");
        const string password = "PasswordCopy#123";

        Directory.CreateDirectory(inputDir);

        try
        {
            byte[] rawBytes = new byte[8192];
            new Random(999).NextBytes(rawBytes);
            string testFilePath = Path.Combine(inputDir, "data.bin");
            File.WriteAllBytes(testFilePath, rawBytes);

            // -mx=0 (Copy) + AES cifrado
            Run7z($"a -y \"{archivePath}\" \"{testFilePath}\" -p{password} -mx=0");

            using (var archive = Archive7z.Open(archivePath, password))
            {
                Assert.Single(archive.Entries);
                var entry = archive.Entries[0];
                Assert.True(entry.IsEncrypted);

                using var mem = new MemoryStream();
                archive.Extract(entry, mem);

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
    public void OpenAndExtract_EncryptedBcjExecutable_Success()
    {
        Official7zFinder.EnsureAvailable();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_test_crypto_bcj_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string archivePath = Path.Combine(tempDir, "encrypted_bcj.7z");
        const string password = "PasswordBcjExe#2026";

        Directory.CreateDirectory(inputDir);

        try
        {
            byte[] binary = new byte[4096];
            new Random(123).NextBytes(binary);
            binary[50] = 0xE8;
            binary[51] = 0x10;
            binary[52] = 0x20;
            binary[53] = 0x30;
            binary[54] = 0x00;

            string binFile = Path.Combine(inputDir, "app.exe");
            File.WriteAllBytes(binFile, binary);

            // -m0=bcj -m1=lzma2 + AES cifrado
            Run7z($"a -y \"{archivePath}\" \"{binFile}\" -p{password} -m0=bcj -m1=lzma2");

            using (var archive = Archive7z.Open(archivePath, password))
            {
                Assert.Single(archive.Entries);
                var entry = archive.Entries[0];
                Assert.True(entry.IsEncrypted);

                using var mem = new MemoryStream();
                archive.Extract(entry, mem);

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
