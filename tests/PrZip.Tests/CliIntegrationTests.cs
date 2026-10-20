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
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

public class CliIntegrationTests
{
    private static string GetCliExecutablePath()
    {
        // Ruta del ejecutable AOT o Release si existe, o Debug
        string aotPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\tools\PrZip.Cli\bin\Release\net10.0\win-x64\publish\PrZip.Cli.exe"));
        if (File.Exists(aotPath))
        {
            return aotPath;
        }

        string debugPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\tools\PrZip.Cli\bin\Debug\net10.0\PrZip.Cli.exe"));
        if (File.Exists(debugPath))
        {
            return debugPath;
        }

        throw new FileNotFoundException("No se encontró el ejecutable de PrZip.Cli");
    }

    [Requires7zFact]
    public void Cli_CreateListTestExtract_RoundTrip_WithOfficial7zInteroperability()
    {
        Official7zFinder.EnsureAvailable();
        string cliExe = GetCliExecutablePath();

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_cli_test_{Guid.NewGuid():N}");
        string inputDir = Path.Combine(tempDir, "input");
        string extractDirCli = Path.Combine(tempDir, "extract_cli");
        string extractDir7z = Path.Combine(tempDir, "extract_7z");
        string archivePath = Path.Combine(tempDir, "archivo_cli.7z");

        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(extractDirCli);
        Directory.CreateDirectory(extractDir7z);

        try
        {
            // 1. Crear archivos de prueba
            string file1 = Path.Combine(inputDir, "test1.txt");
            string file2 = Path.Combine(inputDir, "test2.bin");
            File.WriteAllText(file1, "Hola mundo desde PrZip CLI Native AOT!");
            byte[] binData = new byte[10000];
            new Random(99).NextBytes(binData);
            File.WriteAllBytes(file2, binData);

            // 2. Ejecutar CLI: 7z a <archivo.7z> <inputDir>
            var resA = RunProcess(cliExe, $"a \"{archivePath}\" \"{file1}\" \"{file2}\" -mx=5 -mmt=2");
            Assert.Equal(0, resA.ExitCode);
            Assert.Contains("Todo correcto", resA.Stdout);
            Assert.True(File.Exists(archivePath));

            // 3. Ejecutar CLI: 7z l <archivo.7z>
            var resL = RunProcess(cliExe, $"l \"{archivePath}\"");
            Assert.Equal(0, resL.ExitCode);
            Assert.Contains("test1.txt", resL.Stdout);
            Assert.Contains("test2.bin", resL.Stdout);

            // 4. Ejecutar CLI: 7z t <archivo.7z>
            var resT = RunProcess(cliExe, $"t \"{archivePath}\"");
            Assert.Equal(0, resT.ExitCode);
            Assert.Contains("Todo correcto", resT.Stdout);

            // 5. Validar con 7z.exe oficial
            string official7z = Official7zFinder.ExecutablePath!;
            var res7zT = RunProcess(official7z, $"t -y \"{archivePath}\"");
            Assert.Equal(0, res7zT.ExitCode);

            // 6. Extraer con 7z.exe oficial
            var res7zX = RunProcess(official7z, $"x -y \"{archivePath}\" -o\"{extractDir7z}\"");
            Assert.Equal(0, res7zX.ExitCode);
            Assert.True(File.Exists(Path.Combine(extractDir7z, "test1.txt")));
            Assert.True(File.Exists(Path.Combine(extractDir7z, "test2.bin")));

            // 7. Extraer con NUESTRA CLI
            var resCliX = RunProcess(cliExe, $"x \"{archivePath}\" -o\"{extractDirCli}\" -y");
            Assert.Equal(0, resCliX.ExitCode);
            Assert.Contains("Todo correcto", resCliX.Stdout);

            string extracted1 = Path.Combine(extractDirCli, "test1.txt");
            string extracted2 = Path.Combine(extractDirCli, "test2.bin");
            Assert.True(File.Exists(extracted1));
            Assert.True(File.Exists(extracted2));
            Assert.Equal("Hola mundo desde PrZip CLI Native AOT!", File.ReadAllText(extracted1));
            Assert.Equal(binData, File.ReadAllBytes(extracted2));
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
    public void Cli_CreateEncryptedArchive_Official7zExtractsWithPassword()
    {
        Official7zFinder.EnsureAvailable();
        string cliExe = GetCliExecutablePath();
        string official7z = Official7zFinder.ExecutablePath!;

        string tempDir = Path.Combine(Path.GetTempPath(), $"7z_cli_enc_{Guid.NewGuid():N}");
        string extractDir7z = Path.Combine(tempDir, "extract_7z");
        string archivePath = Path.Combine(tempDir, "archivo_enc.7z");
        string password = "PasswordSeguro123!";

        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir7z);

        try
        {
            string testFile = Path.Combine(tempDir, "secreto.txt");
            string secretContent = "Contenido ultra confidencial protegido con AES-256";
            File.WriteAllText(testFile, secretContent);

            // Crear con CLI cifrado con contraseña y cifrado de encabezados (-p y -mhe)
            var resA = RunProcess(cliExe, $"a \"{archivePath}\" \"{testFile}\" -p{password} -mhe=on");
            Assert.Equal(0, resA.ExitCode);
            Assert.True(File.Exists(archivePath));

            // Probar sin contraseña en 7z oficial (debe fallar)
            var resFail = RunProcess(official7z, $"t -y \"{archivePath}\" -p\"incorrecta\"");
            Assert.NotEqual(0, resFail.ExitCode);

            // Extraer con contraseña correcta en 7z oficial
            var resX = RunProcess(official7z, $"x -y \"{archivePath}\" -p{password} -o\"{extractDir7z}\"");
            Assert.Equal(0, resX.ExitCode);

            string extractedSecret = Path.Combine(extractDir7z, "secreto.txt");
            Assert.True(File.Exists(extractedSecret));
            Assert.Equal(secretContent, File.ReadAllText(extractedSecret));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"No se pudo iniciar {exe}");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        return (proc.ExitCode, stdout, stderr);
    }
}
