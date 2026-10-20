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

namespace PrZip.Tests.Infrastructure;

/// <summary>
/// Descubridor portable del binario oficial de 7-Zip (7z.exe / 7zz).
/// Busca en OFFICIAL_7Z_EXE, PATH del sistema y rutas de instalación estándar en Windows y Linux.
/// Con OFFICIAL_7Z_EXE=none se comporta como si no estuviera: sirve para probar la suite tal como corre en una
/// máquina sin 7-Zip (los tests que lo necesitan se saltean, ver <see cref="Requires7zFactAttribute"/>).
/// </summary>
public static class Official7zFinder
{
    private static readonly Lazy<(string? Path, string? Version)> ResolvedExecutable = new(Resolve);

    public static string? ExecutablePath => ResolvedExecutable.Value.Path;

    public static bool IsAvailable => !string.IsNullOrEmpty(ExecutablePath);

    public static string? Version => ResolvedExecutable.Value.Version;

    public static void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "No se encontró el ejecutable oficial de 7-Zip ('7z.exe' o '7zz'). " +
                "Configura la variable de entorno OFFICIAL_7Z_EXE o asegúrate de que esté instalado en el PATH o en 'C:\\Program Files\\7-Zip\\7z.exe'.");
        }
    }

    private static (string? Path, string? Version) Resolve()
    {
        // 1. Variable de entorno explícita
        string? envPath = Environment.GetEnvironmentVariable("OFFICIAL_7Z_EXE");
        if (string.Equals(envPath, "none", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return (envPath, ProbeVersion(envPath));
        }

        // 2. Nombres según sistema operativo
        bool isWindows = OperatingSystem.IsWindows();
        string[] binaryNames = isWindows
            ? ["7z.exe", "7zz.exe", "7za.exe"]
            : ["7zz", "7z", "7za"];

        // 3. Rutas fijas comunes
        string[] commonPaths = isWindows
            ?
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe"
            ]
            :
            [
                "/usr/bin/7zz",
                "/usr/bin/7z",
                "/usr/local/bin/7zz",
                "/usr/local/bin/7z"
            ];

        foreach (string path in commonPaths)
        {
            if (File.Exists(path))
            {
                return (path, ProbeVersion(path));
            }
        }

        // 4. Búsqueda en PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            char separator = isWindows ? ';' : ':';
            string[] directories = pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (string dir in directories)
            {
                foreach (string binary in binaryNames)
                {
                    try
                    {
                        string candidate = Path.Combine(dir, binary);
                        if (File.Exists(candidate))
                        {
                            return (candidate, ProbeVersion(candidate));
                        }
                    }
                    catch
                    {
                        // Directorio inválido en PATH
                    }
                }
            }
        }

        return (null, null);
    }

    private static string? ProbeVersion(string path)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            // Buscar primera línea con versión
            using var reader = new StringReader(output);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains("7-Zip", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Trim();
                }
            }

            return "7-Zip (versión no identificada)";
        }
        catch
        {
            return "7-Zip (error al consultar versión)";
        }
    }
}
