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
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

/// <summary>
/// Escritor sólido y cadena de escritura en streaming (BCJ → LZMA2 → AES sin cargar el archivo).
/// </summary>
public class SolidWriterTests
{
    private static Dictionary<string, byte[]> SampleFiles()
    {
        var files = new Dictionary<string, byte[]>();
        for (int i = 0; i < 40; i++)
        {
            string text = string.Concat(Enumerable.Range(0, 30 + i).Select(k => $"registro {k} del caso {i % 7};\n"));
            files[$"docs/nota_{i:D2}.txt"] = Encoding.UTF8.GetBytes(text);
        }

        files["docs/vacio.txt"] = [];
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        byte[] code = File.ReadAllBytes(Path.Combine(runtimeDir, OperatingSystem.IsWindows() ? "coreclr.dll" : "libcoreclr.so"));
        files["bin/programa.exe"] = code.AsSpan(0, Math.Min(code.Length, 1 << 20)).ToArray();
        return files;
    }

    private static string Write(string dir, string name, Dictionary<string, byte[]> files, WriterOptions7z options)
    {
        string path = Path.Combine(dir, name);
        using var writer = Writer7z.Create(path, options);
        writer.Add(new WriterEntry7z { Name = "docs/", IsDirectory = true });
        foreach (var (entryName, data) in files)
        {
            writer.Add(new WriterEntry7z { Name = entryName }, new MemoryStream(data));
        }

        writer.Finish();
        return path;
    }

    private static (int ExitCode, string Output) Run7z(string arguments)
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
        return (proc.ExitCode, output);
    }

    [Requires7zTheory]
    [InlineData(null)]
    [InlineData("Clave123")]
    public void Solid_Official7zAndOwnReader_ExtractEverythingWithCrcs(string? password)
    {
        Official7zFinder.EnsureAvailable();
        string dir = Path.Combine(Path.GetTempPath(), $"7z_solido_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var files = SampleFiles();
            string path = Write(dir, "solido.7z", files, new WriterOptions7z
            {
                Solid = true,
                Password = password,
                EncryptHeaders = password != null
            });

            string pw = password != null ? $"-p{password}" : "";
            var (testCode, testOut) = Run7z($"t \"{path}\" {pw}");
            Assert.True(testCode == 0, testOut);

            var (_, listing) = Run7z($"l -slt \"{path}\" {pw}");
            Assert.Contains("Solid = +", listing);
            Assert.Contains("Blocks = 2", listing); // bloque sólido de texto + folder propio del .exe con BCJ
            int dataFiles = files.Count(f => f.Value.Length > 0);
            Assert.Equal(dataFiles, listing.Split('\n').Count(l => l.StartsWith("CRC = ", StringComparison.Ordinal) && l.Trim().Length > 6));

            string out7z = Path.Combine(dir, "x7z");
            var (xCode, xOut) = Run7z($"x \"{path}\" -o\"{out7z}\" -y {pw}");
            Assert.True(xCode == 0, xOut);

            string outOwn = Path.Combine(dir, "propio");
            using (var archive = Archive7z.Open(path, password))
            {
                archive.ExtractAll(outOwn);
            }

            foreach (var (name, data) in files)
            {
                Assert.Equal(data, File.ReadAllBytes(Path.Combine(out7z, name)));
                Assert.Equal(data, File.ReadAllBytes(Path.Combine(outOwn, name)));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Solid_IsSmallerThanOneFolderPerFile_AndBlockSizeSplitsIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"7z_solido_tam_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var files = SampleFiles();
            files.Remove("bin/programa.exe");
            long separate = new FileInfo(Write(dir, "separado.7z", files, new WriterOptions7z())).Length;
            long solid = new FileInfo(Write(dir, "solido.7z", files, new WriterOptions7z { Solid = true })).Length;
            Assert.True(solid < separate * 0.6, $"Sólido {solid} bytes vs separado {separate} bytes.");

            string split = Write(dir, "partido.7z", files, new WriterOptions7z { Solid = true, SolidBlockSize = 20_000 });
            int folders;
            using (var source = new PrZip.Primitives.IO.FileRandomAccessSource(split))
            {
                folders = PrZip.Container.Headers.ArchiveHeaderReader.Read(source).Folders.Count;
            }

            using var archive = Archive7z.Open(split);
            Assert.True(folders > 1, "SolidBlockSize no partió el bloque sólido.");
            foreach (var entry in archive.Entries.Where(e => !e.IsEmptyStream))
            {
                using var mem = new MemoryStream();
                archive.Extract(entry, mem);
                Assert.Equal(files[entry.Name], mem.ToArray());
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AesEncryptStream_ArbitraryWriteSizes_PadOnlyAtTheEnd()
    {
        // Antes, cada lectura que no fuera múltiplo de 16 se rellenaba con ceros en el medio del stream.
        byte[] data = new byte[100_003];
        new Random(9).NextBytes(data);

        using var encoder = new Aes7zEncoder("clave".AsSpan(), numCyclesPower: 4);
        using var oneShot = new MemoryStream();
        using (var s = encoder.CreateEncryptStream(oneShot))
        {
            s.Write(data);
            s.Finish();
        }

        using var pieces = new MemoryStream();
        using (var s = encoder.CreateEncryptStream(pieces))
        {
            int pos = 0;
            int step = 1;
            while (pos < data.Length)
            {
                int n = Math.Min(step, data.Length - pos);
                s.Write(data, pos, n);
                pos += n;
                step = step * 3 % 977 + 1;
            }

            s.Finish();
        }

        Assert.Equal(oneShot.ToArray(), pieces.ToArray());
        Assert.Equal((data.Length + 15) / 16 * 16, pieces.Length);
    }

    [Fact]
    public void Writer_LargeEncryptedExecutable_IsStreamedNotLoaded()
    {
        // Con contraseña o BCJ el escritor antes cargaba el archivo entero (y una copia) en memoria.
        string dir = Path.Combine(Path.GetTempPath(), $"7z_stream_w_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            const long size = 192L << 20;
            string path = Path.Combine(dir, "grande.7z");
            // Con Threads = 1 toda la escritura ocurre en este hilo: se mide solo lo que asigna él
            // (GetTotalAllocatedBytes sumaría los tests que xUnit corre en paralelo).
            long before = GC.GetAllocatedBytesForCurrentThread();
            using (var writer = Writer7z.Create(path, new WriterOptions7z { Level = 1, Password = "Clave123", Threads = 1 }))
            {
                writer.Add(new WriterEntry7z { Name = "imagen.exe" }, new ZeroStream(size));
                writer.Finish();
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 96L << 20, $"Se asignaron {allocated >> 20} MB para escribir {size >> 20} MB.");

            using var archive = Archive7z.Open(path, "Clave123");
            var entry = archive.Entries.Single();
            Assert.Equal(size, entry.Size);
            archive.Extract(entry, Stream.Null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
