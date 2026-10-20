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
using PrZip.Codecs.Crypto;
using PrZip.Container.Extraction;
using PrZip.Container.Headers;
using PrZip.Primitives.IO;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

/// <summary>
/// La clave 7zAES (2^19 rondas de SHA-256) se deriva una vez por archivo y no una vez por folder: sin modo
/// sólido hay un folder cifrado por entrada, y derivar en cada uno duplicaba el tiempo de escritura y
/// multiplicaba por cinco el de extracción.
/// </summary>
public class AesKeyReuseTests
{
    private const string Password = "Clave123";

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
        proc.WaitForExit(60000);
        Assert.True(proc.ExitCode == 0, output);
    }

    private static string MakeSourceDir(string root, int files)
    {
        string src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        for (int i = 0; i < files; i++)
        {
            string text = string.Concat(Enumerable.Range(0, 50 + i).Select(k => $"renglón {k} del archivo {i};\n"));
            File.WriteAllText(Path.Combine(src, $"nota_{i:D2}.txt"), text, Encoding.UTF8);
        }

        return src;
    }

    [Requires7zFact]
    public void Writer_AllFoldersShareSaltAndKey_EachWithItsOwnIv()
    {
        Official7zFinder.EnsureAvailable();
        string dir = Path.Combine(Path.GetTempPath(), $"7z_aes_clave_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string src = MakeSourceDir(dir, 12);
            string path = Path.Combine(dir, "cifrado.7z");
            using (var writer = Writer7z.Create(path, new WriterOptions7z { Password = Password, EncryptHeaders = true, Threads = 4 }))
            {
                foreach (string file in Directory.GetFiles(src).Order(StringComparer.Ordinal))
                {
                    writer.Add(file, Path.GetFileName(file));
                }
            }

            ArchiveMetadata metadata;
            using (var source = new FileRandomAccessSource(path))
            {
                metadata = ArchiveHeaderReader.Read(source, Password);
            }

            var aes = metadata.Folders
                .Select(f => Aes7zProperties.Decode(f.Coders.Single(c => MethodId.Equals(c.MethodId, MethodId.Aes256)).Properties))
                .ToList();
            Assert.Equal(12, aes.Count);
            Assert.All(aes, p => Assert.Equal(16, p.Salt.Length));
            Assert.Single(aes.Select(p => Convert.ToHexString(p.Salt)).Distinct());
            Assert.Equal(12, aes.Select(p => Convert.ToHexString(p.Iv)).Distinct().Count());

            Run7z($"t \"{path}\" -p{Password}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Requires7zFact]
    public void Reader_7zArchiveWithManyEncryptedFolders_DerivesTheKeyOnce()
    {
        Official7zFinder.EnsureAvailable();
        string dir = Path.Combine(Path.GetTempPath(), $"7z_aes_lector_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string src = MakeSourceDir(dir, 10);
            string path = Path.Combine(dir, "de7zip.7z");
            Run7z($"a \"{path}\" \"{src}{Path.DirectorySeparatorChar}*\" -ms=off -mhe=on -p{Password}");

            using var keys = new Aes7zKeyCache(Password);
            using var source = new FileRandomAccessSource(path);
            var metadata = ArchiveHeaderReader.Read(source, Password, keys);
            Assert.Equal(10, metadata.Folders.Count);

            byte[] bytes = File.ReadAllBytes(path);
            long offset = SignatureHeader.HeaderSize + metadata.PackInfo!.PackPos;
            for (int i = 0; i < metadata.Folders.Count; i++)
            {
                // Un stream empaquetado por folder, contiguos y en orden (LZMA2 + AES, sin BCJ2).
                long size = metadata.PackInfo.PackSizes[i];
                using var packed = new MemoryStream(bytes, (int)offset, (int)size, writable: false);
                var folder = metadata.Folders[i];
                FolderDecoder.Decode(folder, packed, Stream.Null, folder.GetUnpackSize(), Password, keys);
                offset += size;
            }

            // Cabecera cifrada y diez folders: 7-Zip escribe sal vacía, así que todos usan la misma clave.
            Assert.Equal(1, keys.Derivations);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void KeyCache_DifferentSalts_DeriveSeparately_AndCopiesAreIndependent()
    {
        using var keys = new Aes7zKeyCache(Password);
        byte[] a1 = keys.GetKey([1, 2, 3], 6);
        byte[] a2 = keys.GetKey([1, 2, 3], 6);
        byte[] b = keys.GetKey([9, 9, 9], 6);
        byte[] c = keys.GetKey([1, 2, 3], 7);

        Assert.Equal(3, keys.Derivations);
        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.NotEqual(a1, c);
        Assert.Equal(Aes7zDecoder.DeriveKey(Password.AsSpan(), [1, 2, 3], 6), a1);

        // Quien recibe la clave puede borrarla sin afectar al caché.
        Array.Clear(a1);
        Assert.Equal(a2, keys.GetKey([1, 2, 3], 6));
    }
}
