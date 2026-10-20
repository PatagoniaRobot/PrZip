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

using System.Text;
using PrZip.Primitives.IO;
using Xunit;

namespace PrZip.Tests;

/// <summary>
/// Hallazgos de la revisión del escritor del 2026-10-13: cada test falla con el código anterior al arreglo.
/// </summary>
public class WriterReviewTests
{
    /// <summary>Destino que falla en la próxima escritura una sola vez, como un error transitorio de disco.</summary>
    private sealed class FlakyStream : MemoryStream
    {
        public bool FailNextWrite { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfArmed();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfArmed();
            base.Write(buffer);
        }

        private void ThrowIfArmed()
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("falla transitoria");
            }
        }
    }

    private static MemoryStream Text(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void FinishThatFails_LeavesWriterFaulted_AndDisposeDoesNotRetry()
    {
        var output = new FlakyStream();
        var writer = Writer7z.Create(output, new WriterOptions7z { Solid = true, LeaveStreamOpen = true });
        writer.Add(new WriterEntry7z { Name = "a.txt" }, Text("contenido A"));
        writer.Add(new WriterEntry7z { Name = "b.txt" }, Text("contenido B"));

        // La falla llega al cerrar el bloque sólido, que es lo primero que Finish escribe.
        output.FailNextWrite = true;
        Assert.Throws<IOException>(writer.Finish);

        // Antes, Dispose reintentaba Finish sin error y la firma quedaba sellada sobre un 7z sin datos.
        writer.Dispose();
        Assert.Throws<ObjectDisposedException>(writer.Finish);
        Assert.True(output.ToArray().AsSpan(0, 6).SequenceEqual(new byte[6]), "la firma 7z no debe quedar escrita");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", false)]
    [InlineData("", true)]
    public void PasswordMissingOrEmpty_WhenEncryptionWasAsked_IsRejected(string? password, bool encryptHeaders)
    {
        // Antes salía un 7z sin cifrar y con los nombres a la vista, sin ningún aviso.
        var options = new WriterOptions7z { Password = password, EncryptHeaders = encryptHeaders };
        Assert.Throws<ArgumentException>(() => Writer7z.Create(new MemoryStream(), options));

        string path = Path.Combine(Path.GetTempPath(), "writer_review_" + Guid.NewGuid() + ".7z");
        Assert.Throws<ArgumentException>(() => Writer7z.Create(path, options));
        Assert.False(File.Exists(path), "no se crea el archivo si las opciones no sirven");
    }

    [Fact]
    public void OptionsChangedAfterCreate_DoNotAffectTheArchive()
    {
        var options = new WriterOptions7z { Password = "clave", EncryptHeaders = true, LeaveStreamOpen = true };
        var output = new MemoryStream();
        using (var writer = Writer7z.Create(output, options))
        {
            options.Password = null;
            options.EncryptHeaders = false;
            writer.Add(new WriterEntry7z { Name = "nombre_reservado.txt" }, Text("dato"));
            writer.Finish();
        }

        Assert.DoesNotContain("nombre_reservado", Encoding.Unicode.GetString(output.ToArray()));
    }

    [Fact]
    public void CreateOnExistingFile_FailsWithoutTouchingIt_UnlessOverwrite()
    {
        string path = Path.Combine(Path.GetTempPath(), "writer_review_" + Guid.NewGuid() + ".7z");
        File.WriteAllText(path, "archivo ajeno");
        try
        {
            // Antes lo truncaba sin aviso, y si el empaquetado fallaba después lo borraba.
            Assert.Throws<IOException>(() => Writer7z.Create(path));
            Assert.Equal("archivo ajeno", File.ReadAllText(path));

            using (var writer = Writer7z.Create(path, null, overwrite: true))
            {
                writer.Add(new WriterEntry7z { Name = "a.txt" }, Text("A"));
                writer.Finish();
            }

            using var archive = Archive7z.Open(path);
            Assert.Equal("a.txt", Assert.Single(archive.Entries).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Archive7z Reopen(MemoryStream output) =>
        Archive7z.Open(new MemoryRandomAccessSource(output.ToArray()));

    [Theory]
    [InlineData("")]
    [InlineData("nul\0l.txt")]
    [InlineData("../fuera.txt")]
    [InlineData("a/../../fuera.txt")]
    [InlineData("./a.txt")]
    [InlineData("a//b.txt")]
    [InlineData("/raiz.txt")]
    [InlineData("\\raiz.txt")]
    [InlineData("C:/abs.txt")]
    [InlineData("C:")]
    [InlineData("termina/")]
    public void InvalidName_IsRejected_AndTheWriterKeepsWorking(string name)
    {
        var output = new MemoryStream();
        using (var writer = Writer7z.Create(output, new WriterOptions7z { LeaveStreamOpen = true }))
        {
            Assert.Throws<ArgumentException>(() => writer.Add(new WriterEntry7z { Name = name }, Text("x")));

            // Una entrada rechazada no escribió nada: el escritor sigue sirviendo.
            writer.Add(new WriterEntry7z { Name = "despues.txt" }, Text("sigue"));
            writer.Finish();
        }

        using var archive = Reopen(output);
        Assert.Equal("despues.txt", Assert.Single(archive.Entries).Name);
    }

    [Fact]
    public void NulInName_NoLongerShiftsTheOtherNames()
    {
        // Antes, el nulo cortaba la lista de nombres: "l.txt" pasaba a ser una carpeta y las entradas
        // siguientes quedaban con el nombre de otra.
        var output = new MemoryStream();
        using (var writer = Writer7z.Create(output, new WriterOptions7z { LeaveStreamOpen = true }))
        {
            writer.Add(new WriterEntry7z { Name = "primero.txt" }, Text("1"));
            Assert.Throws<ArgumentException>(() => writer.Add(new WriterEntry7z { Name = "nul\0l.txt" }, Text("2")));
            writer.Add(new WriterEntry7z { Name = "carpeta", IsDirectory = true });
            writer.Add(new WriterEntry7z { Name = "ultimo.txt" }, Text("3"));
            writer.Finish();
        }

        using var archive = Reopen(output);
        Assert.Equal(["primero.txt", "carpeta", "ultimo.txt"], archive.Entries.Select(e => e.Name));
        Assert.True(archive.Entries[1].IsDirectory);
    }

    [Fact]
    public void Names_AreNormalized_AndDuplicatesRejected()
    {
        var output = new MemoryStream();
        using (var writer = Writer7z.Create(output, new WriterOptions7z { LeaveStreamOpen = true }))
        {
            writer.Add(new WriterEntry7z { Name = "docs\\nota.txt" }, Text("n"));
            writer.Add(new WriterEntry7z { Name = "docs/", IsDirectory = true });
            writer.Add(new WriterEntry7z { Name = "x:y.txt" }, Text("dos puntos, válido fuera de Windows"));

            Assert.Throws<ArgumentException>(() => writer.Add(new WriterEntry7z { Name = "docs/nota.txt" }, Text("otra")));
            Assert.Throws<ArgumentException>(() => writer.Add(new WriterEntry7z { Name = "docs", IsDirectory = true }));
            writer.Finish();
        }

        using var archive = Reopen(output);
        Assert.Equal(["docs/nota.txt", "docs", "x:y.txt"], archive.Entries.Select(e => e.Name));
    }

    [Fact]
    public void DirectoryWithContent_IsRejected()
    {
        // Antes el contenido se descartaba sin aviso y la entrada quedaba como carpeta vacía.
        using var writer = Writer7z.Create(new MemoryStream());
        Assert.Throws<ArgumentException>(() =>
            writer.Add(new WriterEntry7z { Name = "carpeta", IsDirectory = true }, Text("se perdía")));
    }
}
