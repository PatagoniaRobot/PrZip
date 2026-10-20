# Arquitectura de PrZip

Cómo está organizado el motor y por qué. Para el formato 7z en sí, ver [FORMAT.md](FORMAT.md); para el origen y la
licencia de cada pieza, [PROVENANCE.md](PROVENANCE.md).

---

## 1. Capas

```
            ┌──────────────────────────────────────────────┐
            │ PrZip.Rar   (licencia de UnRAR, opcional)    │  RarSupport.Register()
            └──────────────────────┬───────────────────────┘
                                   │ depende de
┌──────────────────────────────────▼───────────────────────────────────┐
│ PrZip          API pública: Archive7z, Writer7z, ArchiveReader,      │
│                EntryPathSanitizer; lectores TAR, XZ e ISO            │
├──────────────────────────────────────────────────────────────────────┤
│ PrZip.Container   cabeceras 7z, folders, grafo de coders (interno)   │
├──────────────────────────────────────────────────────────────────────┤
│ PrZip.Codecs      LZMA, LZMA2, PPMd, BZip2, Deflate, filtros, 7zAES  │
├──────────────────────────────────────────────────────────────────────┤
│ PrZip.Primitives  excepciones, IRandomAccessSource, CRC, VarInt,     │
│                   ProducerStream, SourceWindowStream                 │
└──────────────────────────────────────────────────────────────────────┘
```

- Cada capa depende solo de las de abajo. Ninguna tiene dependencias externas (`Directory.Build.props` hace fallar
  la compilación si un proyecto de `src/` agrega un `PackageReference`).
- **La API pública es chica a propósito:** en `PrZip`, lo que usa quien empaqueta o extrae; en `PrZip.Primitives`, las
  excepciones y las fuentes de datos; en `PrZip.Rar`, solo `RarSupport`. El resto es `internal` y lo ven los demás
  ensamblados del motor y los tests mediante `InternalsVisibleTo`.
- **RAR va aparte y al revés:** `PrZip.Rar` depende del núcleo, nunca el núcleo de él. El núcleo define
  `ArchiveFormatProvider` y `ArchiveReader.RegisterFormat`; `RarSupport.Register()` suma RAR. Quien no referencia
  `PrZip.Rar` no lleva código con la licencia de UnRAR (lo controla `ArchitectureTests`).
- Todo el código es apto para recorte y Native AOT (`IsAotCompatible`).

---

## 2. Lectura de un 7z

```
archivo ──► SignatureHeader ──► cabecera (CRC) ──► ¿kEncodedHeader? ──► ArchiveHeaderReader ──► Archive7z
                                                      │ sí: se decodifica con
                                                      ▼ el mismo FolderDecoder
                                              (hasta 4 niveles anidados)

Extract(entrada) ──► folder de la entrada ──► FolderDecoder ──► salida principal (Stream) ──► CRC al vuelo ──► destino
                                                 ▲
                         flujos empaquetados: SourceWindowStream sobre IRandomAccessSource
```

1. **Fuente de datos.** Todo se lee de un `IRandomAccessSource` (archivo o memoria) con lecturas posicionales. Cada
   flujo empaquetado es un `SourceWindowStream`, una ventana sin copia sobre esa fuente: varias ventanas se leen a la
   vez desde hilos distintos.
2. **Cabecera.** `SignatureHeader` da la posición, el tamaño y el CRC de la cabecera, y se verifican antes de leerla.
   Si es una cabecera codificada (comprimida o cifrada), se arma su folder y se decodifica con el mismo
   `FolderDecoder` que los datos. `ArchiveHeaderReader` interpreta folders, bind pairs, `SubStreamsInfo` y
   `FilesInfo`, y acota cada cantidad que declara el archivo antes de usarla (sección 5).
3. **Grafo de coders.** `FolderDecoder` evalúa el grafo de cualquier folder de forma genérica: cada salida es un
   `Stream` que se lee a demanda, conectado según los bind pairs. Los decodificadores que trabajan "empujando"
   (escriben hacia un stream) corren detrás de un `ProducerStream`, que los convierte en un stream que se lee a
   demanda, con una cola acotada: la memoria no depende del tamaño del folder, solo de los diccionarios y de los
   búferes de tránsito.
