# Registro de Procedencia y Licencias (PROVENANCE.md)

Este documento registra el origen, licencia y trazabilidad de todos los algoritmos, especificaciones y componentes utilizados en el proyecto **PrZip**.

---

## 1. Política de licencias y de procedencia

Para que la librería quede **libre de licencias copyleft (GPL, LGPL) y apta para cualquier distribución o uso
comercial o pericial, también en productos cerrados**, se sigue esta política:

1. **Código portado directamente:**
   * Solo se porta código fuente de la subcarpeta `C/` del **LZMA SDK oficial de Igor Pavlov**, liberado al
     **dominio público**.
   * **Excepción RAR (2026-10-09):** el descompresor RAR es un port declarado de **unrar 7.3.1 de RARLAB** y
     vive **solo** en el ensamblado `src/PrZip.Rar/`, con su licencia (`LICENSE-UNRAR.txt`) y el párrafo 2 de
     esa licencia al comienzo de cada archivo portado. La licencia de unrar permite usar su código para
     manejar archivos RAR en cualquier software, pero no para crear un compresor RAR: ese ensamblado no es
     libre en sentido OSI y no escribe RAR. Fuera de `src/PrZip.Rar/` no puede haber código derivado de
     unrar (lo controla `LicenseHeaderTests`), y el núcleo no depende de ese ensamblado.
   * **Excepción bzip2 (2026-10-13):** solo la tabla de 512 números de aleatorización de bloques, con su
     aviso, en `BZip2RandomTable.cs` (ver el registro de componentes).
   * libarchive (BSD) se evaluó como fuente para RAR y se descartó: sus lectores RAR no son BSD limpios.
2. **Código Prohibido:**
   * Queda estrictamente **prohibido** copiar o traducir código de `CPP/7zip/...` del repositorio de 7-Zip, el cual está licenciado bajo GNU LGPL con restricciones comerciales unRAR impuestas por RARLAB.
   * Para RAR se porta de la fuente original (unrarsrc), nunca de derivados: el RAR de 7-Zip, junrar,
     SharpCompress, unarr, The Unarchiver y libarchive siguen **prohibidos**.
3. **Escritura y Contenedor:**
   * Todo el motor de serialización y empaquetado del contenedor 7z (`PrZip.Container` y `Writer7z`) se implementa siguiendo la especificación pública **`DOC/7zFormat.txt`** y analizando la salida del binario oficial como caja negra.
4. **Criptografía:**
   * Implementada usando exclusivamente las primitivas estándar de .NET 10 (`System.Security.Cryptography.Aes` y `SHA256`).
   * Lo que `7zFormat.txt` no documenta de 7zAES (disposición de las propiedades, derivación de la clave,
     tope de vueltas) se estableció probando 7z.exe como caja negra: ver `FORMAT.md` §5.
5. **Citas en el código:** los comentarios citan el LZMA SDK `C/`, especificaciones públicas o lo observado
   en 7z.exe; nunca nombres, constantes ni archivos de `CPP/`. (Revisión del 2026-10-13: se quitaron tres
   citas de ese tipo y se reescribió el lector de propiedades de 7zAES.)

---

## 2. Registro de Componentes

