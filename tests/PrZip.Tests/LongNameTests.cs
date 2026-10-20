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
using Xunit;

namespace PrZip.Tests;

/// <summary>
/// Nombres más largos de lo que admite el sistema de archivos (255 caracteres por componente en NTFS). Antes
/// la extracción entera se cortaba en ese archivo y se borraba lo ya extraído: pasaba con reportes de Autopsy
/// con tags. Ahora el nombre se acorta, conserva la extensión y lleva una huella del original.
/// </summary>
public class LongNameTests
{
    private static string Long(char c, int length, string ext = ".txt") => new string(c, length) + ext;

    [Fact]
    public void LongComponent_IsShortened_KeepingTheExtension()
    {
        string relative = EntryPathSanitizer.ToSafeRelativePath("tags/" + Long('x', 300), windowsRules: true, out bool changed);

        Assert.True(changed);
        string name = relative.Split(Path.DirectorySeparatorChar)[^1];
        Assert.True(name.Length <= 240, $"largo {name.Length}");
        Assert.EndsWith(".txt", name, StringComparison.Ordinal);
        Assert.Matches(@"~[0-9a-f]{8}\.txt$", name);
        Assert.StartsWith("tags", relative, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoLongNamesThatStartTheSame_StayDifferent()
    {
        string a = EntryPathSanitizer.ToSafeRelativePath(new string('t', 300) + "_victima.html", true, out _);
        string b = EntryPathSanitizer.ToSafeRelativePath(new string('t', 300) + "_testigo.html", true, out _);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ShortNames_AreUntouched()
    {
        string name = Long('y', 251); // 255 justos
        Assert.Equal(name, EntryPathSanitizer.ToSafeRelativePath(name, true, out bool changed));
        Assert.False(changed);
    }

    [Fact]
    public void UnixRules_CountBytes_AndSurrogatesAreNotSplit()
    {
        // 200 "ñ" son 400 bytes en UTF-8: no entra en ext4 aunque sean 200 caracteres.
        string enie = EntryPathSanitizer.ToSafeRelativePath(Long('ñ', 200), windowsRules: false, out bool changed);
        Assert.True(changed);
        Assert.True(Encoding.UTF8.GetByteCount(enie) <= 240);

        string emoji = string.Concat(Enumerable.Repeat("📁", 200)) + ".txt";
        string cut = EntryPathSanitizer.ToSafeRelativePath(emoji, windowsRules: true, out _);
        Assert.True(cut.Length <= 240);
        Assert.Equal(cut, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(cut))); // sin mitades de par sustituto
    }

    [Fact]
    public void MakeUnique_OnANameNearTheLimit_StaysWithinIt()
    {
        var used = EntryPathSanitizer.CreatePathSet();
        string path = Path.Combine(Path.GetTempPath(), Long('z', 250));
        EntryPathSanitizer.MakeUnique(path, used);
        string second = EntryPathSanitizer.MakeUnique(path, used);
        Assert.NotEqual(path, second);
        Assert.True(Path.GetFileName(second).Length <= 255);
    }

    [Fact]
    public void ExtractAll_WithALongName_ExtractsEverything()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"przip_largo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string archive = Path.Combine(dir, "reporte.7z");
            string longName = "Reports/" + new string('k', 300) + ".html";
            using (var writer = Writer7z.Create(archive))
            {
                foreach (string name in new[] { "antes.txt", longName, "despues.txt" })
                {
                    writer.Add(new WriterEntry7z { Name = name }, new MemoryStream(Encoding.UTF8.GetBytes("contenido de " + name[..5])));
                }

                writer.Finish();
            }

            string output = Path.Combine(dir, "salida");
            var renamed = new List<string>();
            using (var reader = Archive7z.Open(archive))
            {
                reader.ExtractAll(output, (entry, path) => renamed.Add(entry.Name));
            }

            string[] files = Directory.GetFiles(output, "*", SearchOption.AllDirectories);
            Assert.Equal(3, files.Length);
            Assert.Equal(new[] { longName }, renamed);
            Assert.Contains(files, f => f.EndsWith(".html", StringComparison.Ordinal) && File.ReadAllText(f) == "contenido de Repor");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