4. **Extracción.** El CRC de cada archivo se calcula mientras se copia y se compara al final. `Archive7z` mantiene un
   cursor por folder: extraer en orden las entradas de un bloque sólido lo decodifica una sola vez.
5. **Cifrado.** Las claves derivadas se guardan por (sal, vueltas) en un caché que vive mientras el `Archive7z` está
   abierto y se borra de memoria al cerrarlo: con muchos folders cifrados con la misma sal, se deriva una sola vez.

---

## 3. Escritura de un 7z

```
Add(entrada, datos) ──► [BCJ] ──► compresor ──► [AES-256] ──► destino        (FolderBuilder)
                                                                  │
Finish() ──► cabecera (ArchiveHeaderWriter) ──► [cifrada] ──► firma de 32 bytes, al final de todo
```

- **`FolderBuilder`** encadena streams de escritura: filtro BCJ (en `.exe`/`.dll`), compresor y cifrado. Nada se
  carga entero en memoria, salvo con LZMA1, cuyo codificador trabaja sobre un búfer.
- **La firma se escribe al final.** Los primeros 32 bytes quedan en cero hasta que la cabecera está escrita. Si algo
  falla, el escritor queda inutilizable, no sella la firma y borra el archivo que creó: nunca queda un 7z incompleto
  con forma de válido.
- **Validación antes de escribir.** Las opciones se copian y se validan al crear el escritor (por ejemplo, cifrar
  nombres sin contraseña se rechaza). Cada nombre se normaliza y se valida antes de tocar el destino; uno inválido
  lanza `ArgumentException` y el escritor sigue sirviendo.
- **Concurrencia sin cambiar la salida.** Con varios hilos:
  - LZMA2 parte un archivo grande en bloques independientes que se codifican en paralelo y se escriben en orden;
  - sin modo sólido, varios archivos se comprimen a la vez, cada uno a un búfer propio, con un presupuesto de memoria
    y una cola acotada, y se escriben en el orden en que se agregaron.

  El resultado es idéntico byte a byte para cualquier cantidad de hilos. La única fuente de variación son la sal y los
  IV, que se generan al azar.
- **Cifrado.** La clave se deriva una vez por archivo, con una sal aleatoria de 16 bytes; cada folder y la cabecera
  cifrada llevan su propio IV aleatorio.
- **Sólido, apagado por defecto.** En un bloque sólido un byte dañado arruina lo que sigue en el bloque; sin modo
  sólido, el daño queda en un solo archivo.

---

## 4. Otros formatos

`ArchiveReader.TryOpen` reconoce el formato por su firma, no por la extensión, en este orden: 7z, XZ, BZip2, GZip,
formatos registrados (RAR), TAR, ISO 9660 y, por último, autoextraíbles de formatos registrados. Todos se recorren
igual: `MoveNext()` avanza y `CopyCurrentTo()` copia la entrada actual. Es lo único posible en un `.tar.gz`, que no
admite saltar.

| Formato | Implementación |
|---|---|
| 7z | Adaptador sobre `Archive7z`. |
| TAR | `TarReader` de .NET (v7, ustar, pax y GNU). Un comprimido suelto cuyo contenido empieza con una cabecera TAR se lee como `.tar.gz`, `.tar.bz2` o `.tar.xz`. |
| GZip | `GZipStream` de .NET, que verifica CRC32 y largo de cada miembro. |
| BZip2 | Decodificador propio detrás de un `ProducerStream`; controla el CRC de cada bloque y del stream. |
| XZ | Lector propio: recorre índices y pies desde el final para conocer los límites exactos de cada bloque, y decodifica con el LZMA2 y los filtros del motor. |
| ISO 9660 | Lector propio, con nombres Joliet. |
| RAR | En `PrZip.Rar`, mediante `ArchiveFormatProvider`. |

Lo que no es un archivo común (carpetas, enlaces, dispositivos, flujos NTFS) se informa en `ArchiveEntryInfo.Kind`
y no se crea. `BytesRead` y `ArchiveLength` dan el progreso aun sin conocer el tamaño descomprimido.

---

## 5. Archivos hostiles

El motor supone que todo archivo puede estar armado para dañar. Ante cualquier entrada solo lanza excepciones
propias (`PrZipException` y sus subclases), además de las de E/S del stream que se le pasa.

