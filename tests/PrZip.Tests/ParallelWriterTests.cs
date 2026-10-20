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

/// <summary>
/// Sin modo sólido y con varios hilos, los archivos agregados por ruta se comprimen a la vez y se
/// escriben en el orden en que se agregaron: el resultado debe ser idéntico al monohilo.
/// </summary>
public class ParallelWriterTests
{
    /// <summary>
    /// Archivos en disco de tamaños dispares (uno lento entre muchos chicos, uno vacío, un ejecutable con
    /// BCJ) más entradas por stream y carpetas intercaladas, que obligan a vaciar la fila en medio.
    /// </summary>
    private static List<(string Name, string? Path, byte[]? Data)> SampleEntries(string dir)
    {
        var entries = new List<(string, string?, byte[]?)>();
        void AddFile(string name, byte[] data)
        {
            string path = Path.Combine(dir, name.Replace('/', '_'));
            File.WriteAllBytes(path, data);
            entries.Add((name, path, data));
        }

        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        byte[] code = File.ReadAllBytes(Path.Combine(runtimeDir, OperatingSystem.IsWindows() ? "coreclr.dll" : "libcoreclr.so"));
        AddFile("bin/programa.dll", code.AsSpan(0, Math.Min(code.Length, 3 << 20)).ToArray());
        for (int i = 0; i < 60; i++)
        {
            string text = string.Concat(Enumerable.Range(0, 20 + 15 * i).Select(k => $"registro {k} del caso {i % 9};\n"));
            AddFile($"docs/nota_{i:D2}.txt", Encoding.UTF8.GetBytes(text));
            if (i == 30)
            {
                entries.Add(("docs/sub/", null, null));
                entries.Add(("docs/desde_stream.txt", null, Encoding.UTF8.GetBytes("agregado como stream, en el medio")));
            }
        }

        AddFile("docs/vacio.txt", []);
        return entries;
    }

    private static byte[] Write(string path, List<(string Name, string? Path, byte[]? Data)> entries, WriterOptions7z options)
    {
        using (var writer = Writer7z.Create(path, options))
        {
            foreach (var (name, filePath, data) in entries)
            {
                if (filePath != null)
                {
                    writer.Add(filePath, name);
                }
                else if (data != null)
                {
                    writer.Add(new WriterEntry7z { Name = name }, new MemoryStream(data));
                }
                else
                {
                    writer.Add(new WriterEntry7z { Name = name, IsDirectory = true });
                }
            }

            // Sin Finish explícito: Dispose debe vaciar la fila y cerrar el archivo.
        }

        return File.ReadAllBytes(path);
    }

    private static void AssertExtractsAll(string path, List<(string Name, string? Path, byte[]? Data)> entries, string? password = null)
    {
        using var archive = Archive7z.Open(path, password);
        var byName = archive.Entries.ToDictionary(e => e.Name.Replace('\\', '/'));
        foreach (var (name, _, data) in entries.Where(e => e.Data != null))
        {
            var output = new MemoryStream();
            archive.Extract(byName[name], output);
            Assert.True(data!.AsSpan().SequenceEqual(output.ToArray()), name);
        }
    }

    [Theory]
    [InlineData(CompressionMethod.Lzma2, 5)]
    [InlineData(CompressionMethod.Lzma2, 1)]
    [InlineData(CompressionMethod.Lzma, 5)]
    [InlineData(CompressionMethod.Ppmd, 5)]
    [InlineData(CompressionMethod.BZip2, 1)]
    [InlineData(CompressionMethod.BZip2, 9)]
    [InlineData(CompressionMethod.Deflate, 5)]
    [InlineData(CompressionMethod.Deflate, 9)]
    public void ParallelFiles_AreByteIdenticalToSingleThread(CompressionMethod method, int level)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"7z_paralelo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var entries = SampleEntries(dir);
            byte[] serial = Write(Path.Combine(dir, "serie.7z"), entries, new WriterOptions7z { Method = method, Level = level, Threads = 1 });
            byte[] parallel = Write(Path.Combine(dir, "paralelo.7z"), entries, new WriterOptions7z { Method = method, Level = level, Threads = 8 });

