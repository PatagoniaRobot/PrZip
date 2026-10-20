# Especificación Técnica del Formato 7z (FORMAT.md)

Este documento detalla la estructura binaria del formato de archivo **7z**, tal como se implementa en esta librería.

---

## 1. Disposición General del Archivo (Binary Layout)

Un archivo `.7z` se divide en tres áreas fundamentales:

```
+------------------------------------+ (Offset 0)
| Signature Header (32 bytes fijos)  |
+------------------------------------+ (Offset 32)
| Streams de Datos Empaquetados      |
| (Comprimidos con LZMA2/BCJ/AES...) |
+------------------------------------+ (Offset NextHeaderOffset + 32)
| NextHeader (Cabeceras 7z)          |
| (kHeader o kEncodedHeader)         |
+------------------------------------+ Fin de archivo
```

---

## 2. Signature Header (32 bytes)

Todo archivo 7z comienza con una cabecera de 32 bytes estructurada de la siguiente manera:

| Offset | Tamaño | Tipo | Descripción |
| :--- | :--- | :--- | :--- |
| `0x00` | 6 bytes | `byte[]` | Magic Bytes: `0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C` (`7z¼¯'\x1c`) |
| `0x06` | 1 byte | `byte` | Versión Mayor (`kMajorVersion = 0`) |
| `0x07` | 1 byte | `byte` | Versión Menor (`kMinorVersion = 4` al escribir) |
| `0x08` | 4 bytes | `uint32` | CRC-32 (IEEE) de los siguientes 20 bytes (offset `0x0C` a `0x1F`) |
| `0x0C` | 8 bytes | `uint64` | `NextHeaderOffset` (relativo al byte 32) |
| `0x14` | 8 bytes | `uint64` | `NextHeaderSize` (longitud en bytes del bloque de cabecera) |
| `0x1C` | 4 bytes | `uint32` | CRC-32 (IEEE) del contenido del bloque `NextHeader` |

---

## 3. Enteros de Longitud Variable (7z VarInt UInt64)

Los tamaños y contadores en 7z se serializan con una codificación compacta de 1 a 9 bytes:
* Se examina el primer byte. La cantidad de bits en `1` más significativos consecutivos determina cuántos bytes extra siguen (0 a 8).
* Si el primer bit es `0`, el valor es directamente el byte (0 a 127).
* Si hay $N$ unos iniciales seguidos de un `0`, siguen $N$ bytes adicionales en orden little-endian.

---

## 4. Property IDs de Cabeceras (TLV)

Las secciones de cabecera no tienen offsets estáticos, sino que se parsean mediante identificadores de propiedad:

| ID | Nombre | Descripción |
| :--- | :--- | :--- |
| `0x00` | `kEnd` | Fin de sección / marcador de terminación |
| `0x01` | `kHeader` | Cabecera principal plana |
| `0x04` | `kMainStreamsInfo` | Define empaquetado, carpetas y desempaquetado de datos |
| `0x05` | `kFilesInfo` | Metadatos de archivos (nombres, fechas, atributos) |
| `0x06` | `kPackInfo` | Tamaños de streams empaquetados y sus CRCs |
| `0x07` | `kUnPackInfo` | Lista de *Folders* y *Coders* |
| `0x08` | `kSubStreamsInfo` | Sub-división de carpetas sólidas en archivos individuales |
| `0x0B` | `kFolder` | Estructura de coders y *BindPairs* dentro de un folder |
| `0x0E` | `kEmptyStream` | Bitmap de archivos que no consumen espacio en streams |
| `0x0F` | `kEmptyFile` | Bitmap de archivos de 0 bytes (frente a directorios) |
| `0x10` | `kAnti` | Bitmap de entradas marcadas para borrado (sincronización) |
| `0x11` | `kNames` | Nombres de archivos en UTF-16LE terminados en nulo |
| `0x14` | `kMTime` | Marcas de tiempo de modificación (FILETIME de 100 ns) |
| `0x15` | `kWinAttributes` | Atributos de archivo Win32 / POSIX extendido |
| `0x17` | `kEncodedHeader` | Indica que la cabecera real está comprimida/cifrada en un Folder |