| Riesgo | Defensa |
|---|---|
| Cantidades y tamaños absurdos en la cabecera | `ReaderLimits`: cabecera de hasta 512 MB y validada contra el largo del archivo; hasta 4 cabeceras codificadas anidadas; hasta 64 coders por folder; cada cantidad acotada por los bytes que pueden describirla. |
| Diccionarios enormes en un archivo chico | Se reserva el mínimo entre lo declarado y lo que el coder va a producir, con tope de 1536 MB (LZMA) y 2 GB (PPMd). En RAR no sólido, la ventana se dimensiona por el tamaño del archivo. |
| Derivación de clave interminable | Tope de 2^24 vueltas, el mismo que acepta 7z.exe (FORMAT.md §5). |
| Bombas de descompresión en ISO | Se rechazan las imágenes cuyas entradas superpuestas suman más de 100 veces la imagen (mínimo 64 MB). |
| Rutas que salen del destino y nombres peligrosos | `EntryPathSanitizer`: `..` aborta (Zip Slip), salvo que quien extrae pida neutralizarlo (ForZip lo hace con evidencia de terceros, documentándolo); rutas absolutas y unidades se vuelven relativas; caracteres inválidos y nombres reservados de Windows se corrigen; un nombre de más de 255 caracteres se acorta con una huella del original; dos nombres que terminan iguales reciben `~2`. Todo cambio se informa. |
| Pisar o borrar archivos ajenos | La extracción nunca sobrescribe salvo pedido expreso (`overwrite`). Un archivo extraído que no pasa su CRC se borra en vez de quedar con el nombre del original. |

---

## 6. Cómo se verifica

| Tipo | Dónde | Qué asegura |
|---|---|---|
| Unitarios y de ida y vuelta | `tests/PrZip.Tests` | Cada códec, filtro y estructura; escribir y volver a leer. |
| Interoperabilidad | `*InteropTests`, `ArchiveWriterTests`, etc. | Lo que escribe 7-Zip, PrZip lo lee; lo que escribe PrZip, 7-Zip lo prueba y extrae. Necesitan `7z.exe` y, si no está, se saltean y figuran como omitidos. |
| Caja negra | `BlackBoxFormatTests` | Lo que el formato público no documenta (7zAES, parámetros por nivel) se estableció preguntándole a `7z.exe`; estos tests repiten cada caso y exigen que el motor coincida. |
| Entradas hostiles | `HardeningTests`, `WriterReviewTests`, `LongNameTests` y otros | Cabeceras malformadas, nombres hostiles, fallas a mitad de escritura. |
| Otros formatos | `Formats/` | Archivos de prueba armados con productores independientes del motor: Python (tarfile, gzip, bz2, lzma) y el `tar.exe` de Windows, más el corpus RAR de libarchive y archivos hechos con las herramientas de RARLAB (RAR 1.5 a 7). |
| Fuzzing | `tools/PrZip.Fuzz` | Archivos con bits alterados: ninguno debe colgar el lector ni lanzar algo que no sea `PrZipException`. |
| Batería con material real | `tools/interop/bateria.sh` | Varios modos de lectura y escritura contra `7z.exe`, comparando el árbol extraído con el original. |
| Licencias | `LicenseHeaderTests`, `ArchitectureTests` | Todo archivo declara su licencia, el código de UnRAR no sale de `PrZip.Rar` y el núcleo no lo referencia. |

**Regla de trabajo:** un test que se agrega por un error se corre primero contra el código viejo, para comprobar que
de verdad lo detecta.

---

## 7. Extender el motor

- **Un formato nuevo de lectura:** derivar de `ArchiveReader`. Si vive fuera del núcleo (por licencia, por ejemplo),
  exponerlo con un `ArchiveFormatProvider` y registrarlo con `ArchiveReader.RegisterFormat`, como hace `PrZip.Rar`.
- **Un códec o filtro de 7z:** agregar su ID en los encabezados y su construcción en `FolderDecoder`, como un stream
  que se lee a demanda (o un decodificador "push" detrás de `ProducerStream`). Para escribirlo, agregarlo a
  `FolderBuilder`.
- **En cualquier caso:** anotar el origen en PROVENANCE (nunca código de `CPP/` de 7-Zip; lo no documentado, por caja
  negra contra `7z.exe`), y sumar tests de ida y vuelta y de interoperabilidad.