            Assert.Equal(serial.Length, parallel.Length);
            Assert.True(serial.AsSpan().SequenceEqual(parallel), "la salida con 8 hilos difiere de la monohilo");
            AssertExtractsAll(Path.Combine(dir, "paralelo.7z"), entries);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Envoltorio que calcula el SHA-256 de lo que se lee, como hace ForZip con la evidencia.</summary>
    private sealed class HashingReadStream(Stream inner) : Stream
    {
        private readonly System.Security.Cryptography.IncrementalHash _hash =
            System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);

        public byte[]? Sha256 { get; private set; }
        public int DisposeCount { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            _hash.AppendData(buffer[..n]);
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCount++;
                Sha256 ??= _hash.GetHashAndReset();
                inner.Dispose();
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void AddOwned_WrappedStreams_SameBytesAsAddByPath_AndEachReadOnceAndClosed()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"7z_owned_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var files = SampleEntries(dir).Where(e => e.Path != null).ToList();
            byte[] byPath = Write(Path.Combine(dir, "ruta.7z"), files, new WriterOptions7z { Threads = 8 });

            var wrappers = new List<HashingReadStream>();
            string ownedPath = Path.Combine(dir, "owned.7z");
            using (var writer = Writer7z.Create(ownedPath, new WriterOptions7z { Threads = 8 }))
            {
                foreach (var (name, filePath, _) in files)
                {
                    var wrapper = new HashingReadStream(new FileStream(filePath!, FileMode.Open, FileAccess.Read, FileShare.Read));
                    wrappers.Add(wrapper);
                    writer.AddOwned(WriterEntry7z.FromFile(filePath!, name), wrapper);
                }
            }

            Assert.True(byPath.AsSpan().SequenceEqual(File.ReadAllBytes(ownedPath)), "AddOwned produjo otro archivo que Add(ruta)");
            for (int i = 0; i < files.Count; i++)
            {
                Assert.Equal(1, wrappers[i].DisposeCount);
                Assert.Equal(System.Security.Cryptography.SHA256.HashData(files[i].Data!), wrappers[i].Sha256);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Un archivo que falla al leerse en otro hilo: el error llega al llamador (en un Add posterior o al
    /// cerrar) y no queda en disco un 7z con forma válida al que le falte ese archivo.
    /// </summary>
    [Theory]
    [InlineData(5)]   // sale en un Add posterior, al vaciar la fila
    [InlineData(-2)]  // el último con datos: sale recién al cerrar, dentro de Dispose
    public void ParallelFiles_ReadErrorInWorker_FailsAndLeavesNoArchive(int brokenIndex)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // el bloqueo de rangos de FileStream.Lock es obligatorio solo en Windows
        }

        string dir = Path.Combine(Path.GetTempPath(), $"7z_paralelo_falla_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var entries = SampleEntries(dir).Where(e => e.Path != null).ToList();
            string broken = entries[brokenIndex >= 0 ? brokenIndex : entries.Count + brokenIndex].Path!;
            string path = Path.Combine(dir, "paralelo.7z");

            // Otro proceso tiene bloqueado un rango del archivo: abrirlo funciona, leerlo falla.
            using var locker = new FileStream(broken, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            locker.Lock(0, 1);

            Assert.ThrowsAny<IOException>(() =>
            {
                using var writer = Writer7z.Create(path, new WriterOptions7z { Threads = 8 });
                foreach (var (name, filePath, _) in entries)
                {
                    writer.Add(filePath!, name);
                }
            });
            Assert.False(File.Exists(path), "quedó un 7z incompleto en disco");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Requires7zFact]
    public void ParallelFiles_WithEncryptedHeaders_Official7zVerifiesAll()
    {
        Official7zFinder.EnsureAvailable();
        string dir = Path.Combine(Path.GetTempPath(), $"7z_paralelo_aes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var entries = SampleEntries(dir);
            string path = Path.Combine(dir, "paralelo.7z");
            Write(path, entries, new WriterOptions7z { Threads = 8, Password = "Clave123", EncryptHeaders = true });

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = Official7zFinder.ExecutablePath!,
                Arguments = $"t \"{path}\" -pClave123",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(60000);
            Assert.True(proc.ExitCode == 0, output);
            Assert.Contains($"Files: {entries.Count(e => e.Data != null)}", output, StringComparison.Ordinal);

            AssertExtractsAll(path, entries, "Clave123");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
