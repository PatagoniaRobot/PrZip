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
using PrZip.Formats;
using PrZip.Rar;

namespace PrZip.Tests.Formats.Rar;

/// <summary>
/// Comentarios y flujos NTFS de RAR contra archivos creados por los Rar.exe oficiales de cada época (ver
/// make_rar_fixtures_comentarios.py): el comentario del archivo, los comentarios por archivo de RAR 2.x y los
/// flujos alternativos se informan; un comentario dañado se informa como tal sin impedir la extracción.
/// </summary>
public class RarCommentsTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Formats", "Rar");

    private static Dictionary<string, JsonElement> Manifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "comentarios.json")));
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

    private sealed record Read(ArchiveComment? Comment, List<(string Name, string Kind, string? Value, ArchiveComment? Comment)> Entries);

    private static Read ReadAll(string path, string? password)
    {
        using var reader = ArchiveReader.TryOpen(path, password);
        Assert.NotNull(reader);
        var entries = new List<(string, string, string?, ArchiveComment?)>();
        while (reader.MoveNext())
        {
            var e = reader.Current;
            string? value = null;
            if (e.Kind == ArchiveEntryKind.File)
            {
                using var ms = new MemoryStream();
                reader.CopyCurrentTo(ms);
                value = Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
            }
            else if (e.Kind == ArchiveEntryKind.AlternateStream)
            {
                using var ms = new MemoryStream();
                reader.CopyCurrentTo(ms); // un flujo se informa, no se extrae
                Assert.Equal(0, ms.Length);
                value = e.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            entries.Add((e.Name, e.Kind.ToString(), value, e.Comment));
        }

        return new Read(reader.Comment, entries);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_ComentariosYFlujosSeInforman(string fixture)
    {
        var m = Manifest()[fixture];
        string? password = m.TryGetProperty("password", out var pw) ? pw.GetString() : null;
        var read = ReadAll(Path.Combine(Dir, fixture), password);

        Assert.NotNull(read.Comment);
        Assert.Null(read.Comment.Problem);
        Assert.Equal(m.GetProperty("comment").GetString(), read.Comment.Text);

        var expected = m.GetProperty("entries").EnumerateArray()
            .Select(e => (e.GetProperty("name").GetString()!, e.GetProperty("kind").GetString()!,
                e.TryGetProperty("sha256", out var s) ? s.GetString() : e.TryGetProperty("size", out var z) ? z.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : null))
            .OrderBy(e => e.Item1, StringComparer.Ordinal)
            .ToList();
        var actual = read.Entries.Select(e => (e.Name, e.Kind, e.Value)).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);

        var fileComments = m.GetProperty("fileComments").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        foreach (var e in read.Entries)
        {
            if (fileComments.TryGetValue(e.Name, out var text))
            {
                Assert.Equal(new ArchiveComment(text, null), e.Comment);
            }
            else
            {
                Assert.Null(e.Comment);
            }
        }
    }

    [Fact]
    public void CabecerasCifradas_SinContrasena_ElComentarioNoSeLee()
    {
        // RAR 3.x: el archivo abre y el comentario (cifrado como todo lo demás) no aparece.
        using (var reader = ArchiveReader.TryOpen(Path.Combine(Dir, "cmt_v4_hpass.rar")))
        {
            Assert.NotNull(reader);
            Assert.Null(reader.Comment);
        }

        // RAR5: sin contraseña no se puede ni abrir (ya era así); el pedido de contraseña es explícito.
        var ex = Assert.Throws<PrZip.Exceptions.InvalidPasswordOrCorruptDataException>(
            () => ArchiveReader.TryOpen(Path.Combine(Dir, "cmt_v5_hpass.rar"))?.Dispose());
        Assert.Contains("hace falta la contraseña", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("m3_v5.rar")]
    [InlineData("m3_v4.rar")]
    [InlineData("m3_v20.rar")]
    [InlineData("m3_v15.rar")]
    public void SinComentario_EsNull(string fixture)
    {
        using var reader = ArchiveReader.TryOpen(Path.Combine(Dir, fixture));
        Assert.NotNull(reader);
        Assert.Null(reader.Comment);
    }

    [Theory]
    [InlineData("cmt_v5.rar")]
    [InlineData("cmt_v4.rar")]
    [InlineData("cmt_v20.rar")]
    public void Flujo_NoSeExtrae(string fixture)
    {
        using var arc = RarArchive.Open(Path.Combine(Dir, fixture));
        bool seen = false;
        while (arc.MoveNext())
        {
            if (arc.Current.IsAlternateStream)
            {
                seen = true;
                Assert.Throws<NotSupportedException>(() => arc.ExtractCurrent(Stream.Null));
            }
        }

        Assert.True(seen);
    }

    /// <summary>Copia el fixture alterando un byte y devuelve la ruta.</summary>
    private static string Sabotage(string fixture, Action<byte[]> change)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Dir, fixture));
        change(bytes);
        var path = Path.Combine(Path.GetTempPath(), "rar_cmt_" + Guid.NewGuid().ToString("N") + ".rar");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        int at = haystack.AsSpan().IndexOf(needle);
        Assert.True(at > 0);
        return at;
    }

    public static TheoryData<string, int> DamagedComments() => new()
    {
        // RAR5: comentario almacenado (cabecera "CMT"); se toca el texto.
        { "cmt_v5.rar", -1 },
        // RAR4: comentario comprimido en la cabecera "CMT" (35 bytes): se toca un byte de sus datos.
        { "cmt_v4_largo.rar", -2 },
        // RAR 2.x y 1.5: comentario comprimido dentro de la cabecera principal; sus datos empiezan en
        // 7 (marca) + 13 (cabecera principal) + 13 (cabecera del comentario) = 33.
        { "cmt_v20.rar", 40 },
        { "cmt_v15_largo.rar", 40 },
    };

    [Theory]
    [MemberData(nameof(DamagedComments))]
    public void ComentarioDanado_SeInformaYLoDemasSeExtrae(string fixture, int offset)
    {
        var m = Manifest()[fixture];
        var path = Sabotage(fixture, b =>
        {
            int at = offset switch
            {
                -1 => IndexOf(b, "Causa 1234"u8) + 2,
                -2 => IndexOf(b, "CMT"u8) + 3 + 5,
                _ => offset
            };
            b[at] ^= 0x55;
        });
        try
        {
            var read = ReadAll(path, null);
            Assert.NotNull(read.Comment);
            Assert.Null(read.Comment.Text);
            Assert.NotNull(read.Comment.Problem);

            // El resto del archivo no depende del comentario.
            int files = m.GetProperty("entries").EnumerateArray().Count(e => e.GetProperty("kind").GetString() == "File");
            Assert.Equal(files, read.Entries.Count(e => e.Kind == "File" && e.Value != null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ComentarioDeArchivoDanado_SeInformaEnEsaEntrada()
    {
        // El comentario de archivo de RAR 2.x queda fuera del CRC de la cabecera: dañarlo no rompe la entrada.
        var path = Sabotage("cmt_v20.rar", b => b[IndexOf(b, "Comentario del archivo"u8) + 3] ^= 0x55);
        try
        {
            var read = ReadAll(path, null);
            var acta = Assert.Single(read.Entries, e => e.Name == "acta.txt");
            Assert.NotNull(acta.Value);
            Assert.NotNull(acta.Comment);
            Assert.Null(acta.Comment.Text);
            Assert.NotNull(acta.Comment.Problem);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
