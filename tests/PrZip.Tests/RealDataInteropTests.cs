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
using PrZip.Primitives.Checksum;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

/// <summary>
/// Interoperabilidad con 7z.exe usando código máquina x64 real (coreclr.dll del runtime en curso).
/// Los datos sintéticos no ejercitan el filtro BCJ: un encoder y un decoder propios igualmente
/// equivocados se compensan entre sí, y el ida y vuelta interno pasa. Solo 7z.exe puede arbitrarlo.
/// </summary>
[Collection(GlobalAllocationTests.Name)]
public class RealDataInteropTests
{
    private const int SampleSize = 2 * 1024 * 1024;

    private static byte[] LoadRealX64Code()
    {
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string native = Path.Combine(runtimeDir, OperatingSystem.IsWindows() ? "coreclr.dll" : "libcoreclr.so");
        byte[] all = File.ReadAllBytes(native);
        return all.AsSpan(0, Math.Min(all.Length, SampleSize)).ToArray();
    }

    [Requires7zTheory]
    [InlineData("-mf=BCJ -m0=LZMA2")]
    [InlineData("-mf=BCJ -m0=Copy")]
    [InlineData("-mf=BCJ -m0=LZMA2 -pClave123 -mhe=on")]
    [InlineData("-mx9")] // 7-Zip elige BCJ2 (4 streams) para un .exe en -mx9
    [InlineData("-mf=BCJ2 -m0=LZMA")]
    public void Official7z_BcjX86OnRealCode_OurReaderExtractsIdentical(string switches)
    {
        Official7zFinder.EnsureAvailable();
        using var dir = new TempDir();
        byte[] code = LoadRealX64Code();
        string input = Path.Combine(dir.Path, "programa.exe");
        File.WriteAllBytes(input, code);
        string archivePath = Path.Combine(dir.Path, "oficial.7z");

        Run7z($"a \"{archivePath}\" \"{input}\" {switches}");

        string? password = switches.Contains("-p") ? "Clave123" : null;
        using var archive = Archive7z.Open(archivePath, password: password);
        using var mem = new MemoryStream();
        archive.Extract(archive.Entries.Single(e => e.Name == "programa.exe"), mem);
        Assert.Equal(SHA256.HashData(code), SHA256.HashData(mem.ToArray()));
    }

    /// <summary>
    /// Filtros de salto de otras arquitecturas. El código es x64, pero los patrones que buscan
    /// (BL, ADRP, JAL, AUIPC...) aparecen igual en cualquier dato, y 7-Zip los convierte con su código real.
    /// </summary>
    [Requires7zTheory]
    [InlineData("ARM")]
    [InlineData("ARM64")]
    [InlineData("ARM64:4096")]
    [InlineData("ARMT")]
    [InlineData("PPC")]
    [InlineData("SPARC")]
    [InlineData("IA64")]
    [InlineData("RISCV")]
    [InlineData("RISCV:8192")]
    [InlineData("Delta:4")]
    public void Official7z_BranchFilters_OurReaderExtractsIdentical(string filter)
    {
        Official7zFinder.EnsureAvailable();
        using var dir = new TempDir();
        byte[] code = LoadRealX64Code();
        string input = Path.Combine(dir.Path, "codigo.bin");
        File.WriteAllBytes(input, code);
        string archivePath = Path.Combine(dir.Path, "filtro.7z");

        Run7z($"a \"{archivePath}\" \"{input}\" -mf={filter}");

        using var archive = Archive7z.Open(archivePath);
        using var mem = new MemoryStream();
        archive.Extract(archive.Entries.Single(), mem);
        Assert.Equal(SHA256.HashData(code), SHA256.HashData(mem.ToArray()));
    }