---

## 5. Cifrado 7zAES (método `06 F1 07 01`): propiedades y clave

**Fuente:** pruebas de caja negra contra el ejecutable oficial **7z.exe 24.08** (2026-10-13), no su código.
Cada caso se arma a mano (un 7z con un archivo cifrado sobre Copy y las propiedades elegidas) y se le pide a
7z.exe que lo pruebe (`7z t`). Los casos están en `tests/PrZip.Tests/BlackBoxFormatTests.cs`, que además
exigen que el lector del motor llegue al mismo resultado que 7z.exe.

### 5.1 Propiedades

| Propiedades | Resultado en 7z.exe |
| :--- | :--- |
| Vacías | Acepta: 2^0 vueltas, sin sal, IV en ceros. |
| Un byte con los bits 7 y 6 en cero | Acepta: bits 0–5 = exponente; sin sal, IV en ceros. |
| Bits 7 y 6 en cero y un segundo byte | Rechaza ("Unsupported Method"), cualquiera sea el segundo byte. |
| Bit 7 o bit 6 encendido | Lee un segundo byte: sal = bit 7 + nibble alto; IV = bit 6 + nibble bajo. Siguen la sal y el IV. |
| Menos bytes que los declarados | Rechaza. |
| Un byte de más al final | Rechaza. |

Se probaron sales e IV de 0, 1, 8, 15 y 16 bytes con todas las formas de escribir cada tamaño (bit 0 +
nibble n, o bit 1 + nibble n−1): todas las que tienen algún bit encendido descifran bien; un IV de menos de
16 bytes se completa con ceros.

Lo que escribe 7z.exe con `-p`: `53 0F` + 16 bytes de IV (2^19 vueltas, sin sal, IV de 16).

### 5.2 Exponente de vueltas

| Exponente | Resultado en 7z.exe |
| :--- | :--- |
| 0, 19, 24 | Acepta (con la clave derivada como en 5.3). |
| 25, 30, 62 | Rechaza ("Unsupported Method") antes de derivar. El motor usa el mismo tope. |
| `0x3F` | Acepta con la clave cruda de 5.3. |

### 5.3 Clave

- **Exponente n ≤ 24:** SHA-256 de 2^n repeticiones de `sal ‖ contraseña (UTF-16LE) ‖ contador`, con el
  contador de 64 bits little-endian que empieza en 0.
- **Exponente `0x3F`:** `sal ‖ contraseña (UTF-16LE)`, cortada o completada con ceros hasta 32 bytes.

Con esa clave, AES-256-CBC sin relleno; el último bloque se completa con ceros y el tamaño real viaja en la
cabecera. Las dos formas descifran en 7z.exe los archivos armados por los tests (5.2).

---

## 6. Parámetros por nivel observados en 7z.exe 24.08

Lo que escribe 7z.exe para `-mx=1..9` (texto de unos 500 KB), leído de su salida:

| Nivel | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| PPMd, orden (`-m0=PPMd`) | 4 | 4 | 5 | 5 | 6 | 8 | 16 | 24 | 32 |
| PPMd, memoria | 2^20 | 2^21 | 2^22 | 2^23 | 2^24 | 2^24 | 2^24 | 2^24 | 2^24 |
| BZip2, cabecera (`-m0=BZip2`) | BZh1 | BZh3 | BZh5 | BZh7 | BZh9 | BZh9 | BZh9 | BZh9 | BZh9 |

El motor usa los mismos órdenes de PPMd. La memoria de PPMd (2^(19+nivel)) y el bloque de BZip2 (1, 5 o 9)
son elección propia: cualquier valor dentro del formato es válido para cualquier lector.
