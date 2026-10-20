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
using PrZip.Rar;

namespace PrZip.Tests.Formats.Rar;

/// <summary>
/// Los 100 archivos RAR de la batería de libarchive (válidos y hostiles, muchos son regresiones de OSS-Fuzz;
/// ver libarchive/LEEME-LICENCIA.txt). Contrato: cada uno se lee entero con el contenido esperado o falla
/// con una excepción propia del motor, nunca con otra ni colgándose. El resultado esperado está congelado
/// en libarchive/esperado.json, contrastado el 2026-10-10 contra UnRAR 7.01: idéntico salvo diferencias
/// deliberadas (enlaces informados y no creados, versiones viejas "nombre;n" extraídas en lugar de
/// salteadas, y archivos sin suma cuya salida no tiene el tamaño declarado, rechazados).
/// Para regenerarlo: RAR_CORPUS_REGENERAR=1 dotnet test --filter RarCorpusTests.
/// </summary>
public class RarCorpusTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Formats", "Rar", "libarchive");

    private static string ExpectedPath =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Formats", "Rar", "libarchive", "esperado.json");

    private static string Describe(string path)
    {
        var lines = new List<string>();
        try
        {
            // Contraseñas de la batería de libarchive: "12345678" en test_read_format_rar_encryption_*,
            // "password" en los demás cifrados.
            string file = Path.GetFileName(path);
            string? password = file.StartsWith("test_read_format_rar_encryption", StringComparison.Ordinal) ? "12345678"
                : file.Contains("encrypt", StringComparison.Ordinal) ? "password" : null;
            using var arc = RarArchive.Open(path, password);
            while (arc.MoveNext())
            {
                var e = arc.Current;
                if (e.IsDirectory || e.Redirection != RarRedirection.None)
                {
                    lines.Add($"{e.Name} [{(e.IsDirectory ? "carpeta" : e.Redirection.ToString())}]");
                    continue;
                }

                using var ms = new MemoryStream();
                arc.ExtractCurrent(ms);
                lines.Add($"{e.Name} {Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()))}");
            }

            lines.Add("OK");
        }
        catch (PrZipException ex)
        {
            lines.Add("RECHAZO " + ex.GetType().Name);
        }

        return string.Join("\n", lines);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public async Task Corpus_CadaArchivoSeLeeOSeRechazaComoSeEspera()
    {
        var files = Directory.GetFiles(Dir).Where(f => !f.EndsWith(".txt", StringComparison.Ordinal) && !f.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(100, files.Count);

        var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var task = Task.Run(() => Describe(f));
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(done == task, "se colgó con " + Path.GetFileName(f));
            actual[Path.GetFileName(f)] = await task;
        }

        if (Environment.GetEnvironmentVariable("RAR_CORPUS_REGENERAR") == "1")
        {
            File.WriteAllText(ExpectedPath, JsonSerializer.Serialize(actual, Indented));
            return;
        }

        var expected = JsonSerializer.Deserialize<SortedDictionary<string, string>>(File.ReadAllText(Path.Combine(Dir, "esperado.json")))!;
        foreach (var (name, result) in expected)
        {
            Assert.Equal(result, actual[name]);
        }
    }
}
