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
using PrZip.Exceptions;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

/// <summary>
/// Inflater propio (Deflate y Deflate64), arbitrado por 7z.exe.
/// </summary>
public class DeflateTests
{
    /// <summary>
    /// Código real, un bloque de 40 KB de azar repetido a más de 32 KB de distancia (distancias 30/31 de
    /// Deflate64) y corridas de ceros largas (largo 285 con 16 bits extra en Deflate64).
    /// </summary>
    private static byte[] Payload()
    {
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        byte[] code = File.ReadAllBytes(Path.Combine(runtimeDir, OperatingSystem.IsWindows() ? "coreclr.dll" : "libcoreclr.so"));
        byte[] noise = new byte[40_000];
        new Random(8).NextBytes(noise);
        using var ms = new MemoryStream();
        ms.Write(code.AsSpan(0, Math.Min(code.Length, 600_000)));
        ms.Write(noise);
        ms.Write(new byte[30_000]);
        ms.Write(noise);
        ms.Write(new byte[200_000]);
        return ms.ToArray();
    }

    private static void Run7z(string arguments)
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
        Assert.True(proc.ExitCode == 0, output);
    }

    [Requires7zTheory]
    [InlineData("-m0=Deflate -mx1")]
    [InlineData("-m0=Deflate -mx9")]
    [InlineData("-m0=Deflate64")]
    [InlineData("-m0=Deflate64 -mx9")]
    public void Official7z_Deflate_OurReaderExtractsIdentical(string switches)
    {
        Official7zFinder.EnsureAvailable();
        string dir = Path.Combine(Path.GetTempPath(), $"7z_deflate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = Payload();
            string input = Path.Combine(dir, "datos.bin");
            File.WriteAllBytes(input, data);
            string archivePath = Path.Combine(dir, "deflate.7z");
            Run7z($"a \"{archivePath}\" \"{input}\" {switches} -mf=off");

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
    public void CorruptedDeflate_AlwaysThrowsOwnException()
    {
        // Antes se usaba DeflateStream de la BCL, que ante datos dañados lanzaba InvalidDataException
        // en lugar de las excepciones del motor.
        Official7zFinder.EnsureAvailable();
        string dir = Path.Combine(Path.GetTempPath(), $"7z_deflate_fz_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            byte[] data = Payload().AsSpan(0, 200_000).ToArray();
            string input = Path.Combine(dir, "datos.bin");
            File.WriteAllBytes(input, data);
            string archivePath = Path.Combine(dir, "deflate.7z");
            Run7z($"a \"{archivePath}\" \"{input}\" -m0=Deflate -mf=off");
            byte[] original = File.ReadAllBytes(archivePath);
            long packSize = BitConverter.ToInt64(original, 12);

            var rnd = new Random(77);
            for (int round = 0; round < 200; round++)
            {
                byte[] bytes = (byte[])original.Clone();
                bytes[32 + rnd.Next((int)packSize)] ^= (byte)(1 << rnd.Next(8));
                string path = Path.Combine(dir, "fz.7z");
                File.WriteAllBytes(path, bytes);

                var ex = Record.Exception(() =>
                {
                    using var archive = Archive7z.Open(path);
                    archive.Extract(archive.Entries.Single(), Stream.Null);
                });

                Assert.NotNull(ex);
                Assert.IsAssignableFrom<PrZipException>(ex);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
