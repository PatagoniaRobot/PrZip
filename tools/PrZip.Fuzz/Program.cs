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

using System.Globalization;
using System.Text;
using PrZip;
using PrZip.Exceptions;
using PrZip.Rar;

// Arnés de fuzzing por mutación de bits. Contrato que verifica: ante cualquier .7z alterado, el lector
// termina sin colgarse y, si falla, lo hace con una excepción propia (PrZipException). Cualquier otra
// excepción o un cuelgue es un hallazgo.
//
// Uso: PrZip.Fuzz [carpeta_con_7z] [semilla] [rondas_por_archivo]
//   Sin carpeta (o con ""), genera sus propias semillas con el escritor (LZMA2, LZMA, PPMd, BZip2, Deflate, Copy, sólido, AES).
//   La batería de interoperabilidad deja en <trabajo>/arch los .7z de 7-Zip para usarlos como semillas.

RarSupport.Register();

Console.OutputEncoding = Encoding.UTF8;
int seed = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 1;
int rounds = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 200;
string work = Path.Combine(Path.GetTempPath(), "pr7z-fuzz-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
Directory.CreateDirectory(work);
var findings = new Dictionary<string, int>();

try
{
    string seedDir = args.Length > 0 && args[0].Length > 0 ? args[0] : GenerateSeeds(Path.Combine(work, "semillas"));
    int failures = CheckWrongPassword(work);
    failures += Mutate(seedDir, seed, rounds);
    foreach (var kv in findings.OrderByDescending(k => k.Value))
        Console.WriteLine($"{kv.Value}x {kv.Key}");
    return failures == 0 ? 0 : 1;
}
finally
{
    Directory.Delete(work, recursive: true);
}

void Record(string key) => findings[key] = findings.GetValueOrDefault(key) + 1;

void RecordException(Exception ex)
{
    var frames = (ex.StackTrace ?? "").Split('\n').Take(2).Select(l => l.Trim());
    Record(ex.GetType().Name + " @ " + string.Join(" <- ", frames));
}

// Una contraseña incorrecta debe dar siempre InvalidPasswordOrCorruptDataException, nunca otra cosa.
int CheckWrongPassword(string dir)
{
    byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("contenido secreto del caso ", 40)));
    const int Cases = 100;
    int ok = 0;
    for (int i = 0; i < Cases; i++)
    {
        string path = Path.Combine(dir, $"pw_{i}.7z");
        var options = new WriterOptions7z
        {
            Password = "Correcta123",
            Level = i % 2 == 0 ? 5 : 1,
            Method = i % 3 == 0 ? CompressionMethod.Ppmd : CompressionMethod.Lzma2,
            EncryptHeaders = i % 4 == 0,
        };
        using (var w = Writer7z.Create(path, options, overwrite: true))
        {
            w.Add(new WriterEntry7z { Name = "a.txt" }, new MemoryStream(data));
            w.Finish();
        }
        try
        {
            using var a = Archive7z.Open(path, "Incorrecta999");
            a.Extract(a.Entries[0], new MemoryStream());
            Record("contraseña incorrecta aceptada");
        }
        catch (InvalidPasswordOrCorruptDataException) { ok++; }
#pragma warning disable CA1031 // Registrar cualquier otra excepción es justamente el propósito del arnés
        catch (Exception ex) { RecordException(ex); }
#pragma warning restore CA1031
        File.Delete(path);
    }
    Console.WriteLine($"Contraseña incorrecta -> InvalidPassword: {ok}/{Cases}");
    return Cases - ok;
}

int Mutate(string seedDir, int randomSeed, int roundsPerFile)
{
#pragma warning disable CA5394 // Aleatoriedad reproducible por semilla, no criptográfica
    var rnd = new Random(randomSeed);
    int total = 0, own = 0, clean = 0, bad = 0;
    string path = Path.Combine(work, "mutado.bin");
    // Cualquier archivo de la carpeta: 7z y los demás formatos que abre el lector común (tar, gz, xz, iso…).
    foreach (string file in Directory.GetFiles(seedDir).Where(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
    {
        byte[] original = File.ReadAllBytes(file);
        if (original.Length <= 32)
            continue;
        for (int round = 0; round < roundsPerFile; round++)
        {
            byte[] bytes = (byte[])original.Clone();
            int flips = 1 + rnd.Next(4);
            for (int f = 0; f < flips; f++)
                bytes[32 + rnd.Next(bytes.Length - 32)] ^= (byte)(1 << rnd.Next(8));
            File.WriteAllBytes(path, bytes);
            total++;
            var task = Task.Run(() =>
            {
                using var reader = PrZip.Formats.ArchiveReader.TryOpen(path, "Clave123");
                if (reader == null)
                {
                    return; // la mutación le borró la firma: deja de ser un archivo reconocible
                }

                while (reader.MoveNext())
                {
                    reader.CopyCurrentTo(Stream.Null);
                }
            });
            try
            {
                if (!task.Wait(TimeSpan.FromSeconds(20)))
                {
                    Record("CUELGUE " + Path.GetFileName(file));
                    bad++;
                    // El hilo colgado sigue con el archivo abierto: cada caso siguiente usa otro nombre.
                    path = Path.Combine(work, $"mutado_{total}.bin");
                    continue;
                }
                clean++;
            }
            catch (AggregateException ae) when (ae.InnerException is PrZipException) { own++; }
            catch (AggregateException ae)
            {
                RecordException(ae.InnerException!);
                bad++;
            }
        }
    }
#pragma warning restore CA5394
    Console.WriteLine($"Corrupción: {total} casos, {own} con excepción propia, {clean} sin error " +
                      $"(bit alterado sin efecto o en zona no verificada), {bad} hallazgos");
    return bad;
}

// Semillas propias con el escritor; contraseña "Clave123" para las cifradas.
static string GenerateSeeds(string dir)
{
    Directory.CreateDirectory(dir);
    var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"línea {i} del acta; ")));
    var binary = new byte[48 * 1024];
    new Random(7).NextBytes(binary.AsSpan(0, binary.Length / 2));
    var seeds = new (string Name, WriterOptions7z Options)[]
    {
        ("lzma2", new() { Method = CompressionMethod.Lzma2 }),
        ("lzma2_mx1", new() { Method = CompressionMethod.Lzma2, Level = 1 }),
        ("lzma", new() { Method = CompressionMethod.Lzma }),
        ("ppmd", new() { Method = CompressionMethod.Ppmd }),
        ("bzip2", new() { Method = CompressionMethod.BZip2 }),
        ("deflate", new() { Method = CompressionMethod.Deflate }),
        ("copy", new() { Level = 0 }),
        ("solido", new() { Solid = true }),
        ("aes", new() { Password = "Clave123" }),
        ("aes_mhe", new() { Password = "Clave123", EncryptHeaders = true }),
    };
    foreach (var (name, options) in seeds)
    {
        using var w = Writer7z.Create(Path.Combine(dir, name + ".7z"), options, overwrite: true);
        w.Add(new WriterEntry7z { Name = "acta.txt" }, new MemoryStream(text));
        w.Add(new WriterEntry7z { Name = "datos.bin" }, new MemoryStream(binary));
        w.Finish();
    }
    return dir;
}
