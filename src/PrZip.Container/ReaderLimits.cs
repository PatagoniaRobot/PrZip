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

namespace PrZip.Container;

/// <summary>
/// Topes que protegen la lectura frente a archivos hostiles: todo lo que el archivo declara
/// (tamaños, cantidades, diccionarios) se valida antes de reservar memoria o lanzar hilos.
/// Los valores cubren con holgura cualquier archivo que pueda crear 7-Zip.
/// </summary>
internal static class ReaderLimits
{
    /// <summary>Tamaño máximo de la cabecera, plana o ya decodificada (millones de entradas caben en mucho menos).</summary>
    public const long MaxHeaderSize = 512L << 20;

    /// <summary>Una cabecera codificada puede describir otra; más de unos pocos niveles es un ataque.</summary>
    public const int MaxEncodedHeaderDepth = 4;

    /// <summary>Diccionario LZMA/LZMA2 máximo: 1536 MB, el mayor que 7-Zip permite crear.</summary>
    public const long MaxDictionarySize = 1536L << 20;

    /// <summary>Memoria del modelo PPMd: 2 GB (7-Zip usa 192 MB en -mx9 y admite hasta unos pocos GB).</summary>
    public const long MaxPpmdMemory = 2048L << 20;

    /// <summary>
    /// Coders por folder y streams por coder: topes propios, muy por encima de lo que usa un folder real
    /// (a lo sumo cuatro coders: cifrado, compresor y dos filtros).
    /// </summary>
    public const int MaxCodersPerFolder = 64;
    public const int MaxStreamsPerCoder = 64;
}
