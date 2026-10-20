# PrZip — Patagonia Robot Archive Engine

Un motor 7z escrito íntegramente en C# administrado para .NET 10: **lee y escribe 7z** y **abre RAR, TAR, GZip,
BZip2, XZ e ISO 9660**. Sin código nativo, sin dependencias externas, apto para recorte (trimming) y Native AOT.

PrZip es el motor de [ForZip](https://github.com/PatagoniaRobot/ForZip), una herramienta forense de empaquetado y
verificación, y se hizo pensando en ese uso: cada archivo se controla contra su CRC, un archivo hostil se rechaza
con un error claro en lugar de colgarse o llenar el disco, y nada se escribe fuera de la carpeta de destino ni
encima de un archivo existente.

> **English:** [README.md](README.md)

> PrZip es un proyecto independiente, **sin relación con 7-Zip ni con Igor Pavlov**; implementa el formato 7z
> público. Ver [Procedencia](#procedencia-y-licencias).

**Estado: 0.1.0-alpha.** La API todavía puede cambiar. Aún no está publicado en NuGet.

---

## Qué hace

### 7z: lectura y escritura

| | Lee | Escribe |
|---|---|---|
| Compresión | Copy, LZMA, LZMA2, PPMd (var. H), BZip2, Deflate, Deflate64 | Copy, LZMA, LZMA2, PPMd, BZip2, Deflate |
| Filtros | BCJ (x86), BCJ2, ARM, ARM64, ARM-Thumb, PowerPC, SPARC, IA-64, RISC-V, Delta | BCJ (x86), automático en `.exe`/`.dll` |
| Cifrado | AES-256 (7zAES), también con cabeceras cifradas | AES-256 con sal aleatoria e IV nuevo por folder; nombres cifrados opcionales |
| Estructura | Sólido y no sólido, cabeceras codificadas, archivos y carpetas vacíos | Sólido (con tope por bloque) o un folder por archivo; varios hilos |

El escritor produce los mismos bytes con cualquier cantidad de hilos. Una falla al escribir nunca deja un archivo
con apariencia de 7z válido: el escritor no sigue, y si creó el archivo de salida, lo borra.

### Otros formatos: lectura

| Formato | Notas |
|---|---|
| **RAR** 1.4 a 7 | Todos los métodos de compresión, archivos sólidos, autoextraíbles `.exe`, cifrado (RAR 1.3 a 5.0, también cabeceras cifradas), volúmenes con los dos esquemas de nombres. Los comentarios y los flujos NTFS se informan. Está en el componente aparte `PrZip.Rar` (ver [Licencias](#procedencia-y-licencias)). |
| **TAR**, y TAR dentro de GZip, BZip2 o XZ | Se reconoce por el contenido, no por la extensión. |
| **GZip**, **BZip2**, **XZ** | Archivos comprimidos sueltos. BZip2 incluye los bloques "randomised"; XZ controla CRC32, CRC64 o SHA-256 por bloque y admite streams concatenados. |
| **ISO 9660** | Con los nombres largos de Joliet. Rock Ridge y UDF no se interpretan. |

### Hecho para entradas no confiables

- Todo tamaño, cantidad y diccionario que declara un archivo se controla contra límites **antes** de reservar memoria.
- Las vueltas de la derivación de clave tienen tope (2^24, el mismo observado en 7-Zip): un archivo armado no puede colgar el lector.
- Las imágenes ISO cuyas entradas se superponen para "contener" mucho más que su tamaño, y los RAR que declaran diccionarios enormes, se manejan sin agotar memoria ni disco.
- `EntryPathSanitizer` convierte cualquier nombre en una ruta relativa segura: rechaza `..` (Zip Slip), descarta rutas absolutas y unidades, corrige nombres inválidos o reservados de Windows (`NUL`, `CON`…), acorta los nombres más largos de lo que admite el sistema de archivos con una huella del original, y resuelve choques con un sufijo `~2`. Todo cambio se informa.
- Probado con más de 20.000 archivos mutados (7z, RAR, TAR, GZip, BZip2, XZ) sin cuelgues ni excepciones inesperadas: un archivo dañado solo lanza `PrZipException`.

---

## Uso

Referenciar `src/PrZip/PrZip.csproj` (y `src/PrZip.Rar/PrZip.Rar.csproj` si se necesita RAR). Los ejemplos de abajo
están compilados y ejecutados contra el código actual.

### Escribir un 7z

```csharp
using System.Text;
using PrZip;

var options = new WriterOptions7z
{
    Method = CompressionMethod.Lzma2,
    Level = 5,
    Threads = 0,               // todos los núcleos; la salida es la misma con cualquier cantidad de hilos
    Password = "secreta",      // AES-256
    EncryptHeaders = true,     // también los nombres de archivo
};

using (var writer = Writer7z.Create("caso.7z", options))   // no pisa un archivo existente
{
    writer.Add(Path.Combine("evidencia", "informe.pdf"), "informe.pdf");
    writer.Add(new WriterEntry7z { Name = "notas/leeme.txt" }, new MemoryStream(Encoding.UTF8.GetBytes("hola")));
    writer.Add(new WriterEntry7z { Name = "carpeta-vacia", IsDirectory = true });
    writer.Finish();
}
```

`Writer7z.Create(ruta, opciones, overwrite: true)` reemplaza un archivo existente a propósito. Un nombre de entrada
inválido (vacío, absoluto, con `..`, repetido…) se rechaza con `ArgumentException` sin arruinar el archivo en curso.

### Leer un 7z

```csharp
using (var archive = Archive7z.Open("caso.7z", password: "secreta"))
{
    foreach (Entry7z entry in archive.Entries)
    {
        Console.WriteLine($"{entry.Name}  {entry.Size} bytes  CRC {entry.Crc:X8}");
    }

    archive.ExtractAll("salida");   // cada archivo se controla con su CRC; nunca se pisan archivos existentes
}
```

`Archive7z.Extract(entry, stream)` extrae una sola entrada. `Archive7z.Open` también acepta un `IRandomAccessSource`
(`FileRandomAccessSource`, `MemoryRandomAccessSource` o uno propio).

### Cualquier formato soportado

```csharp
using PrZip.Formats;
using PrZip.Rar;

RarSupport.Register();   // una vez al arrancar, solo si se referencia PrZip.Rar

var used = EntryPathSanitizer.CreatePathSet();
using (ArchiveReader? reader = ArchiveReader.TryOpen("evidencia.tar.gz"))   // se reconoce por la firma
{
    if (reader == null)
    {
        throw new InvalidDataException("No es un formato que PrZip pueda abrir.");
    }

    Console.WriteLine($"Formato: {reader.FormatName}");
    while (reader.MoveNext())
    {
        ArchiveEntryInfo entry = reader.Current;
        if (entry.Kind != ArchiveEntryKind.File)
        {
            continue;   // carpetas, enlaces, dispositivos y flujos NTFS se informan, no se crean
        }

        // Destino seguro: sin "..", sin rutas absolutas, nombres inválidos o demasiado largos corregidos, sin choques.
        string target = EntryPathSanitizer.ResolveTarget("salida", entry.Name, isDirectory: false, flatten: false, used, out bool renamed);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var file = new FileStream(target, FileMode.CreateNew);
        reader.CopyCurrentTo(file);
    }
}
```

`ArchiveReader` recorre las entradas en orden, que es lo único posible en un `.tar.gz`. `BytesRead` y
`ArchiveLength` dan el progreso aun cuando no se conoce el tamaño descomprimido.

### Errores

Todo lo que viene del archivo lanza una subclase de `PrZipException`: `CorruptArchiveException` (y
`ArchiveTruncatedException`), `InvalidPasswordOrCorruptDataException`, `UnsupportedMethodException` o
`LimitExceededException`. Los errores de E/S de los streams propios pasan tal cual.

### Línea de comandos

`tools/PrZip.Cli` es una línea de comandos chica, pensada sobre todo para pruebas (`a`, `x`, `e`, `l`, `t`, con `-p`,
`-mhe`, `-ms`, `-mx`, `-mmt`, `-m0` y `-y`). Sin argumentos muestra la lista.

---

## Compilar y probar

Requiere el SDK de .NET 10. Los tests necesitan además **Python 3** en el `PATH` (arma los archivos de prueba TAR, GZip, BZip2, XZ y LZMA con su biblioteca estándar, independiente de PrZip) y usan el `tar.exe` de Windows si está.

```bash
dotnet build PrZip.slnx
dotnet test tests/PrZip.Tests
```

Unos 60 tests comparan PrZip con el 7-Zip oficial (lo que escribe, lo que acepta). Buscan `7z.exe` en el `PATH`, en
Program Files o en la variable de entorno `OFFICIAL_7Z_EXE`, y **se saltean** (figuran como omitidos, no como
aprobados) si no lo encuentran. `OFFICIAL_7Z_EXE=none` simula una máquina sin 7-Zip.

## Organización del repositorio

| Ruta | Contenido |
|---|---|
| `src/PrZip` | API pública: `Archive7z`, `Writer7z`, `ArchiveReader`, `EntryPathSanitizer`, y los lectores de TAR, XZ e ISO. |
| `src/PrZip.Container` | Cabeceras 7z, folders y grafos de coders (interno). |
| `src/PrZip.Codecs` | LZMA, LZMA2, PPMd, BZip2, Deflate, filtros, 7zAES (interno). |
| `src/PrZip.Primitives` | Excepciones, `IRandomAccessSource`, sumas de control, E/S de bajo nivel. |
| `src/PrZip.Rar` | Lector RAR, con la licencia de UnRAR. Depende del núcleo; el núcleo nunca depende de él. |
| `tests/PrZip.Tests` | Tests unitarios, de ida y vuelta, de entradas hostiles y de interoperabilidad. |
| `tools/` | Línea de comandos, fuzzer, script de interoperabilidad. |
| `docs/` | [ARCHITECTURE.md](docs/ARCHITECTURE.md) (capas, lectura y escritura, defensas, tests), [PROVENANCE.md](docs/PROVENANCE.md) (origen de cada componente) y [FORMAT.md](docs/FORMAT.md) (notas del formato, resultados de caja negra). |

---

## Procedencia y licencias

PrZip está bajo la **Licencia Apache 2.0** ([LICENSE](LICENSE)), **salvo el componente `PrZip.Rar`**, que se
distribuye con la licencia de UnRAR ([src/PrZip.Rar/LICENSE-UNRAR.txt](src/PrZip.Rar/LICENSE-UNRAR.txt)). Si no se
referencia `PrZip.Rar`, nada de su código entra en la compilación.

| Parte | Origen | Licencia |
|---|---|---|
| La mayor parte del código | Escrito para PrZip | Apache 2.0 |
| Codificadores LZMA/LZMA2, buscadores de coincidencias, PPMd, filtros de saltos | Portados de la carpeta `C/` del LZMA SDK de Igor Pavlov | Dominio público |
| Tabla de aleatorización de BZip2 | `randtable.c` de bzip2/libbzip2 1.0.8 (Julian Seward), con su aviso | Licencia de bzip2 (tipo BSD) |
| Lector RAR (`PrZip.Rar`) | Port declarado de UnRAR 7.3.1 de RARLAB | Licencia de UnRAR: se puede usar para leer RAR en cualquier software; nunca para crear archivos RAR. PrZip solo lee RAR. |

No se usa código de la carpeta `CPP/` de 7-Zip (LGPL). Lo que la documentación pública del formato no describe se
estableció con **pruebas de caja negra contra el `7z.exe` oficial**, documentadas en [FORMAT.md](docs/FORMAT.md) y
reproducidas por `BlackBoxFormatTests`. El registro completo está en [PROVENANCE.md](docs/PROVENANCE.md); los avisos
obligatorios, en [NOTICE](NOTICE). Un test falla si a un archivo de código le falta el encabezado de licencia o si
aparece código derivado de UnRAR fuera de `PrZip.Rar`.

---

Copyright (c) 2026 Claudio Andino — desarrollado bajo la iniciativa Patagonia Robot.
