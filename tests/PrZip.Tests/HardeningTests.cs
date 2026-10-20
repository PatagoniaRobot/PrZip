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

using PrZip.Container.Headers;
using PrZip.Exceptions;
using PrZip.Primitives.Checksum;
using PrZip.Primitives.Numeric;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

/// <summary>
/// Defensa frente a archivos hostiles: nombres que escapan del destino o que Windows reinterpreta,
/// y cabeceras que declaran tamaños, cantidades o diccionarios desmedidos.
/// </summary>
[Collection(GlobalAllocationTests.Name)]
public class HardeningTests
{
    [Theory]
    [InlineData("carpeta/archivo.txt", "carpeta|archivo.txt", false)]
    [InlineData("/raiz/absoluta.txt", "raiz|absoluta.txt", true)]
    [InlineData("C:/Windows/win.ini", "C_|Windows|win.ini", true)]
    [InlineData("datos:flujo_oculto", "datos_flujo_oculto", true)]
    [InlineData("informe.txt:Zone.Identifier", "informe.txt_Zone.Identifier", true)]
    [InlineData("CON", "_CON", true)]
    [InlineData("dir/nul.txt", "dir|_nul.txt", true)]
    [InlineData("COM1 .log", "_COM1 .log", true)]
    [InlineData("console.txt", "console.txt", false)]
    [InlineData("termina en punto.", "termina en punto_", true)]
    [InlineData("a<b>c|d?e*f\"g", "a_b_c_d_e_f_g", true)]
    [InlineData("./a//b/./c", "a|b|c", true)]
    public void Sanitizer_WindowsRules(string name, string expected, bool expectChanged)
    {
        string result = EntryPathSanitizer.ToSafeRelativePath(name, windowsRules: true, out bool changed);
        Assert.Equal(expected.Replace('|', Path.DirectorySeparatorChar), result);
        Assert.Equal(expectChanged, changed);
    }

    [Theory]
    [InlineData("../fuera.txt")]
    [InlineData("a/../../fuera.txt")]
    [InlineData("a\\..\\..\\fuera.txt")]
    public void Sanitizer_Traversal_Throws(string name)
    {
        Assert.Throws<CorruptArchiveException>(() => EntryPathSanitizer.ToSafeRelativePath(name, windowsRules: true, out _));
        Assert.Throws<CorruptArchiveException>(() => EntryPathSanitizer.ToSafeRelativePath(name, windowsRules: false, out _));
    }

    [Fact]
    public void ParentReferences_ThrowByDefault_AndAreNeutralizedOnlyWhenAsked()
    {
        Assert.Throws<CorruptArchiveException>(() => EntryPathSanitizer.ToSafeRelativePath("../a/../b.txt", windowsRules: true, out _));

        string safe = EntryPathSanitizer.ToSafeRelativePath("../a/../b.txt", windowsRules: true, neutralizeParentReferences: true, out bool changed);
        Assert.True(changed);
        Assert.Equal(string.Join(Path.DirectorySeparatorChar, "__", "a", "__", "b.txt"), safe);

        string root = Path.Combine(Path.GetTempPath(), "raiz_neutral");
        string target = EntryPathSanitizer.ResolveTarget(root, "../../fuera.txt", isDirectory: false, flatten: false,
            EntryPathSanitizer.CreatePathSet(), neutralizeParentReferences: true, out bool renamed);
        Assert.True(renamed);
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractAll_AppliesTheStoredTimesAsUtc()
    {
        // Revisión de lectores 2026-10-13: la hora UTC del 7z se aplicaba como local (3 h corrida en Argentina).
        string dir = Path.Combine(Path.GetTempPath(), $"7z_fechas_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var written = new DateTimeOffset(2019, 3, 15, 10, 30, 0, TimeSpan.Zero);
            string archivePath = Path.Combine(dir, "fechas.7z");
            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z()))
            {
                writer.Add(new WriterEntry7z { Name = "acta.txt", LastWriteTime = written },
                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes("acta")));
                writer.Finish();
            }

            string outDir = Path.Combine(dir, "salida");
            using (var archive = Archive7z.Open(archivePath))
            {
                archive.ExtractAll(outDir);
            }

