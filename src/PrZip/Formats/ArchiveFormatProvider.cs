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

namespace PrZip.Formats;

/// <summary>
/// Formato que otro ensamblado suma a los que reconoce <see cref="ArchiveReader"/>. El núcleo no conoce RAR: su
/// lector vive en PrZip.Rar, con la licencia de unrar, y se registra con <c>RarSupport.Register()</c>. Así,
/// quien usa solo el núcleo no lleva código con otra licencia.
/// </summary>
public abstract class ArchiveFormatProvider
{
    /// <summary>Nombre para mostrar ("rar").</summary>
    public abstract string Name { get; }

    /// <summary>Reconoce el formato por los primeros bytes del archivo (hasta 512).</summary>
    public abstract bool MatchesHeader(ReadOnlySpan<byte> head);

    /// <summary>
    /// Segunda oportunidad, después de los formatos del núcleo: puede leer más del archivo (un autoextraíble
    /// guarda el archivo comprimido después del ejecutable). No cierra <paramref name="file"/>.
    /// </summary>
    public virtual bool MatchesFile(ReadOnlySpan<byte> head, Stream file) => false;

    /// <summary>
    /// Abre el archivo. <paramref name="openFile"/> abre ese archivo u otro (el volumen siguiente) para leer con
    /// posicionamiento, contando lo leído para el progreso del lector.
    /// </summary>
    public abstract ArchiveReader Open(string path, string? password, Func<string, Stream> openFile);
}