    /// <summary>
    /// BZip2 con corridas (RLE de 4 + contador), código real y varios bloques de 900 KB.
    /// </summary>
    [Requires7zTheory]
    [InlineData("aaaa", "-mmt=1")]
    [InlineData("ceros", "-mmt=1")]
    [InlineData("codigo", "-mmt=1")]
    [InlineData("codigo", "-mmt=4 -mx9")]
    public void Official7z_BZip2_OurReaderExtractsIdentical(string kind, string switches)
    {
        Official7zFinder.EnsureAvailable();
        using var dir = new TempDir();
        byte[] data = kind switch
        {
            "aaaa" => "aaaa"u8.ToArray(),
            "ceros" => new byte[3 * 1024 * 1024],
            _ => LoadRealX64Code()
        };
        string input = Path.Combine(dir.Path, "datos.bin");
        File.WriteAllBytes(input, data);
        string archivePath = Path.Combine(dir.Path, "bzip2.7z");

        Run7z($"a \"{archivePath}\" \"{input}\" -m0=BZip2 {switches}");

        using var archive = Archive7z.Open(archivePath);
        using var mem = new MemoryStream();
        archive.Extract(archive.Entries.Single(), mem);
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(mem.ToArray()));
    }

    [Requires7zTheory]
    [InlineData(CompressionMethod.Lzma2, 5)]
    [InlineData(CompressionMethod.Copy, 0)]
    [InlineData(CompressionMethod.Lzma, 9)]
    public void OurWriter_ExeWithAutoBcj_Official7zExtractsIdentical(CompressionMethod method, int level)
    {
        Official7zFinder.EnsureAvailable();
        using var dir = new TempDir();
        byte[] code = LoadRealX64Code();
        string archivePath = Path.Combine(dir.Path, "nuestro.7z");
        string outDir = Path.Combine(dir.Path, "out");

        using (var writer = Writer7z.Create(archivePath, new WriterOptions7z { Method = method, Level = level, AutoDetectFilters = true }))
        {
            using var ms = new MemoryStream(code);
            writer.Add(new WriterEntry7z { Name = "programa.exe" }, ms);
            writer.Finish();
        }

        Run7z($"x -y \"{archivePath}\" -o\"{outDir}\"");
        Assert.Equal(SHA256.HashData(code), SHA256.HashData(File.ReadAllBytes(Path.Combine(outDir, "programa.exe"))));
    }

    [Requires7zFact]
    public void OurWriter_Official7zSeesPerFileCrc_AndRejectsTamperedData()
    {
        Official7zFinder.EnsureAvailable();
        using var dir = new TempDir();
        string archivePath = Path.Combine(dir.Path, "copia.7z");
        byte[] data = new byte[4096];
        new Random(7).NextBytes(data);

        using (var writer = Writer7z.Create(archivePath, new WriterOptions7z { Method = CompressionMethod.Copy }))
        {
            using var ms = new MemoryStream(data);
            writer.Add(new WriterEntry7z { Name = "datos.bin" }, ms);
            writer.Finish();
        }

        string listing = Run7z($"l -slt \"{archivePath}\"");
        Assert.Contains($"CRC = {Crc32Algorithm.Compute(data):X8}", listing);

        // Modo Copy: el byte 32 + 100 es el byte 100 del archivo empaquetado.
        byte[] bytes = File.ReadAllBytes(archivePath);
        bytes[32 + 100] ^= 0xFF;
        File.WriteAllBytes(archivePath, bytes);

        var (exitCode, output) = Run7zRaw($"t \"{archivePath}\"");
        Assert.NotEqual(0, exitCode);
        Assert.Contains("CRC Failed", output);
    }

    [Fact]
    public void OurWriter_EntryFailsMidway_NoArchiveLeftOnDisk()
    {
        using var dir = new TempDir();
        string archivePath = Path.Combine(dir.Path, "fallido.7z");

        using (var writer = Writer7z.Create(archivePath, new WriterOptions7z { Method = CompressionMethod.Copy }))
        {
            using var ok = new MemoryStream(new byte[1000]);
            writer.Add(new WriterEntry7z { Name = "bueno.bin" }, ok);

            Assert.Throws<IOException>(() => writer.Add(new WriterEntry7z { Name = "roto.bin" }, new FailingStream(5000)));
            Assert.Throws<InvalidOperationException>(() => writer.Add(new WriterEntry7z { Name = "otro.bin" }, new MemoryStream(new byte[10])));
            Assert.Throws<InvalidOperationException>(writer.Finish);
        }

        Assert.False(File.Exists(archivePath), "Un 7z incompleto no debe quedar en disco.");
    }

    [Fact]
    public void LzmaEncoder_LongRunsWithManyMatchLengths_DoesNotOverflow()
    {
        // Corridas de ceros de largo creciente: en una sola posición el BT4 encuentra
        // coincidencias de todos los largos entre 2 y 273 (antes desbordaba un buffer de 64).
        using var data = new MemoryStream();
        for (int run = 1; run <= 300; run++)
        {
            data.Write(new byte[run]);
            data.WriteByte((byte)(run | 1));
        }

        byte[] input = data.ToArray();
        using var dir = new TempDir();
        string archivePath = Path.Combine(dir.Path, "corridas.7z");
        using (var writer = Writer7z.Create(archivePath, new WriterOptions7z { Method = CompressionMethod.Lzma2, Level = 9 }))
        {
            using var ms = new MemoryStream(input);
            writer.Add(new WriterEntry7z { Name = "corridas.bin" }, ms);
            writer.Finish();
        }

        using var archive = Archive7z.Open(archivePath);
        using var outMem = new MemoryStream();
        archive.Extract(archive.Entries[0], outMem);
        Assert.Equal(input, outMem.ToArray());
    }

    [Requires7zFact]
    public void Streaming_LargeFolder_AllocatesFarLessThanItsSize()
    {
        Official7zFinder.EnsureAvailable();
        using var dir = new TempDir();
        const long size = 256L * 1024 * 1024;
        string input = Path.Combine(dir.Path, "grande.bin");
        using (var fs = File.Create(input))
        {
            fs.SetLength(size); // ceros: comprime a casi nada, pero se descomprime entero
        }

        string archivePath = Path.Combine(dir.Path, "grande.7z");
        Run7z($"a \"{archivePath}\" \"{input}\" -m0=LZMA2 -md=1m -mf=off");
        File.Delete(input);

        using var archive = Archive7z.Open(archivePath);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var counter = new CountingStream();
        archive.Extract(archive.Entries.Single(), counter);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(size, counter.Count);
        // Antes del streaming se materializaba el folder entero (y una copia): más de 512 MB.
        Assert.True(allocated < 64L * 1024 * 1024, $"Se asignaron {allocated / (1024 * 1024)} MB para extraer 256 MB.");
    }

    [Requires7zFact]
    public void SolidFolder_EntriesRequestedOutOfOrder_AllMatch()
    {
        Official7zFinder.EnsureAvailable();
        using var dir = new TempDir();
        string srcDir = Path.Combine(dir.Path, "src");
        Directory.CreateDirectory(srcDir);
        var expected = new Dictionary<string, byte[]>();
        var rnd = new Random(3);
        for (int i = 0; i < 6; i++)
        {
            byte[] data = new byte[50_000 + i * 7_000];
            rnd.NextBytes(data.AsSpan(0, data.Length / 2)); // mitad azar, mitad ceros
            string name = $"f{i}.bin";
            File.WriteAllBytes(Path.Combine(srcDir, name), data);
            expected[name] = data;
        }

        string archivePath = Path.Combine(dir.Path, "solido.7z");
        Run7z($"a \"{archivePath}\" \"{srcDir}{Path.DirectorySeparatorChar}*\" -ms=on -m0=LZMA2");

        using var archive = Archive7z.Open(archivePath);
        foreach (string name in new[] { "f4.bin", "f1.bin", "f5.bin", "f0.bin", "f3.bin", "f2.bin", "f2.bin" })
        {
            using var mem = new MemoryStream();
            archive.Extract(archive.Entries.Single(e => e.Name == name), mem);
            Assert.Equal(expected[name], mem.ToArray());
        }
    }

    [Fact]
    public void CorruptedData_ExtractToFile_ThrowsAndLeavesNoPartialFile()
    {
        using var dir = new TempDir();
        string archivePath = Path.Combine(dir.Path, "danado.7z");
        byte[] data = new byte[200_000];
        new Random(11).NextBytes(data);

        using (var writer = Writer7z.Create(archivePath, new WriterOptions7z { Method = CompressionMethod.Copy }))
        {
            using var ms = new MemoryStream(data);
            writer.Add(new WriterEntry7z { Name = "evidencia.bin" }, ms);
            writer.Finish();
        }

        byte[] bytes = File.ReadAllBytes(archivePath);
        bytes[32 + 150_000] ^= 0x01;
        File.WriteAllBytes(archivePath, bytes);

        string target = Path.Combine(dir.Path, "salida", "evidencia.bin");
        using var archive = Archive7z.Open(archivePath);
        var ex = Assert.Throws<CorruptArchiveException>(() => archive.Extract(archive.Entries.Single(), target));
        Assert.Contains("CRC", ex.Message);
        Assert.False(File.Exists(target), "Un archivo que no pasó la verificación no debe quedar con el nombre del original.");
    }

    private sealed class CountingStream : Stream
    {
        public long Count { get; private set; }

        public override void Write(byte[] buffer, int offset, int count) => Count += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Count += buffer.Length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static string Run7z(string arguments)
    {
        var (exitCode, output) = Run7zRaw(arguments);
        Assert.True(exitCode == 0, $"7z.exe falló con código {exitCode}.\n{output}");
        return output;
    }

    private static (int ExitCode, string Output) Run7zRaw(string arguments)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = Official7zFinder.ExecutablePath!,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        var stderr = proc.StandardError.ReadToEndAsync();
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60000);
        return (proc.ExitCode, output + stderr.Result);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"7z_real_{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    /// <summary>Stream que entrega ceros y falla al superar un límite, como un disco que se cae.</summary>
    private sealed class FailingStream(int failAfter) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= failAfter)
            {
                throw new IOException("Fallo de lectura simulado.");
            }

            int n = Math.Min(count, failAfter - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
