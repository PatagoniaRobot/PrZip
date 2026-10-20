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

namespace PrZip;

/// <summary>
/// Métodos de compresión soportados para el empaquetado en 7z.
/// </summary>
public enum CompressionMethod
{
    /// <summary>
    /// Sin compresión (Copy / almacén directo).
    /// </summary>
    Copy = 0,

    /// <summary>
    /// Algoritmo LZMA2 estándar.
    /// </summary>
    Lzma2 = 1,

    /// <summary>
    /// Algoritmo LZMA1 clásico.
    /// </summary>
    Lzma = 2,

    /// <summary>
    /// PPMd var.H: suele comprimir texto mejor que LZMA2, pero descomprime a la misma velocidad que
    /// comprime (lento) y no aprovecha varios hilos.
    /// </summary>
    Ppmd = 3,

    /// <summary>
    /// BZip2 (BWT): bueno con texto, más lento que LZMA2. Los bloques se comprimen en paralelo.
    /// Niveles 1–2: bloques de 100 KB; 3–4: 500 KB; 5–9: 900 KB.
    /// </summary>
    BZip2 = 4,

    /// <summary>
    /// Deflate (el del ZIP): rápido y universal, comprime menos que LZMA2. Un hilo por archivo.
    /// </summary>
    Deflate = 5
}

/// <summary>
/// Opciones de configuración para la creación y escritura de archivos 7z.
/// </summary>
public sealed class WriterOptions7z
{
    /// <summary>
    /// Método de compresión a utilizar (por defecto LZMA2).
    /// </summary>
    public CompressionMethod Method { get; set; } = CompressionMethod.Lzma2;

    /// <summary>
    /// Nivel de compresión (0 = Copy, 1..4 = Rápido, 5..9 = Normal/Ultra).
    /// </summary>
    public int Level { get; set; } = 5;

    /// <summary>
    /// Contraseña opcional para cifrado AES-256. Null no cifra; una vacía se rechaza al crear el escritor.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Si es true, cifra también los nombres y metadatos del encabezado (-mhe=on).
    /// Requiere Password: sin ella, crear el escritor falla en vez de dejar los nombres a la vista.
    /// </summary>
    public bool EncryptHeaders { get; set; }

    /// <summary>
    /// Tamaño del diccionario en bytes (por defecto 0 = automático según el nivel).
    /// </summary>
    public uint DictionarySize { get; set; }

    /// <summary>
    /// Si es true, detecta y aplica filtros ejecutables BCJ automáticamente en binarios (.exe, .dll).
    /// </summary>
    public bool AutoDetectFilters { get; set; } = true;

    /// <summary>
    /// Si es true, preserva abierto el stream de destino al hacer Dispose.
    /// </summary>
    public bool LeaveStreamOpen { get; set; }

    /// <summary>
    /// Número de hilos para compresión multihilo.
    /// 1 = monohilo (predeterminado), 0 = automático (Environment.ProcessorCount), > 1 = número explícito de hilos.
    /// Con varios hilos, LZMA2 divide en bloques los archivos grandes, y sin modo sólido los archivos
    /// agregados por ruta se comprimen varios a la vez. La salida es la misma para cualquier cantidad de hilos.
    /// </summary>
    public int Threads { get; set; } = 1;

    /// <summary>
    /// Si es true, las entradas se comprimen juntas en bloques sólidos (como 7-Zip -ms=on): mucho mejor
    /// relación con muchos archivos chicos, pero un byte dañado afecta a todo el resto de su bloque.
    /// Los ejecutables (.exe/.dll con AutoDetectFilters) van siempre en folders propios con BCJ.
    /// </summary>
    public bool Solid { get; set; }

    /// <summary>
    /// Tamaño máximo descomprimido de un bloque sólido; al superarlo se abre uno nuevo (acota el daño
    /// de una corrupción y la memoria del lector). Por defecto 1 GB.
    /// </summary>
    public long SolidBlockSize { get; set; } = 1L << 30;

    internal WriterOptions7z WithThreads(int threads)
    {
        var copy = Clone();
        copy.Threads = threads;
        return copy;
    }

    internal WriterOptions7z Clone() => (WriterOptions7z)MemberwiseClone();
}