            string file = Path.Combine(outDir, "acta.txt");
            Assert.Equal(written.UtcDateTime, File.GetLastWriteTimeUtc(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Extract_OverAnExistingFile_DoesNotOverwriteNorDeleteIt()
    {
        // Antes se pisaba con FileMode.Create y, si la extracción fallaba, se borraba el archivo que ya estaba.
        string dir = Path.Combine(Path.GetTempPath(), $"7z_existente_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string archivePath = Path.Combine(dir, "uno.7z");
            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z()))
            {
                writer.Add(new WriterEntry7z { Name = "acta.txt" }, new MemoryStream(System.Text.Encoding.UTF8.GetBytes("del 7z")));
                writer.Finish();
            }

            string target = Path.Combine(dir, "acta.txt");
            File.WriteAllText(target, "ajeno");
            using (var archive = Archive7z.Open(archivePath))
            {
                Assert.Throws<IOException>(() => archive.Extract(archive.Entries[0], target));
                Assert.Equal("ajeno", File.ReadAllText(target));

                // Pedido explícitamente, sí se pisa.
                archive.Extract(archive.Entries[0], target, overwrite: true);
            }

            Assert.Equal("del 7z", File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExtractAll_HostileNames_StayInsideAndDoNotOverwriteEachOther()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"7z_nombres_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string archivePath = Path.Combine(dir, "nombres.7z");
            var entries = new (string Name, string Content)[]
            {
                ("x_y.txt", "primero"),
                ("x:y.txt", "segundo, choca con el primero al sanear"),
                ("zabsoluto.txt", "tercero"),
                ("NUL.txt", "cuarto"),
            };

            using (var writer = Writer7z.Create(archivePath, new WriterOptions7z { Method = CompressionMethod.Copy }))
            {
                foreach (var (name, content) in entries)
                {
                    writer.Add(new WriterEntry7z { Name = name }, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));
                }

                writer.Finish();
            }

            // El escritor rechaza las rutas absolutas: la de esta entrada se pone después, en la cabecera.
            HostileArchive.ReplaceName(archivePath, "zabsoluto.txt", "/absoluto.txt");

            string outDir = Path.Combine(dir, "salida");
            var renamed = new List<string>();
            using (var archive = Archive7z.Open(archivePath))
            {
                archive.ExtractAll(outDir, (entry, path) => renamed.Add($"{entry.Name} -> {Path.GetRelativePath(outDir, path)}"));
            }

            // Todo quedó dentro del destino y cada entrada en su propio archivo.
            string[] files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
            Assert.Equal(entries.Length, files.Length);
            Assert.All(files, f => Assert.StartsWith(outDir, f, StringComparison.OrdinalIgnoreCase));
            var contents = files.Select(File.ReadAllText).ToHashSet();
            Assert.All(entries, e => Assert.Contains(e.Content, contents));
            Assert.Equal("primero", File.ReadAllText(Path.Combine(outDir, "x_y.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "absoluto.txt")));

            if (OperatingSystem.IsWindows())
            {
                Assert.True(File.Exists(Path.Combine(outDir, "_NUL.txt")));
                Assert.Contains(renamed, r => r.StartsWith("x:y.txt", StringComparison.Ordinal));
                Assert.Contains(renamed, r => r.StartsWith("NUL.txt", StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Arma un .7z mínimo: firma (con sus CRC) seguida directamente de la cabecera dada.</summary>
    private static byte[] BuildArchive(byte[] header, long? declaredHeaderSize = null)
    {
        var sig = new SignatureHeader(
            majorVersion: 0, minorVersion: 4, startHeaderCrc: 0,
            nextHeaderOffset: 0,
            nextHeaderSize: declaredHeaderSize ?? header.Length,
            nextHeaderCrc: Crc32Algorithm.Compute(header),
            baseOffset: 0);
        byte[] file = new byte[SignatureHeader.HeaderSize + header.Length];
        sig.Write(file);
        header.CopyTo(file, SignatureHeader.HeaderSize);
        return file;
    }

    private static Exception OpenHostile(byte[] archive)
    {
        string path = Path.Combine(Path.GetTempPath(), $"7z_hostil_{Guid.NewGuid():N}.7z");
        File.WriteAllBytes(path, archive);
        try
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var ex = Record.Exception(() =>
            {
                using var a = Archive7z.Open(path);
            });
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 16L << 20, $"Rechazar la cabecera hostil costó {allocated >> 20} MB.");
            return ex!;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HostileHeader_HugeFileCount_RejectedWithoutAllocating()
    {
        // kHeader, kFilesInfo, numFiles = 2^31 - 1 sin propiedades, kEnd, kEnd
        byte[] count = new byte[9];
        int n = VarInt.Write(count, int.MaxValue);
        byte[] header = [0x01, 0x05, .. count.AsSpan(0, n), 0x00, 0x00];

        var ex = OpenHostile(BuildArchive(header));
        Assert.IsType<CorruptArchiveException>(ex);
    }

    [Fact]
    public void HostileHeader_DeclaredSizeBeyondFile_RejectedWithoutAllocating()
    {
        byte[] header = [0x01, 0x00];
        var ex = OpenHostile(BuildArchive(header, declaredHeaderSize: 1L << 40));
        Assert.IsAssignableFrom<PrZipException>(ex);
    }

    [Fact]
    public void HugeDeclaredDictionary_SmallFile_ExtractsWithoutReservingIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"7z_dic_{Guid.NewGuid():N}.7z");
        try
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("evidencia ", 500)));
            using (var writer = Writer7z.Create(path, new WriterOptions7z { Method = CompressionMethod.Lzma, DictionarySize = 1 << 16 }))
            {
                writer.Add(new WriterEntry7z { Name = "a.txt" }, new MemoryStream(data));
                writer.Finish();
            }

            // Reemplazar el diccionario de 64 KB de las propiedades LZMA (5D 00 00 01 00) por 3,75 GB.
            byte[] file = File.ReadAllBytes(path);
            var sig = SignatureHeader.Read(new PrZip.Primitives.IO.MemoryRandomAccessSource(file));
            int headerStart = SignatureHeader.HeaderSize + (int)sig.NextHeaderOffset;
            byte[] header = file.AsSpan(headerStart, (int)sig.NextHeaderSize).ToArray();
            int at = header.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x5D, 0x00, 0x00, 0x01, 0x00]);
            Assert.True(at >= 0, "No se encontraron las propiedades LZMA en la cabecera.");
            header[at + 4] = 0xF0;

            var patched = new SignatureHeader(0, 4, 0, sig.NextHeaderOffset, header.Length, Crc32Algorithm.Compute(header), 0);
            patched.Write(file);
            header.CopyTo(file, headerStart);
            File.WriteAllBytes(path, file);

            using var archive = Archive7z.Open(path);
            long before = GC.GetTotalAllocatedBytes(precise: true);
            using var mem = new MemoryStream();
            archive.Extract(archive.Entries.Single(), mem);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            Assert.Equal(data, mem.ToArray());
            Assert.True(allocated < 64L << 20, $"Se asignaron {allocated >> 20} MB para un archivo de {data.Length} bytes.");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