| Componente | Origen Canónico | Licencia de Origen | Estado de Adaptación |
| :--- | :--- | :--- | :--- |
| **Especificación de Contenedor** | `DOC/7zFormat.txt` (Igor Pavlov) | Dominio Público | Implementación limpia en C# .NET 10. |
| **Range Coder Aritmético** | `C/LzmaDec.c` / `C/RangeEnc.c` | Dominio Público | Portado y modernizado a `Span<byte>` y `ref struct`. |
| **LZMA Core (Decoder)** | `C/LzmaDec.c` | Dominio Público | Portado con diseño dual *fast-path* / *slow-path*. |
| **LZMA Core (Encoder)** | `C/LzmaEnc.c` | Dominio Público | Portado limpio (Fast Greedy/Lazy y Optimal Parsing). |
| **LZMA2 Chunk Controller** | `C/Lzma2Dec.c` / `C/Lzma2Enc.c` | Dominio Público | Máquina de control de chunks y partición determinista. |
| **Match Finders (HC4 & BT4)** | `C/LzFind.c` | Dominio Público | Portado con hashing multietapa y bifurcación binaria. |
| **Filtros Branch (BCJ x86, ARM, ARM64, ARM-Thumb, PPC, SPARC, IA-64, RISC-V)** | `C/Bra.c` / `C/Bra86.c` | Dominio Público | Portado a C# con soporte de estado entre bloques. |
| **Filtro Delta** | `C/Delta.c` | Dominio Público | Portado (`DeltaFilter`). |
| **Filtro BCJ2** | `C/Bcj2.c` | Dominio Público | Decoder para 4 streams simultáneos. |
| **CRC-32 IEEE** | RFC 1952 / IEEE 802.3 (`0xEDB88320`) | Dominio Público | Implementación propia (Slicing-by-16 + PCLMULQDQ / ARM CRC). |
| **7zAES: propiedades, derivación de clave y cifrado** | Pruebas de caja negra contra 7z.exe 24.08 (`FORMAT.md` §5; reproducidas por `BlackBoxFormatTests`) | Hechos del formato observados | Implementación propia con `SHA256` y `Aes` de .NET. El lector de propiedades se reescribió el 2026-10-13 desde esas pruebas. |
| **Parámetros por nivel (órdenes de PPMd)** | Salida de 7z.exe 24.08 (`FORMAT.md` §6) | Hechos observados | Tabla propia; la memoria de PPMd y el bloque de BZip2 son elección propia. |
| **Compresor Deflate** | RFC 1951 | Especificación pública | Implementación propia desde la RFC (el LZMA SDK `C/` no trae compresor Deflate; el de 7-Zip está en `CPP/`, excluido). |
| **Descompresor Deflate y Deflate64** | RFC 1951; las tres diferencias de Deflate64 (ventana de 64 KB, largo 285 con 16 bits extra, distancias 30 y 31) verificadas contra 7z.exe (`DeflateTests`) | Especificación pública y caja negra | Implementación propia (`Inflater`); no usa `DeflateStream` de .NET. |
| **Descompresor BZip2** | Formato bzip2 | Formato documentado | Implementación propia (`BZip2Decoder`); ni código de bzip2 ni de `CPP/7zip`. |
| **Tabla de aleatorización de BZip2** | `BZ2_rNums` de `randtable.c`, bzip2/libbzip2 1.0.8 (Julian Seward) | Licencia de bzip2 (tipo BSD: conservar el aviso en el código fuente y marcar la versión modificada; los binarios no exigen nada) | Solo los 512 números, extraídos por programa del archivo original y traducidos a C# en `BZip2RandomTable.cs`, con el aviso y la licencia completos. La usa el decodificador para los bloques "randomised". |
| **Compresor BZip2** | Formato bzip2 (cabeceras, RLE, BWT, MTF, Huffman, CRC) | Formato documentado | Implementación propia desde el formato; ni código de bzip2 ni de `CPP/7zip`. |
| **Huffman con largo máximo** | Package-merge (Larmore y Hirschberg, 1990) | Algoritmo publicado | Implementación propia, compartida por Deflate y BZip2. |
| **BWT (orden de rotaciones)** | Larsson y Sadakane, "Faster suffix sorting" (1999) | Algoritmo publicado | Implementación propia adaptada a rotaciones cíclicas. |
| **Contenedor XZ** | "The .xz File Format" 1.x (Tukaani) | Dominio público | Implementación propia; reutiliza LZMA2 y filtros del motor. |
| **CRC-64** | ECMA-182 (forma reflejada) | Estándar público | Implementación propia por tabla. |
| **ISO 9660 y Joliet** | ECMA-119; especificación Joliet (Microsoft, 1995) | Estándares públicos | Lector propio. |
| **Lector RAR (contenedor, nombres, fechas, sumas)** | unrar 7.3.1 (RARLAB): `arcread.cpp`, `archive.cpp`, `rawread.cpp`, `encname.cpp`, `timefn.cpp`, `hash.cpp`, `extract.cpp` | Licencia de unrar (no OSI; prohíbe crear compresores RAR) | Port declarado en `src/PrZip.Rar/`, con el párrafo 2 de la licencia en cada archivo. |
| **Descompresor RAR5/RAR7** | unrar 7.3.1: `unpack.cpp`, `unpackinline.cpp`, `unpack50.cpp`, `getbits.hpp`, `compress.hpp` | Licencia de unrar | Port declarado en `src/PrZip.Rar/Unpack*.cs`; ventana en arreglo administrado (sin la ventana fragmentada ni el multihilo de unrar). |
| **Descompresor RAR 2.9–4 (v29) y filtros de la RarVM** | unrar 7.3.1: `unpack30.cpp`, `rarvm.cpp`, `DecodeInit` de `model.cpp` | Licencia de unrar | Port declarado en `src/PrZip.Rar/Unpack30.cs` y `RarVM.cs`. |
| **Comentarios y flujos NTFS de RAR** | unrar 7.3.1: `arccmt.cpp`, `ReadSubData`/`SearchSubBlock` de `arcread.cpp`, `GetStreamNameNTFS` de `win32stm.cpp`; comentarios por archivo de RAR 1.5–2.x según el `TECHNOTE.DOC` de RAR 1.55 (documentación de RARLAB) | Licencia de unrar | Port declarado en `src/PrZip.Rar/RarComments.cs`. |
| **Descompresores RAR 1.5 y 2.0** | unrar 7.3.1: `unpack15.cpp`, `unpack20.cpp` | Licencia de unrar | Port declarado en `src/PrZip.Rar/Unpack15.cs` y `Unpack20.cs`. |
| **Cifrado RAR (1.3, 1.5, 2.0, 3.x, 5.0)** | unrar 7.3.1: `crypt.cpp`, `crypt1.cpp`, `crypt2.cpp`, `crypt3.cpp`, `crypt5.cpp`, `sha1.cpp` (SHA-1 de Steve Reid, dominio público) | Licencia de unrar | Port declarado en `src/PrZip.Rar/RarCrypt.cs`; AES, HMAC-SHA256 y SHA-256 de .NET. |
| **Volúmenes RAR** | unrar 7.3.1: `volume.cpp` (MergeArchive), `pathfn.cpp` (NextVolumeName, GetVolNumPos, VolNameToFirstName) | Licencia de unrar | Port declarado en `src/PrZip.Rar/RarVolumes.cs` y `RarArchive.cs`. |
| **PPMd de RAR 3.x (codificador de Subbotin)** | `C/Ppmd7aDec.c` del LZMA SDK (Igor Pavlov) | Dominio público | `src/PrZip.Codecs/Ppmd/Ppmd7aDecoder.cs`, sobre el `Ppmd7Model` existente. No se usa el PPMd de unrar. |
| **BLAKE2sp (para RAR5)** | `blake2s.cpp`/`blake2sp.cpp` de unrar 7.3.1, basados en la referencia de Samuel Neves (CC0) | CC0 + licencia de unrar | Portado en `src/PrZip.Rar/Blake2sp.cs`. |
| **Corpus de prueba RAR** | `libarchive/test/*.rar.uu` (commit f65c564) | BSD-2 (libarchive) | Datos de prueba, no código: `tests/PrZip.Tests/Formats/Rar/libarchive/` con su aviso de licencia. |
| **TAR y GZip** | `System.Formats.Tar` y `System.IO.Compression` de .NET | Parte de .NET (MIT) | Se usan tal cual; no son dependencias de terceros. |

