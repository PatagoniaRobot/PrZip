# PrZip — Patagonia Robot Archive Engine

A 7z engine written entirely in managed C# for .NET 10: it **reads and writes 7z** and **opens RAR, TAR, GZip,
BZip2, XZ and ISO 9660** archives. No native code, no external dependencies, trimming- and Native-AOT-friendly.

PrZip is the archive engine of [ForZip](https://github.com/PatagoniaRobot/ForZip), a forensic packaging and
verification tool, and was built with that use in mind: every file is checked against its stored CRC, hostile
archives are rejected with a clear error instead of hanging or filling the disk, and nothing is written outside the
destination folder or over an existing file.

> **Español:** [README_ES.md](README_ES.md)

> PrZip is an independent project. It is **not affiliated with 7-Zip or with Igor Pavlov**; it implements the public
> 7z format. See [Provenance](#provenance-and-licenses).

**Status: 0.1.0-alpha.** The API may still change. Not published on NuGet yet.

---

## What it does

### 7z — read and write

| | Read | Write |
|---|---|---|
| Compression | Copy, LZMA, LZMA2, PPMd (var. H), BZip2, Deflate, Deflate64 | Copy, LZMA, LZMA2, PPMd, BZip2, Deflate |
| Filters | BCJ (x86), BCJ2, ARM, ARM64, ARM-Thumb, PowerPC, SPARC, IA-64, RISC-V, Delta | BCJ (x86), applied automatically to `.exe`/`.dll` |
| Encryption | AES-256 (7zAES), including encrypted headers | AES-256 with a random salt and a fresh IV per folder; optional encrypted file names |
| Layout | Solid and non-solid, encoded headers, empty files and folders | Solid (with a block size limit) or one folder per file; multi-threaded |

The writer produces the same bytes for any number of threads. A failure while writing never leaves a file that
looks like a valid 7z: the writer refuses to continue, and an output file it created is deleted.

### Other formats — read

| Format | Notes |
|---|---|
| **RAR** 1.4 to 7 | Every compression method, solid archives, self-extracting `.exe`, encryption (RAR 1.3 to 5.0, including encrypted headers), multi-volume sets in both naming schemes. Comments and NTFS streams are reported. In the separate `PrZip.Rar` component (see [Licenses](#provenance-and-licenses)). |
| **TAR**, and TAR inside GZip, BZip2 or XZ | Recognised by content, not by extension. |
| **GZip**, **BZip2**, **XZ** | Single compressed files. BZip2 includes "randomised" blocks; XZ checks CRC32, CRC64 or SHA-256 per block and supports concatenated streams. |
| **ISO 9660** | With Joliet long names. Rock Ridge and UDF are not interpreted. |

### Built for untrusted input

- Every size, count and dictionary declared by an archive is checked against limits **before** memory is reserved.
- Key-derivation rounds are capped (2^24, the same limit observed in 7-Zip), so a crafted archive cannot hang the reader.
- ISO images whose entries overlap to "contain" far more than their size, and RAR files declaring huge dictionaries, are handled without exhausting memory or disk.
- `EntryPathSanitizer` turns any entry name into a safe relative path: `..` is refused (Zip Slip), absolute paths and drive letters are dropped, Windows-invalid and reserved names (`NUL`, `CON`…) are fixed, names longer than the file system allows are shortened with a fingerprint of the original, and collisions get a `~2` suffix. Every change is reported.
- Fuzzed with more than 20,000 mutated archives (7z, RAR, TAR, GZip, BZip2, XZ) without a hang or an unexpected exception: a damaged archive only ever raises a `PrZipException`.

---

## Usage

Reference `src/PrZip/PrZip.csproj` (and `src/PrZip.Rar/PrZip.Rar.csproj` if you need RAR). The examples below are
compiled and run against the current code.

### Write a 7z

```csharp
using System.Text;
using PrZip;

var options = new WriterOptions7z
{
    Method = CompressionMethod.Lzma2,
    Level = 5,
    Threads = 0,               // every core; the output is identical for any thread count
    Password = "secret",       // AES-256
    EncryptHeaders = true,     // file names encrypted too
};

using (var writer = Writer7z.Create("case.7z", options))   // refuses to overwrite an existing file
{
    writer.Add(Path.Combine("evidence", "report.pdf"), "report.pdf");
    writer.Add(new WriterEntry7z { Name = "notes/readme.txt" }, new MemoryStream(Encoding.UTF8.GetBytes("hello")));
    writer.Add(new WriterEntry7z { Name = "empty-folder", IsDirectory = true });
    writer.Finish();
}
```

`Writer7z.Create(path, options, overwrite: true)` replaces an existing file on purpose. Invalid entry names (empty,
absolute, with `..`, duplicated…) are rejected with an `ArgumentException` without spoiling the archive being written.

### Read a 7z

```csharp
using (var archive = Archive7z.Open("case.7z", password: "secret"))
{
    foreach (Entry7z entry in archive.Entries)
    {
        Console.WriteLine($"{entry.Name}  {entry.Size} bytes  CRC {entry.Crc:X8}");
    }

    archive.ExtractAll("output");   // every file is CRC-checked; existing files are never overwritten
}
```

`Archive7z.Extract(entry, stream)` extracts a single entry. `Archive7z.Open` also accepts an `IRandomAccessSource`
(`FileRandomAccessSource`, `MemoryRandomAccessSource`, or your own).

### Any supported format

```csharp
using PrZip.Formats;
using PrZip.Rar;

RarSupport.Register();   // once at startup, only if you reference PrZip.Rar

var used = EntryPathSanitizer.CreatePathSet();
using (ArchiveReader? reader = ArchiveReader.TryOpen("evidence.tar.gz"))   // recognised by signature
{
    if (reader == null)
    {
        throw new InvalidDataException("Not an archive format PrZip can open.");
    }

    Console.WriteLine($"Format: {reader.FormatName}");
    while (reader.MoveNext())
    {
        ArchiveEntryInfo entry = reader.Current;
        if (entry.Kind != ArchiveEntryKind.File)
        {
            continue;   // directories, links, devices and NTFS streams are reported, not created
        }

        // Safe target: no "..", no absolute paths, Windows-invalid and over-long names fixed, no collisions.
        string target = EntryPathSanitizer.ResolveTarget("output", entry.Name, isDirectory: false, flatten: false, used, out bool renamed);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var file = new FileStream(target, FileMode.CreateNew);
        reader.CopyCurrentTo(file);
    }
}
```

`ArchiveReader` reads entries in order, the only way possible in a `.tar.gz`. `BytesRead` and `ArchiveLength`
give progress even when the uncompressed size is unknown.

### Errors

Everything that comes from the archive raises a subclass of `PrZipException`: `CorruptArchiveException`
(and `ArchiveTruncatedException`), `InvalidPasswordOrCorruptDataException`, `UnsupportedMethodException` or
`LimitExceededException`. I/O errors from your own streams are passed through.

### Command line

`tools/PrZip.Cli` is a small command-line front end, mostly for testing (`a`, `x`, `e`, `l`, `t`, with `-p`,
`-mhe`, `-ms`, `-mx`, `-mmt`, `-m0` and `-y`). Run it without arguments for the list.

---

## Building and testing

Requires the .NET 10 SDK. The tests also need **Python 3** in the `PATH` (it builds the TAR, GZip, BZip2, XZ and LZMA test files with its standard library, independently of PrZip) and use Windows' `tar.exe` when present.

```bash
dotnet build PrZip.slnx
dotnet test tests/PrZip.Tests
```

About 60 tests compare PrZip with the official 7-Zip (what it writes, what it accepts). They look for `7z.exe` in
the `PATH`, in Program Files, or in the `OFFICIAL_7Z_EXE` environment variable, and are **skipped** (reported as
skipped, not passed) when it is not found. `OFFICIAL_7Z_EXE=none` simulates a machine without 7-Zip.

## Repository layout

| Path | Contents |
|---|---|
| `src/PrZip` | Public API: `Archive7z`, `Writer7z`, `ArchiveReader`, `EntryPathSanitizer`, and the TAR, XZ and ISO readers. |
| `src/PrZip.Container` | 7z headers, folders and coder graphs (internal). |
| `src/PrZip.Codecs` | LZMA, LZMA2, PPMd, BZip2, Deflate, filters, 7zAES (internal). |
| `src/PrZip.Primitives` | Exceptions, `IRandomAccessSource`, checksums, low-level I/O. |
| `src/PrZip.Rar` | RAR reader, under the UnRAR license. Depends on the core; the core never depends on it. |
| `tests/PrZip.Tests` | Unit, round-trip, hostile-input and interoperability tests. |
| `tools/` | Command line, fuzzer, interoperability script. |
| `docs/` | [ARCHITECTURE.md](docs/ARCHITECTURE.md) (layers, read and write paths, defences, tests), [PROVENANCE.md](docs/PROVENANCE.md) (origin of every component) and [FORMAT.md](docs/FORMAT.md) (format notes, black-box findings). Written in Spanish, like the code comments. |

---

## Provenance and licenses

PrZip is licensed under the **Apache License 2.0** ([LICENSE](LICENSE)), **except the `PrZip.Rar` component**, which
is distributed under the UnRAR license ([src/PrZip.Rar/LICENSE-UNRAR.txt](src/PrZip.Rar/LICENSE-UNRAR.txt)). If you
do not reference `PrZip.Rar`, none of its code is in your build.

| Part | Origin | License |
|---|---|---|
| Most of the code | Written for PrZip | Apache 2.0 |
| LZMA/LZMA2 coders, match finders, PPMd, branch filters | Ported from the `C/` folder of Igor Pavlov's LZMA SDK | Public domain |
| BZip2 block randomisation table | `randtable.c` of bzip2/libbzip2 1.0.8 (Julian Seward), with its notice | bzip2 license (BSD-style) |
| RAR reader (`PrZip.Rar`) | Declared port of RARLAB's UnRAR 7.3.1 | UnRAR license: may be used to read RAR in any software; never to create RAR archives. PrZip only reads RAR. |

No code from 7-Zip's `CPP/` folder (LGPL) is used. What the public format documentation does not describe was
established by **black-box tests against the official `7z.exe`**, documented in [FORMAT.md](docs/FORMAT.md) and
reproduced by `BlackBoxFormatTests`. The full record is in [PROVENANCE.md](docs/PROVENANCE.md); the required
notices are in [NOTICE](NOTICE). A test fails if any source file lacks its license header or if UnRAR-derived
code appears outside `PrZip.Rar`.

---

Copyright (c) 2026 Claudio Andino — developed under the Patagonia Robot initiative.
