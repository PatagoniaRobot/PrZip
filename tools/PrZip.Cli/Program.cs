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
using System.Globalization;
using PrZip.Exceptions;

namespace PrZip.Cli;

public static class Program
{
    private const string Version = "1.0.0";

    public static int Main(string[] args)
    {
        PrZip.Rar.RarSupport.Register();
        PrintBanner();

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            return options.Command.ToLowerInvariant() switch
            {
                "a" => ExecuteAdd(options),
                "x" or "e" => ExecuteExtract(options),
                "l" => ExecuteList(options),
                "t" => ExecuteTest(options),
                _ => PrintUnknownCommand(options.Command)
            };
        }
        catch (InvalidPasswordOrCorruptDataException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.ResetColor();
            return 2;
        }
        catch (ArchiveTruncatedException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"ERROR: Fin de archivo inesperado / archivo truncado: {ex.Message}");
            Console.ResetColor();
            return 2;
        }
        catch (CorruptArchiveException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"ERROR: Archivo dañado o corrupto: {ex.Message}");
            Console.ResetColor();
            return 2;
        }
        catch (FileNotFoundException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"ERROR: Archivo no encontrado: {ex.FileName}");
            Console.ResetColor();
            return 2;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"ERROR FATAL: {ex.Message}");
            if (Environment.GetEnvironmentVariable("PRZIP_DEBUG") is not null)
            {
                Console.Error.WriteLine(ex.ToString());
            }
            Console.ResetColor();
            return 2;
        }
    }

    private static void PrintBanner()
    {
        // Sin el nombre de 7-Zip ni "clean-room": el lector RAR es un port declarado de unrar.
        Console.WriteLine($"Patagonia Robot Archive Engine - CLI {Version} (.NET 10, x64)");
        Console.WriteLine("Copyright (c) Claudio Andino / Patagonia Robot. Proyecto independiente, sin relación con 7-Zip ni con Igor Pavlov.\n");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Uso: PrZip.Cli <comando> [<switches>...] <archivo_7z> [<ficheros_a_añadir>...]");
        Console.WriteLine();
        Console.WriteLine("<Comandos>");
        Console.WriteLine("  a : Añadir / crear archivo 7z");
        Console.WriteLine("  x : Extraer ficheros con rutas completas");
        Console.WriteLine("  e : Extraer ficheros excluyendo rutas de directorio");
        Console.WriteLine("  l : Listar contenido del archivo");
        Console.WriteLine("  t : Probar integridad del archivo");
        Console.WriteLine();
        Console.WriteLine("<Switches>");
        Console.WriteLine("  -o{dir}        : Directorio de destino para extracción");
        Console.WriteLine("  -p{password}   : Contraseña para cifrado o descifrado AES-256");
        Console.WriteLine("  -mhe[=on|=off] : Cifrar encabezados de archivo (-mhe o -mhe=on)");
        Console.WriteLine("  -ms[=on|=off]  : Compresión sólida (por defecto off: un daño afecta solo a su archivo)");
        Console.WriteLine("  -mx[=0..9]     : Nivel de compresión (0 = Copy, 5 = Normal, 9 = Ultra)");
        Console.WriteLine("  -mmt[=N|on|off]: Hilos de compresión (1 = monohilo, N = número de hilos, on = auto)");
        Console.WriteLine("  -m0={method}   : Método de compresión (copy, lzma, lzma2, ppmd, bzip2, deflate)");
        Console.WriteLine("  -y             : Reemplazar lo que ya exista (archivos al extraer, el 7z al crear)");
        Console.WriteLine();
    }

    private static int PrintUnknownCommand(string command)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"Comando desconocido: '{command}'");
        Console.ResetColor();
        PrintUsage();
        return 1;
    }

    private static int ExecuteAdd(CliOptions opts)
    {
        if (string.IsNullOrEmpty(opts.ArchivePath))
        {
            Console.Error.WriteLine("ERROR: Debe especificar el nombre del archivo 7z de salida.");
            return 1;
        }

        if (opts.Targets.Count == 0)
        {
            Console.Error.WriteLine("ERROR: Debe especificar al menos un archivo o carpeta para añadir.");
            return 1;
        }

        string fullArchivePath = Path.GetFullPath(opts.ArchivePath);
        if (File.Exists(fullArchivePath) && !opts.Overwrite)
        {
            Console.Error.WriteLine($"ERROR: ya existe '{fullArchivePath}'. Use -y para reemplazarlo.");
            return 1;
        }

        string? archiveDir = Path.GetDirectoryName(fullArchivePath);
        if (!string.IsNullOrEmpty(archiveDir))
        {
            Directory.CreateDirectory(archiveDir);
        }

        var writerOptions = new WriterOptions7z
        {
            Method = opts.Method,
            Level = opts.Level,
            Password = opts.Password,
            EncryptHeaders = opts.EncryptHeaders,
            Threads = opts.Threads,
            AutoDetectFilters = true,
            Solid = opts.Solid
        };

        Console.WriteLine($"Creando archivo: {fullArchivePath}");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();
        int filesCount = 0;
        int dirsCount = 0;
        long totalUnpackSize = 0;

        using (var writer = Writer7z.Create(fullArchivePath, writerOptions, opts.Overwrite))
        {
            foreach (var target in opts.Targets)
            {
                if (File.Exists(target))
                {
                    string fileName = Path.GetFileName(target);
                    Console.WriteLine($"+ {fileName}");
                    var fi = new FileInfo(target);
                    totalUnpackSize += fi.Length;
                    filesCount++;
                    writer.Add(target, fileName);
                }
                else if (Directory.Exists(target))
                {
                    string baseDir = Path.GetFullPath(target);
                    string rootDirName = Path.GetFileName(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                    // Añadir carpeta raíz
                    writer.AddDirectory(baseDir, rootDirName);
                    dirsCount++;

                    // Añadir subcarpetas
                    foreach (var dir in Directory.EnumerateDirectories(baseDir, "*", SearchOption.AllDirectories))
                    {
                        string relPath = Path.Combine(rootDirName, Path.GetRelativePath(baseDir, dir)).Replace('\\', '/');
                        writer.AddDirectory(dir, relPath);
                        dirsCount++;
                    }

                    // Añadir archivos dentro de la carpeta. En modo sólido los ejecutables van al final:
                    // cada .exe/.dll lleva su propio folder con BCJ y cerraría el bloque sólido en curso.
                    var files = Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories).ToList();
                    if (opts.Solid)
                    {
                        files = [.. files.OrderBy(f => IsExecutable(f) ? 1 : 0)];
                    }

                    foreach (var file in files)
                    {
                        string relPath = Path.Combine(rootDirName, Path.GetRelativePath(baseDir, file)).Replace('\\', '/');
                        Console.WriteLine($"+ {relPath}");
                        var fi = new FileInfo(file);
                        totalUnpackSize += fi.Length;
                        filesCount++;
                        writer.Add(file, relPath);
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"ADVERTENCIA: No se encontró el archivo o carpeta '{target}'. Omitiendo.");
                    Console.ResetColor();
                }
            }

            writer.Finish();
        }

        sw.Stop();
        var archiveInfo = new FileInfo(fullArchivePath);

        Console.WriteLine();
        Console.WriteLine("Todo correcto");
        Console.WriteLine($"Carpetas: {dirsCount}");
        Console.WriteLine($"Archivos: {filesCount}");
        Console.WriteLine($"Tamaño original:     {totalUnpackSize:N0} bytes");
        Console.WriteLine($"Tamaño empaquetado:  {archiveInfo.Length:N0} bytes");
        if (totalUnpackSize > 0)
        {
            double ratio = (double)archiveInfo.Length / totalUnpackSize * 100.0;
            Console.WriteLine($"Ratio de compresión: {ratio:F1}%");
        }
        Console.WriteLine($"Tiempo transcurrido: {sw.ElapsedMilliseconds / 1000.0:F2} s");

        return 0;
    }

    private static bool IsExecutable(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static int ExecuteExtract(CliOptions opts)
    {
        if (string.IsNullOrEmpty(opts.ArchivePath) || !File.Exists(opts.ArchivePath))
        {
            Console.Error.WriteLine($"ERROR: Archivo 7z no encontrado: '{opts.ArchivePath}'");
            return 2;
        }

        string fullArchivePath = Path.GetFullPath(opts.ArchivePath);
        string outputDir = string.IsNullOrEmpty(opts.OutputDir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(opts.OutputDir);
        Directory.CreateDirectory(outputDir);

        Console.WriteLine($"Extrayendo archivo: {fullArchivePath}");
        Console.WriteLine($"Hacia directorio:   {outputDir}");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();
        int filesCount = 0;
        int dirsCount = 0;
        long totalBytes = 0;

        bool flattenPaths = opts.Command.Equals("e", StringComparison.OrdinalIgnoreCase);
        string? format = PrZip.Formats.ArchiveReader.DetectFormat(fullArchivePath);
        if (format != null && format != "7z")
        {
            return ExtractOtherFormat(fullArchivePath, outputDir, flattenPaths, sw, opts.Password, opts.Overwrite);
        }

        using var archive = Archive7z.Open(fullArchivePath, opts.Password);

        var usedPaths = EntryPathSanitizer.CreatePathSet();
        int renamedCount = 0;

        foreach (var entry in archive.Entries)
        {
            if (flattenPaths && entry.IsDirectory)
            {
                continue;
            }

            string destPath = EntryPathSanitizer.ResolveTarget(outputDir, entry.Name, entry.IsDirectory, flattenPaths, usedPaths, out bool renamed);
            if (renamed)
            {
                renamedCount++;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  RENOMBRADO: '{entry.Name}' -> '{Path.GetRelativePath(outputDir, destPath)}'");
                Console.ResetColor();
            }

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destPath);
                dirsCount++;
                if (entry.LastWriteTime.HasValue)
                {
                    try { Directory.SetLastWriteTimeUtc(destPath, entry.LastWriteTime.Value.UtcDateTime); } catch { }
                }
            }
            else
            {
                string? parent = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                Console.WriteLine($"- {entry.Name}");

                // Si falla la verificación, la librería borra el archivo parcial y relanza.
                archive.Extract(entry, destPath, opts.Overwrite);

                filesCount++;
                totalBytes += entry.Size;
            }
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine("Todo correcto");
        Console.WriteLine($"Carpetas: {dirsCount}");
        Console.WriteLine($"Archivos: {filesCount}");
        if (renamedCount > 0)
        {
            Console.WriteLine($"Renombrados por seguridad: {renamedCount} (ver líneas RENOMBRADO)");
        }
        Console.WriteLine($"Tamaño descomprimido: {totalBytes:N0} bytes");
        Console.WriteLine($"Tiempo transcurrido:   {sw.ElapsedMilliseconds / 1000.0:F2} s");

        return 0;
    }

    /// <summary>
    /// TAR, GZip, BZip2, XZ e ISO por el lector común. Enlaces, dispositivos y FIFOs no se crean: se
    /// informan como omitidos. Los nombres pasan por el mismo saneamiento que en 7z.
    /// </summary>
    private static int ExtractOtherFormat(string archivePath, string outputDir, bool flattenPaths, Stopwatch sw, string? password, bool overwrite)
    {
        using var reader = PrZip.Formats.ArchiveReader.TryOpen(archivePath, password)!;
        Console.WriteLine($"Formato: {reader.FormatName}");
        PrintComment("Comentario del archivo", reader.Comment);
        var usedPaths = EntryPathSanitizer.CreatePathSet();
        int files = 0, dirs = 0, renamed = 0, skipped = 0;
        long bytes = 0;

        while (reader.MoveNext())
        {
            var entry = reader.Current;
            if (entry.Kind is not (PrZip.Formats.ArchiveEntryKind.File or PrZip.Formats.ArchiveEntryKind.Directory))
            {
                skipped++;
                Console.ForegroundColor = ConsoleColor.Yellow;
                string detail = entry.Kind == PrZip.Formats.ArchiveEntryKind.AlternateStream ? $" ({entry.Size:N0} bytes)" : "";
                Console.WriteLine($"  OMITIDO ({entry.Kind}): '{entry.Name}'{detail}" + (entry.LinkTarget != null ? $" -> '{entry.LinkTarget}'" : ""));
                Console.ResetColor();
                continue;
            }

            PrintComment($"Comentario de '{entry.Name}'", entry.Comment);

            bool isDir = entry.Kind == PrZip.Formats.ArchiveEntryKind.Directory;
            if (flattenPaths && isDir)
            {
                continue;
            }

            string dest = EntryPathSanitizer.ResolveTarget(outputDir, entry.Name, isDir, flattenPaths, usedPaths, out bool wasRenamed);
            if (wasRenamed)
            {
                renamed++;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  RENOMBRADO: '{entry.Name}' -> '{Path.GetRelativePath(outputDir, dest)}'");
                Console.ResetColor();
            }

            if (isDir)
            {
                Directory.CreateDirectory(dest);
                dirs++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            Console.WriteLine($"- {entry.Name}");
            bool created = false;
            try
            {
                using (var fs = new FileStream(dest, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    reader.CopyCurrentTo(fs);
                    bytes += fs.Length;
                }

                if (entry.LastWriteTime.HasValue)
                {
                    try { File.SetLastWriteTimeUtc(dest, entry.LastWriteTime.Value.UtcDateTime); } catch (IOException) { }
                }
            }
            catch
            {
                // Solo se borra lo que creó esta extracción, nunca un archivo que ya estaba.
                if (created)
                {
                    File.Delete(dest);
                }

                throw;
            }

            files++;
        }

        // Fin del recorrido: con esto los comprimidos verifican su CRC final.
        sw.Stop();
        Console.WriteLine();
        Console.WriteLine("Todo correcto");
        Console.WriteLine($"Carpetas: {dirs}");
        Console.WriteLine($"Archivos: {files}");
        if (renamed > 0) Console.WriteLine($"Renombrados por seguridad: {renamed} (ver líneas RENOMBRADO)");
        if (skipped > 0) Console.WriteLine($"Omitidos (enlaces, dispositivos, flujos NTFS): {skipped} (ver líneas OMITIDO)");
        Console.WriteLine($"Tamaño descomprimido: {bytes:N0} bytes");
        Console.WriteLine($"Tiempo transcurrido:   {sw.ElapsedMilliseconds / 1000.0:F2} s");
        return 0;
    }

    /// <summary>
    /// Muestra un comentario. Los caracteres de control (salvo saltos de línea y tabulaciones) se ven como '?':
    /// un comentario puede traer secuencias de escape que alteren la terminal.
    /// </summary>
    private static void PrintComment(string title, PrZip.Formats.ArchiveComment? comment)
    {
        if (comment == null)
        {
            return;
        }

        if (comment.Text == null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{title}: no se pudo leer ({comment.Problem})");
            Console.ResetColor();
            return;
        }

        var safe = new System.Text.StringBuilder(comment.Text.Length);
        foreach (char c in comment.Text)
        {
            safe.Append(char.IsControl(c) && c is not ('\r' or '\n' or '\t') ? '?' : c);
        }

        Console.WriteLine($"{title}:");
        Console.WriteLine(safe.ToString().TrimEnd('\r', '\n'));
    }

    private static int ExecuteList(CliOptions opts)
    {
        if (string.IsNullOrEmpty(opts.ArchivePath) || !File.Exists(opts.ArchivePath))
        {
            Console.Error.WriteLine($"ERROR: Archivo 7z no encontrado: '{opts.ArchivePath}'");
            return 2;
        }

        string fullArchivePath = Path.GetFullPath(opts.ArchivePath);
        using var archive = Archive7z.Open(fullArchivePath, opts.Password);

        Console.WriteLine($"Listado de archivo: {fullArchivePath}");
        Console.WriteLine();
        Console.WriteLine("   Fecha      Hora   Atributos          Tamaño   CRC-32    Nombre");
        Console.WriteLine("------------------- ---------- --------------- --------  ------------------------");

        long totalSize = 0;
        int fileCount = 0;
        int dirCount = 0;

        foreach (var entry in archive.Entries)
        {
            string dateStr = entry.LastWriteTime.HasValue
                ? entry.LastWriteTime.Value.DateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "                   ";

            string attrStr = FormatAttributes(entry);
            string sizeStr = entry.IsDirectory ? string.Empty : entry.Size.ToString("N0", CultureInfo.InvariantCulture);
            string crcStr = entry.Crc.HasValue ? $"{entry.Crc.Value:X8}" : string.Empty;

            Console.WriteLine($"{dateStr} {attrStr,-10} {sizeStr,15} {crcStr,-8}  {entry.Name}");

            if (entry.IsDirectory)
            {
                dirCount++;
            }
            else
            {
                fileCount++;
                totalSize += entry.Size;
            }
        }

        Console.WriteLine("------------------- ---------- --------------- --------  ------------------------");
        Console.WriteLine($"                               {totalSize,15:N0}           {fileCount} archivos, {dirCount} carpetas");

        return 0;
    }

    private static int ExecuteTest(CliOptions opts)
    {
        if (string.IsNullOrEmpty(opts.ArchivePath) || !File.Exists(opts.ArchivePath))
        {
            Console.Error.WriteLine($"ERROR: Archivo 7z no encontrado: '{opts.ArchivePath}'");
            return 2;
        }

        string fullArchivePath = Path.GetFullPath(opts.ArchivePath);
        Console.WriteLine($"Probando integridad de archivo: {fullArchivePath}");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();
        using var archive = Archive7z.Open(fullArchivePath, opts.Password);

        int count = 0;
        long totalSize = 0;

        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory || entry.IsEmptyStream)
            {
                continue;
            }

            Console.WriteLine($"T {entry.Name}");
            archive.Extract(entry, Stream.Null);
            count++;
            totalSize += entry.Size;
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine("Todo correcto (Integridad CRC-32 verificada)");
        Console.WriteLine($"Archivos comprobados: {count}");
        Console.WriteLine($"Tamaño verificado:    {totalSize:N0} bytes");
        Console.WriteLine($"Tiempo transcurrido:  {sw.ElapsedMilliseconds / 1000.0:F2} s");

        return 0;
    }

    private static string FormatAttributes(Entry7z entry)
    {
        if (entry.IsDirectory) return "D....";
        if (!entry.Attributes.HasValue) return ".....";

        var attr = entry.Attributes.Value;
        char r = attr.HasFlag(FileAttributes.ReadOnly) ? 'R' : '.';
        char h = attr.HasFlag(FileAttributes.Hidden) ? 'H' : '.';
        char s = attr.HasFlag(FileAttributes.System) ? 'S' : '.';
        char a = attr.HasFlag(FileAttributes.Archive) ? 'A' : '.';
        return $"..{r}{h}{s}{a}";
    }
}

internal sealed class CliOptions
{
    public string Command { get; set; } = string.Empty;
    public string ArchivePath { get; set; } = string.Empty;
    public List<string> Targets { get; } = [];
    public string? OutputDir { get; set; }
    public string? Password { get; set; }
    public bool EncryptHeaders { get; set; }
    public bool Solid { get; set; }
    public int Level { get; set; } = 5;
    public int Threads { get; set; } = 1;
    public CompressionMethod Method { get; set; } = CompressionMethod.Lzma2;
    public bool ShowHelp { get; set; }

    /// <summary>-y: pisar archivos que ya estén en el destino, o el 7z que se crea. Sin él, uno existente detiene la operación.</summary>
    public bool Overwrite { get; set; }

    public static CliOptions Parse(string[] args)
    {
        var opts = new CliOptions();
        int positionalIndex = 0;

        foreach (var rawArg in args)
        {
            var arg = rawArg.Trim();

            if (arg.StartsWith('-') || arg.StartsWith('/'))
            {
                string switchBody = arg.TrimStart('-', '/');

                if (switchBody.Equals("h", StringComparison.OrdinalIgnoreCase) ||
                    switchBody.Equals("?", StringComparison.OrdinalIgnoreCase) ||
                    switchBody.Equals("help", StringComparison.OrdinalIgnoreCase))
                {
                    opts.ShowHelp = true;
                }
                else if (switchBody.Equals("y", StringComparison.OrdinalIgnoreCase))
                {
                    opts.Overwrite = true;
                }
                else if (switchBody.StartsWith("o", StringComparison.OrdinalIgnoreCase))
                {
                    string pathVal = switchBody[1..].TrimStart(':');
                    opts.OutputDir = pathVal;
                }
                else if (switchBody.StartsWith("p", StringComparison.OrdinalIgnoreCase))
                {
                    string passVal = switchBody[1..].TrimStart(':');
                    opts.Password = passVal;
                }
                else if (switchBody.StartsWith("ms", StringComparison.OrdinalIgnoreCase))
                {
                    string msVal = switchBody[2..].TrimStart('=', ':');
                    opts.Solid = !msVal.Equals("off", StringComparison.OrdinalIgnoreCase);
                }
                else if (switchBody.StartsWith("mhe", StringComparison.OrdinalIgnoreCase))
                {
                    string mheVal = switchBody[3..].TrimStart('=', ':');
                    opts.EncryptHeaders = !mheVal.Equals("off", StringComparison.OrdinalIgnoreCase);
                }
                else if (switchBody.StartsWith("mx", StringComparison.OrdinalIgnoreCase))
                {
                    string levelStr = switchBody[2..].TrimStart('=', ':');
                    if (int.TryParse(levelStr, out int lvl))
                    {
                        opts.Level = Math.Clamp(lvl, 0, 9);
                    }
                }
                else if (switchBody.StartsWith("mmt", StringComparison.OrdinalIgnoreCase))
                {
                    string mtVal = switchBody[3..].TrimStart('=', ':');
                    if (mtVal.Equals("on", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(mtVal))
                    {
                        opts.Threads = Environment.ProcessorCount;
                    }
                    else if (mtVal.Equals("off", StringComparison.OrdinalIgnoreCase))
                    {
                        opts.Threads = 1;
                    }
                    else if (int.TryParse(mtVal, out int th))
                    {
                        opts.Threads = Math.Max(1, th);
                    }
                }
                else if (switchBody.StartsWith("m0=", StringComparison.OrdinalIgnoreCase))
                {
                    string methodStr = switchBody[3..].ToLowerInvariant();
                    opts.Method = methodStr switch
                    {
                        "copy" => CompressionMethod.Copy,
                        "lzma" => CompressionMethod.Lzma,
                        "ppmd" => CompressionMethod.Ppmd,
                        "bzip2" => CompressionMethod.BZip2,
                        "deflate" => CompressionMethod.Deflate,
                        _ => CompressionMethod.Lzma2
                    };
                }
            }
            else
            {
                if (positionalIndex == 0)
                {
                    opts.Command = arg;
                }
                else if (positionalIndex == 1)
                {
                    opts.ArchivePath = arg;
                }
                else
                {
                    opts.Targets.Add(arg);
                }
                positionalIndex++;
            }
        }

        return opts;
    }
}
