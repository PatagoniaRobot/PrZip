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
using System.Text.Json;
using PrZip.Exceptions;
using PrZip.Formats;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests.Formats;

/// <summary>
/// Fixtures generados una vez por clase con productores independientes del motor: Python (tarfile, gzip,
/// bz2, lzma) y bsdtar/libarchive. Un manifiesto dice qué debe contener cada uno.
/// </summary>
public sealed class FormatFixtures : IDisposable
{
    public string Directory { get; }

    public Dictionary<string, JsonElement> Manifest { get; }

    public FormatFixtures()
    {
        Directory = Path.Combine(Path.GetTempPath(), $"formatos_{Guid.NewGuid():N}");
        string script = Path.Combine(AppContext.BaseDirectory, "Formats", "make_fixtures.py");
        string bsdtar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        var psi = new ProcessStartInfo("python", ["-I", script, Directory])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (File.Exists(bsdtar))
        {
            psi.ArgumentList.Add(bsdtar);
        }

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Hace falta Python 3 en el PATH para generar los archivos de prueba.", ex);
        }

        string err = proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(120_000);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException("make_fixtures.py falló: " + err);
        }

        proc.Dispose();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, "manifest.json")));
        Manifest = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class FormatReaderTests(FormatFixtures fixtures) : IClassFixture<FormatFixtures>
{
    public static TheoryData<string> ManifestFiles()
    {
        // Los nombres se listan acá para que cada archivo sea un caso propio; los que el equipo no pueda
        // generar (sin bsdtar) se saltean dentro del test.
        return new TheoryData<string>
        {
            "py_gnu.tar", "py_gnu.tar.gz", "py_gnu.tar.bz2", "py_gnu.tar.xz",
            "py_pax.tar", "py_pax.tar.gz", "py_pax.tar.bz2", "py_pax.tar.xz",
            "suelto.gz", "suelto.txt.bz2",
            "check_none.bin.xz", "check_crc32.bin.xz", "check_crc64.bin.xz", "check_sha256.bin.xz",
            "filtro_x86.bin.xz", "filtro_delta.bin.xz", "concatenado.bin.xz",
            "bsd_ustar.tar", "bsd_pax.tar", "bsd_gnutar.tar", "bsd_v7.tar", "bsd.tgz", "bsd.tar.bz2", "bsd.tar.xz",
            "punto_barra.tar"
        };
    }

    private static string Normalize(string name) => name.Replace('\\', '/').TrimStart('.', '/').TrimEnd('/');

    [Theory]
    [MemberData(nameof(ManifestFiles))]
    public void Reader_ListsAndExtractsExactlyWhatTheProducerWrote(string file)
    {
        if (!fixtures.Manifest.TryGetValue(file, out var expected))
        {
            return; // sin bsdtar en este equipo
        }

        using var reader = ArchiveReader.TryOpen(Path.Combine(fixtures.Directory, file));
        Assert.NotNull(reader);
        Assert.Equal(expected.GetProperty("format").GetString(), reader!.FormatName);

        var actual = new Dictionary<string, (ArchiveEntryKind Kind, string? Sha, string? Link)>();
        while (reader.MoveNext())
        {
            var e = reader.Current;
            string? hash = null;
            if (e.Kind == ArchiveEntryKind.File)
            {
                using var ms = new MemoryStream();
                reader.CopyCurrentTo(ms);
                hash = Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
                if (e.Size >= 0)
                {
                    Assert.Equal(e.Size, ms.Length);
                }
            }

            // Con "exact", los nombres tienen que llegar ya normalizados por el lector.
            actual[expected.TryGetProperty("exact", out _) ? e.Name : Normalize(e.Name)] = (e.Kind, hash, e.LinkTarget);
        }

        // Progreso por bytes del archivo: al terminar, lo leído cubre el archivo (un TAR o ISO puede
        // terminar antes del relleno final).
        long length = new FileInfo(Path.Combine(fixtures.Directory, file)).Length;
        Assert.Equal(length, reader.ArchiveLength);
        Assert.True(reader.BytesRead >= length * 0.5, $"{file}: leídos {reader.BytesRead} de {length}");

        foreach (var entry in expected.GetProperty("entries").EnumerateArray())
        {
            string name = entry.GetProperty("name").GetString()!;
            Assert.True(actual.TryGetValue(name, out var got), $"{file}: falta '{name}'. Hay: {string.Join(", ", actual.Keys)}");
            Assert.Equal(entry.GetProperty("kind").GetString(), got.Kind.ToString());
            if (entry.TryGetProperty("sha256", out var sha))
            {
                Assert.Equal(sha.GetString(), got.Sha);
            }

            if (entry.TryGetProperty("link", out var link))
            {
                Assert.Equal(link.GetString(), got.Link);
            }
        }

        // Nada de más, salvo directorios que algunos productores agregan solos.
        var expectedNames = expected.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToHashSet();
        Assert.All(actual.Where(a => !expectedNames.Contains(a.Key)), a => Assert.Equal(ArchiveEntryKind.Directory, a.Value.Kind));
        if (expected.TryGetProperty("exact", out _))
        {
            Assert.Equal(expectedNames.Count, actual.Count);
        }
    }

    [Requires7zFact]
    public void Iso_ExtractsTheSameTreeAs7Zip()
    {
        Official7zFinder.EnsureAvailable();
        string iso = Path.Combine(fixtures.Directory, "bsd_joliet.iso");
        if (!File.Exists(iso))
        {
            return;
        }

        string byUs = Path.Combine(fixtures.Directory, "iso_nuestro");
        string by7z = Path.Combine(fixtures.Directory, "iso_7z");
        using (var reader = ArchiveReader.TryOpen(iso)!)
        {
            Assert.Equal("iso (Joliet)", reader.FormatName);
            while (reader.MoveNext())
            {
                string target = Path.Combine(byUs, reader.Current.Name.Replace('/', Path.DirectorySeparatorChar));
                if (reader.Current.Kind == ArchiveEntryKind.Directory)
                {
                    System.IO.Directory.CreateDirectory(target);
                    continue;
                }

                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var fs = File.Create(target);
                reader.CopyCurrentTo(fs);
            }
        }

        using (var proc = Process.Start(new ProcessStartInfo(Official7zFinder.ExecutablePath!, $"x \"{iso}\" -o\"{by7z}\" -y")
               { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!)
        {
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(60_000);
            Assert.Equal(0, proc.ExitCode);
        }

        static List<string> Tree(string root) => System.IO.Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p) + (File.Exists(p) ? ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) : "/"))
            .Order(StringComparer.Ordinal).ToList();

        Assert.Equal(Tree(by7z), Tree(byUs));
    }

    [Theory]
    [InlineData("py_pax.tar.gz")]
    [InlineData("py_pax.tar.bz2")]
    [InlineData("py_pax.tar.xz")]
    [InlineData("check_crc64.bin.xz")]
    [InlineData("check_sha256.bin.xz")]
    [InlineData("suelto.gz")]
    public void DamagedCompressedData_IsReportedAsCorrupt(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(fixtures.Directory, file));
        // Un bit en medio de los datos comprimidos (lejos de cabeceras e índices).
        data[data.Length / 2] ^= 0x10;
        string damaged = Path.Combine(fixtures.Directory, "danado_" + file);
        File.WriteAllBytes(damaged, data);

        Assert.ThrowsAny<PrZipException>(() =>
        {
            using var reader = ArchiveReader.TryOpen(damaged)!;
            while (reader.MoveNext())
            {
                reader.CopyCurrentTo(Stream.Null);
            }
        });
    }

    /// <summary>
    /// Solo el CRC32 del final de un GZip alterado: los datos comprimidos están intactos, así que únicamente
    /// la verificación puede notarlo. Vale para un .gz suelto y para un .tar.gz (donde el lector tiene que
    /// llegar hasta el final aunque el TAR ya haya terminado).
    /// </summary>
    [Theory]
    [InlineData("suelto.gz")]
    [InlineData("py_gnu.tar.gz")]
    public void GZip_TrailerCrcAltered_IsDetected(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(fixtures.Directory, file));
        data[^8] ^= 0x01; // primer byte del CRC32 del último miembro
        string damaged = Path.Combine(fixtures.Directory, "crc_" + file);
        File.WriteAllBytes(damaged, data);

        Assert.ThrowsAny<PrZipException>(() =>
        {
            using var reader = ArchiveReader.TryOpen(damaged)!;
            while (reader.MoveNext())
            {
                reader.CopyCurrentTo(Stream.Null);
            }
        });
    }

    /// <summary>
    /// Imágenes hostiles fabricadas a partir de la de bsdtar, tocando los registros de directorio de Joliet:
    /// un subdirectorio que apunta a la raíz (bucle) y un archivo cuya extensión sale de la imagen.
    /// </summary>
    [Theory]
    [InlineData("bucle")]
    [InlineData("fuera")]
    public void Iso_HostileImage_IsRejected(string attack)
    {
        string iso = Path.Combine(fixtures.Directory, "bsd_joliet.iso");
        if (!File.Exists(iso))
        {
            return;
        }

        byte[] img = File.ReadAllBytes(iso);
        int svd = 16 * 2048;
        while (!(img[svd] == 2 && img[svd + 88] == '%'))
        {
            svd += 2048;
        }

        int rootLba = BitConverter.ToInt32(img, svd + 156 + 2);
        int rootLen = BitConverter.ToInt32(img, svd + 156 + 10);
        string wanted = attack == "bucle" ? "datos" : "acta.txt";
        bool patched = false;
        for (int pos = rootLba * 2048; pos < rootLba * 2048 + rootLen && img[pos] != 0;)
        {
            int len = img[pos];
            string name = System.Text.Encoding.BigEndianUnicode.GetString(img, pos + 33, img[pos + 32] & ~1).Split(';')[0];
            if (name == wanted)
            {
                if (attack == "bucle")
                {
                    BitConverter.GetBytes(rootLba).CopyTo(img, pos + 2);
                }
                else
                {
                    BitConverter.GetBytes(img.Length / 2048 + 10).CopyTo(img, pos + 2);
                }

                patched = true;
            }

            pos += len;
        }

        Assert.True(patched, "no se encontró el registro a alterar");
        string hostile = Path.Combine(fixtures.Directory, $"hostil_{attack}.iso");
        File.WriteAllBytes(hostile, img);

        var ex = Assert.ThrowsAny<PrZipException>(() =>
        {
            using var reader = ArchiveReader.TryOpen(hostile)!;
            while (reader.MoveNext())
            {
                reader.CopyCurrentTo(Stream.Null);
            }
        });
        Assert.Contains(attack == "bucle" ? "bucle" : "fuera de la imagen", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bomba de extensiones superpuestas: 200 archivos que apuntan al mismo megabyte. La imagen pesa ~1 MB y
    /// "contiene" 200 MB; con muchos más registros, una imagen chica llenaba el disco. Se rechaza al abrir.
    /// </summary>
    [Fact]
    public void Iso_ManyEntriesOverTheSameData_IsRejectedAsABomb()
    {
        string bomb = Path.Combine(fixtures.Directory, "bomba_superpuesta.iso");
        File.WriteAllBytes(bomb, BuildOverlappingIso(files: 200, dataBytes: 1 << 20));

        var ex = Assert.Throws<LimitExceededException>(() => ArchiveReader.TryOpen(bomb)!.Dispose());
        Assert.Contains("mismos datos", ex.Message, StringComparison.Ordinal);

        // Con pocos duplicados (lo legítimo) se abre normalmente.
        string few = Path.Combine(fixtures.Directory, "pocos_duplicados.iso");
        File.WriteAllBytes(few, BuildOverlappingIso(files: 3, dataBytes: 1 << 20));
        using var reader = ArchiveReader.TryOpen(few)!;
        int count = 0;
        while (reader.MoveNext())
        {
            count++;
        }

        Assert.Equal(3, count);
    }

    /// <summary>ISO 9660 mínima: descriptor primario, terminador, raíz y N archivos sobre la misma extensión.</summary>
    private static byte[] BuildOverlappingIso(int files, int dataBytes)
    {
        const int Sector = 2048;
        var dir = new List<byte[]> { new byte[] { 0 }, new byte[] { 1 } };
        int dirSectors = 1;
        int used = 2 * 34;
        var records = new List<(int Sector, byte[] Record)>();
        foreach (var r in dir)
        {
            records.Add((0, r));
        }

        // La extensión de datos va después del directorio; se calcula al final.
        var fileRecords = Enumerable.Range(0, files).Select(i => System.Text.Encoding.ASCII.GetBytes($"F{i}.TXT;1")).ToList();
        foreach (var name in fileRecords)
        {
            int len = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
            if (used + len > Sector)
            {
                dirSectors++;
                used = 0;
            }

            records.Add((dirSectors - 1, name));
            used += len;
        }

        int dataLba = 18 + dirSectors;
        var img = new byte[(dataLba * Sector) + dataBytes];
        void Descriptor(int sector, byte type)
        {
            img[sector * Sector] = type;
            System.Text.Encoding.ASCII.GetBytes("CD001").CopyTo(img, (sector * Sector) + 1);
            img[(sector * Sector) + 6] = 1;
        }

        Descriptor(16, 1);
        DirRecord([0], 18, dirSectors * Sector, directory: true).CopyTo(img, (16 * Sector) + 156);
        Descriptor(17, 255);

        var offsets = new int[dirSectors];
        foreach (var (sector, data) in records)
        {
            byte[] record = data.Length == 1 && data[0] <= 1 && records.IndexOf((sector, data)) < 2
                ? DirRecord(data, 18, dirSectors * Sector, directory: true)
                : DirRecord(data, dataLba, dataBytes, directory: false);
            record.CopyTo(img, ((18 + sector) * Sector) + offsets[sector]);
            offsets[sector] += record.Length;
        }

        return img;
    }

    private static byte[] DirRecord(byte[] name, int lba, int length, bool directory)
    {
        int len = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        var r = new byte[len];
        r[0] = (byte)len;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(2), lba);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(6), lba);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(10), length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(14), length);
        r[25] = (byte)(directory ? 2 : 0);
        r[32] = (byte)name.Length;
        name.CopyTo(r, 33);
        return r;
    }

    /// <summary>
    /// Un .xz con el índice dañado falla al abrir; el archivo no puede quedar tomado (antes el origen no se
    /// cerraba y la evidencia quedaba bloqueada hasta cerrar el programa).
    /// </summary>
    [Fact]
    public void Xz_DamagedIndex_FailsAndReleasesTheFile()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(fixtures.Directory, "check_crc32.bin.xz"));
        data[^14] ^= 0xFF; // dentro del índice, justo antes del pie
        string damaged = Path.Combine(fixtures.Directory, "indice_danado.xz");
        File.WriteAllBytes(damaged, data);

        Assert.ThrowsAny<PrZipException>(() => ArchiveReader.TryOpen(damaged));
        File.Delete(damaged);
        Assert.False(File.Exists(damaged));
    }

    [Fact]
    public void Detection_UsesTheSignature_NotTheExtension()
    {
        string source = Path.Combine(fixtures.Directory, "py_gnu.tar.xz");
        string disguised = Path.Combine(fixtures.Directory, "parece_un.zip");
        File.Copy(source, disguised, overwrite: true);
        Assert.Equal("xz", ArchiveReader.DetectFormat(disguised));
        using var reader = ArchiveReader.TryOpen(disguised)!;
        Assert.Equal("tar.xz", reader.FormatName);

        string text = Path.Combine(fixtures.Directory, "texto.txt");
        File.WriteAllText(text, "esto no es un archivo comprimido");
        Assert.Null(ArchiveReader.DetectFormat(text));
        Assert.Null(ArchiveReader.TryOpen(text));
    }
}
