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

using System.Security.Cryptography;
using System.Text.Json;
using PrZip.Exceptions;
using PrZip.Formats;
using PrZip.Rar;

namespace PrZip.Tests.Formats.Rar;

/// <summary>
/// Lector RAR contra archivos creados por los Rar.exe oficiales (ver make_rar_fixtures.py): mismas entradas,
/// mismo contenido byte a byte, y las defensas (CRC de datos y de cabecera, truncado) con sabotaje.
/// </summary>
public class RarReaderTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Formats", "Rar");

    private static Dictionary<string, JsonElement> Manifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "manifest.json")));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var name in Manifest().Keys)
        {
            data.Add(name);
        }

        return data;
    }

    private static List<(string Name, string Kind, string? Sha)> ReadAll(string path, string? password = null)
    {
        using var reader = ArchiveReader.TryOpen(path, password);
        Assert.NotNull(reader);
        Assert.Equal("rar", reader.FormatName);
        var result = new List<(string, string, string?)>();
        while (reader.MoveNext())
        {
            string? sha = null;
            if (reader.Current.Kind == ArchiveEntryKind.File)
            {
                using var ms = new MemoryStream();
                reader.CopyCurrentTo(ms);
                Assert.Equal(reader.Current.Size, ms.Length);
                sha = Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
            }

            result.Add((reader.Current.Name, reader.Current.Kind.ToString(), sha));
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_EntradasYContenidoIdenticos(string fixture)
    {
        Assert.Equal("rar", ArchiveReader.DetectFormat(Path.Combine(Dir, fixture)));
        var expected = Manifest()[fixture].GetProperty("entries").EnumerateArray()
            .Select(e => (e.GetProperty("name").GetString()!, e.GetProperty("kind").GetString()!,
                e.TryGetProperty("sha256", out var s) ? s.GetString() : null))
            .OrderBy(e => e.Item1, StringComparer.Ordinal)
            .ToList();

        string? password = Manifest()[fixture].TryGetProperty("password", out var pw) ? pw.GetString() : null;
        var actual = ReadAll(Path.Combine(Dir, fixture), password).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);
    }

    public static TheoryData<string> EncryptedFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var (name, value) in Manifest())
        {
            if (value.TryGetProperty("password", out _))
            {
                data.Add(name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EncryptedFixtures))]
    public void Cifrado_ContrasenaIncorrectaOFaltante_SeInformaComoTal(string fixture)
    {
        string path = Path.Combine(Dir, fixture);
        var wrong = Assert.ThrowsAny<InvalidPasswordOrCorruptDataException>(() => ReadAll(path, "otra-cosa"));
        var missing = Assert.Throws<InvalidPasswordOrCorruptDataException>(() => ReadAll(path));
        Assert.Contains("hace falta la contraseña", missing.Message, StringComparison.Ordinal);

        // RAR5 guarda un valor de chequeo de 64 bits: ahí la contraseña incorrecta es segura, no "o datos dañados", y se
        // dice con su propio tipo para que quien lee no tenga que mirar el texto. Antes de RAR5 no se puede saber.
        if (fixture.Contains("_v5", StringComparison.Ordinal))
        {
            Assert.IsType<PasswordIncorrectException>(wrong);
            Assert.Contains("contraseña incorrecta", wrong.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("dañados", wrong.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.IsNotType<PasswordIncorrectException>(wrong);
        }
    }

    [Theory]
    [InlineData("m0_v4.rar", nameof(RarFormat.Rar15), 0L)]
    [InlineData("m0_v5.rar", nameof(RarFormat.Rar50), 0L)]
    [InlineData("m0_v5_sfx.exe", nameof(RarFormat.Rar50), 70002L)]
    public void Formato_YAutoextraible(string fixture, string format, long sfx)
    {
        // El formato va por nombre: RarFormat es interno y un test público no puede recibirlo.
        using var arc = RarArchive.Open(Path.Combine(Dir, fixture));
        Assert.Equal(format, arc.Format.ToString());
        Assert.Equal(sfx, arc.SfxSize);
        Assert.False(arc.IsSolid);
        Assert.False(arc.IsVolume);
        Assert.False(arc.HeadersEncrypted);
    }

    [Theory]
    [InlineData("m0_v4.rar", "CRC32")]
    [InlineData("m0_v5.rar", "CRC32")]
    [InlineData("m0_v5_blake2.rar", "BLAKE2sp")]
    public void Fechas_YSumas(string fixture, string checksum)
    {
        using var arc = RarArchive.Open(Path.Combine(Dir, fixture));
        int files = 0;
        while (arc.MoveNext())
        {
            var e = arc.Current;
            // Todas las entradas se crearon con la misma fecha de modificación (1.700.000.000 Unix).
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), e.LastWriteTime);
            if (!e.IsDirectory)
            {
                Assert.Equal(checksum, e.Checksum);
                files++;
            }
        }

        Assert.Equal(5, files);
    }

    [Fact]
    public void Blake2sp_VectorDeVacio_YPorPartes()
    {
        // Suma de datos vacíos (la que unrar usa en hash.cpp, vector conocido de BLAKE2sp).
        Assert.Equal("dd0e891776933f43c7d032b08a917e25741f8aa9a12c12e1cac8801500f2ca4f", Convert.ToHexStringLower(new Blake2sp().Final()));

        // Entregar los datos de a pedazos irregulares da lo mismo que de una vez.
        var data = new byte[100_003];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 131 + 7);
        }

        var whole = new Blake2sp();
        whole.Update(data);
        var parts = new Blake2sp();
        int pos = 0, step = 1;
        while (pos < data.Length)
        {
            int n = Math.Min(step, data.Length - pos);
            parts.Update(data.AsSpan(pos, n));
            pos += n;
            step = step * 3 % 1031 + 1;
        }

        Assert.Equal(whole.Final(), parts.Final());
    }

    /// <summary>Copia el fixture alterando un byte (o truncándolo) y devuelve la ruta.</summary>
    private static string Sabotage(string fixture, Func<byte[], byte[]> change)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Dir, fixture));
        var path = Path.Combine(Path.GetTempPath(), "rar_sab_" + Guid.NewGuid().ToString("N") + Path.GetExtension(fixture));
        File.WriteAllBytes(path, change(bytes));
        return path;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    [Theory]
    [InlineData("m0_v4.rar")]
    [InlineData("m0_v5.rar")]
    [InlineData("m0_v5_blake2.rar")]
    public void DatoAlterado_SeDetecta(string fixture)
    {
        // Un byte del texto almacenado: la cabecera queda intacta, la suma del contenido no.
        var marker = "renglón del acta"u8.ToArray();
        var path = Sabotage(fixture, b =>
        {
            int at = IndexOf(b, marker);
            Assert.True(at > 0);
            b[at + 3] ^= 0x20;
            return b;
        });
        try
        {
            var ex = Assert.Throws<CorruptArchiveException>(() => ReadAll(path));
            Assert.Contains("acta.txt", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("m0_v4.rar")]
    [InlineData("m0_v5.rar")]
    public void CabeceraAlterada_SeDetecta(string fixture)
    {
        // Un byte del nombre "acta.txt" dentro de su cabecera: falla el CRC de la cabecera.
        var marker = "acta.txt"u8.ToArray();
        var path = Sabotage(fixture, b =>
        {
            int at = IndexOf(b, marker);
            Assert.True(at > 0);
            b[at] ^= 0x01;
            return b;
        });
        try
        {
            Assert.Throws<CorruptArchiveException>(() => ReadAll(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("m0_v4.rar")]
    [InlineData("m0_v5.rar")]
    public void Truncado_SeDetecta(string fixture)
    {
        var path = Sabotage(fixture, b => b[..(b.Length * 2 / 3)]);
        try
        {
            Assert.ThrowsAny<CorruptArchiveException>(() => ReadAll(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("m5_v5_solid.rar")]
    [InlineData("m5_v5_solid_md128k_blake2.rar")]
    [InlineData("m5_v4_solid.rar")]
    [InlineData("m5_v4_solid_ppmd.rar")]
    public void Solido_ExtraerCadaUnoPorSeparado(string fixture)
    {
        // En un sólido cada entrada depende de las anteriores: extraer una sola obliga a decodificar las
        // previas aunque no se pidan. Se abre el archivo de nuevo para cada entrada.
        var expected = Manifest()[fixture].GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("kind").GetString() == "File")
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("sha256").GetString());

        foreach (var (name, sha) in expected)
        {
            using var arc = RarArchive.Open(Path.Combine(Dir, fixture));
            Assert.True(arc.IsSolid);
            bool found = false;
            while (arc.MoveNext())
            {
                if (arc.Current.Name == name)
                {
                    using var ms = new MemoryStream();
                    arc.ExtractCurrent(ms);
                    Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(ms.ToArray())));
                    found = true;
                    break;
                }
            }

            Assert.True(found, name);
        }
    }

    [Fact]
    public void NoSolido_ReservaLaVentanaPorElTamanoDelArchivo_NoPorElDiccionarioDeclarado()
    {
        // Revisión de lectores 2026-10-13: un RAR chico que declaraba 1 GB de diccionario hacía reservar 1 GB por
        // archivo. RAR achica solo el diccionario de los archivos chicos, así que el ataque se arma a mano: se
        // cambia en la cabecera del primer archivo de m3_v5.rar el diccionario declarado a 1 GB y se recalcula su
        // CRC. Los datos quedan intactos: se extrae igual y bien.
        byte[] rar = DeclareHugeDictionary(File.ReadAllBytes(Path.Combine(Dir, "m3_v5.rar")));
        using var arc = new RarArchive(new MemoryStream(rar));
        Assert.False(arc.IsSolid);
        Assert.True(arc.MoveNext());
        while (arc.Current.IsDirectory)
        {
            Assert.True(arc.MoveNext());
        }

        Assert.Equal(1UL << 30, arc.Current.DictionarySize);
        arc.ExtractCurrent(Stream.Null);

        ulong expected = Math.Max(System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)Math.Max(arc.Current.Size, 1)), 0x40000UL);
        Assert.True(arc.AllocatedWindow <= expected, $"ventana de {arc.AllocatedWindow:N0} bytes para un archivo de {arc.Current.Size:N0}");
    }

    /// <summary>Pone 1 GB (128 KB << 13) como diccionario del primer archivo de un RAR5 y recalcula el CRC de su cabecera.</summary>
    private static byte[] DeclareHugeDictionary(byte[] rar)
    {
        static ulong Vint(byte[] b, ref int pos)
        {
            ulong v = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte x = b[pos++];
                v |= (ulong)(x & 0x7F) << shift;
                if ((x & 0x80) == 0)
                {
                    return v;
                }
            }
        }

        int pos = 8; // firma RAR5
        while (true)
        {
            int crcPos = pos;
            int sizePos = pos + 4;
            int p = sizePos;
            ulong headerSize = Vint(rar, ref p);
            int bodyStart = p;
            int headerEnd = bodyStart + (int)headerSize;
            ulong type = Vint(rar, ref p);
            ulong flags = Vint(rar, ref p);
            if ((flags & 1) != 0) Vint(rar, ref p);
            ulong dataSize = (flags & 2) != 0 ? Vint(rar, ref p) : 0;
            if (type == 2)
            {
                ulong fileFlags = Vint(rar, ref p);
                Vint(rar, ref p); // tamaño
                Vint(rar, ref p); // atributos
                if ((fileFlags & 2) != 0) p += 4;
                if ((fileFlags & 4) != 0) p += 4;
                int compPos = p;
                ulong comp = Vint(rar, ref p);
                int compLen = p - compPos;
                if ((fileFlags & 1) == 0)
                {
                    comp = (comp & ~(0x1FUL << 10)) | (13UL << 10);
                    for (int i = 0; i < compLen; i++)
                    {
                        rar[compPos + i] = (byte)(((comp >> (7 * i)) & 0x7F) | (i < compLen - 1 ? 0x80UL : 0));
                    }

                    uint crc = PrZip.Primitives.Checksum.Crc32Algorithm.Compute(rar.AsSpan(sizePos, headerEnd - sizePos));
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(rar.AsSpan(crcPos), crc);
                    return rar;
                }
            }

            pos = headerEnd + (int)dataSize;
        }
    }

    [Theory]
    [InlineData("m3_v5.rar")]
    [InlineData("m5_v5_solid_md128k_blake2.rar")]
    [InlineData("m3_v4_ppmd.rar")]
    [InlineData("m3_v4_rgb.rar")]
    [InlineData("m5_v4_solid.rar")]
    public void Mutaciones_SoloExcepcionesDelMotor(string fixture)
    {
        // Bytes alterados al azar (determinista): el lector puede rechazar el archivo, pero solo con una
        // excepción propia (dañado, truncado, no soportado), nunca con un IndexOutOfRange u otra genérica.
        var original = File.ReadAllBytes(Path.Combine(Dir, fixture));
        var rnd = new Random(20261010);
        int detected = 0;
        for (int round = 0; round < 150; round++)
        {
            var bytes = (byte[])original.Clone();
            int changes = 1 + rnd.Next(4);
            for (int c = 0; c < changes; c++)
            {
                bytes[rnd.Next(bytes.Length)] ^= (byte)(1 + rnd.Next(255));
            }

            try
            {
                using var arc = new RarArchive(new MemoryStream(bytes));
                while (arc.MoveNext())
                {
                    arc.ExtractCurrent(Stream.Null);
                }
            }
            catch (PrZipException)
            {
                detected++;
            }
        }

        // Casi toda alteración tiene que notarse (CRC de cabecera o suma de los datos).
        Assert.True(detected > 140, $"solo se detectaron {detected} de 150 alteraciones");
    }

    [Fact]
    public void Diccionario_MayorAlTope_SeRechazaConClaridad()
    {
        var unpack = new Unpack();
        var ex = Assert.Throws<LimitExceededException>(() => unpack.Init(2UL << 30, false));
        Assert.Contains("2048 MB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NombreOem_PasaPorAnsiComoUnrar()
    {
        // Bytes UTF-8 de "テ" (E3 83 86) y "ス" (E3 82 B9) en un nombre RAR4 sin Unicode. unrar los toma como
        // OEM y los pasa a ANSI con el mapeo "best fit" de Windows: en una máquina OEM 850 / ANSI 1252, 0xB9
        // ('╣' en 850) termina como '¦', no como '╣'. Solo se verifica con esas páginas de códigos.
        var text = System.Globalization.CultureInfo.CurrentCulture.TextInfo;
        if (text.OEMCodePage != 850 || text.ANSICodePage != 1252)
        {
            return;
        }

        string name = RarStrings.OemToWide([0xE3, 0x83, 0x86, 0xE3, 0x82, 0xB9]);
        Assert.Equal("ÒâåÒé¦", name);
    }

    public static TheoryData<string> VolumeSets()
    {
        var data = new TheoryData<string>();
        foreach (var (name, value) in Manifest())
        {
            if (value.TryGetProperty("volumes", out _))
            {
                data.Add(name);
            }
        }

        return data;
    }

    private static (string[] Parts, string? Password) VolumeInfo(string first)
    {
        var m = Manifest()[first];
        var parts = m.GetProperty("volumes").EnumerateArray().Select(v => v.GetString()!).ToArray();
        return (parts, m.TryGetProperty("password", out var pw) ? pw.GetString() : null);
    }

    [Theory]
    [MemberData(nameof(VolumeSets))]
    public void Volumenes_DesdeCualquierParteSeLeeElJuegoCompleto(string first)
    {
        var (parts, password) = VolumeInfo(first);
        Assert.True(parts.Length >= 3, "el juego debería tener varias partes");
        var expected = ReadAll(Path.Combine(Dir, first), password);
        foreach (var part in parts)
        {
            Assert.Equal(expected, ReadAll(Path.Combine(Dir, part), password));
        }
    }

    /// <summary>Copia el juego de volúmenes a una carpeta temporal, sin las partes indicadas.</summary>
    private static string CopyVolumesWithout(string[] parts, params string[] skip)
    {
        string dir = Path.Combine(Path.GetTempPath(), "rar_vol_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var part in parts.Where(p => !skip.Contains(p)))
        {
            File.Copy(Path.Combine(Dir, part), Path.Combine(dir, part));
        }

        return dir;
    }

    [Theory]
    [MemberData(nameof(VolumeSets))]
    public void Volumenes_FaltaUnoDelMedio_SeInforma(string first)
    {
        var (parts, password) = VolumeInfo(first);
        var ordered = new[] { first }.Concat(parts.Where(p => p != first)).ToArray();
        string missing = ordered[1];
        string dir = CopyVolumesWithout(parts, missing);
        try
        {
            var ex = Assert.Throws<ArchiveTruncatedException>(() => ReadAll(Path.Combine(dir, first), password));
            Assert.Contains("falta el volumen '" + missing + "'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(VolumeSets))]
    public void Volumenes_FaltaElPrimero_NoSeOmiteEnSilencio(string first)
    {
        var (parts, password) = VolumeInfo(first);
        string second = parts.First(p => p != first);
        string dir = CopyVolumesWithout(parts, first);
        try
        {
            // Sin el primer volumen, el archivo partido del principio no se puede extraer: tiene que decirse.
            var ex = Assert.ThrowsAny<PrZipException>(() => ReadAll(Path.Combine(dir, second), password));
            Assert.True(ex is ArchiveTruncatedException or InvalidPasswordOrCorruptDataException, ex.ToString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