---

## 3. Atribución

Este proyecto reconoce y agradece profundamente el trabajo pionero de **Igor Pavlov**, creador de 7-Zip y del algoritmo LZMA, por haber puesto el núcleo del LZMA SDK a disposición de la humanidad en el dominio público.

---

## 4. Qué exige cada licencia al distribuir

Resumen de los textos de cada licencia, para quien use el motor en otro producto (incluido uno cerrado).
No es asesoramiento legal.

| Parte | Licencia | Qué pide su texto |
| :--- | :--- | :--- |
| Todo salvo `PrZip.Rar` | Apache 2.0 (`LICENSE`, decidido el 2026-10-13, como ForZip) | Incluir la licencia y el archivo `NOTICE` con lo que se distribuya, y marcar los archivos modificados. Permite uso comercial y código cerrado. Cada archivo propio lleva el encabezado Apache (lo exige `LicenseHeaderTests`). |
| Código portado del LZMA SDK `C/` | Dominio público | Nada. |
| `PrZip.Rar` (port de unrar 7.3.1) | Licencia de unrar (`LICENSE-UNRAR.txt`) | Se puede usar en cualquier software para manejar archivos RAR, gratis. No se puede usar para crear un compresor compatible con RAR ni para recrear su algoritmo de compresión. Al distribuir el código modificado, el párrafo 2 completo tiene que ir en la licencia (o en la documentación si no hay licencia) y en los comentarios del código. |
| `BZip2RandomTable.cs` (tabla de bzip2) | Licencia de bzip2 | En el código fuente, conservar su aviso, condiciones y descargo (ya están en el archivo) y marcar que es una versión modificada. Para binarios no pide nada; un reconocimiento en la documentación es "apreciado, no exigido". |
| Datos de prueba de libarchive | BSD-2 | Solo en `tests/`; no se distribuyen con el motor. |
