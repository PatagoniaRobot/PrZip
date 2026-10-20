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

// Código propio, no derivado de unrar: engancha el lector RAR de este ensamblado al núcleo.

using PrZip.Formats;

namespace PrZip.Rar;

/// <summary>
/// Suma RAR a los formatos que abre <see cref="ArchiveReader"/>. El núcleo no conoce RAR: quien lo quiera
/// referencia este ensamblado (licencia de unrar, ver LICENSE-UNRAR.txt) y llama a <see cref="Register"/> una
/// vez, al arrancar.
/// </summary>
public static class RarSupport
{
    /// <summary>Registra el formato RAR. Llamarlo más de una vez no tiene efecto.</summary>
    public static void Register() => ArchiveReader.RegisterFormat(new RarFormatProvider());

    private sealed class RarFormatProvider : ArchiveFormatProvider
    {
        public override string Name => "rar";

        public override bool MatchesHeader(ReadOnlySpan<byte> head) => RarArchive.HasSignature(head);

        /// <summary>Autoextraíble: un ejecutable ("MZ") con la firma RAR en sus primeros 4 MB.</summary>
        public override bool MatchesFile(ReadOnlySpan<byte> head, Stream file) =>
            head.Length >= 2 && head[0] == 'M' && head[1] == 'Z' && RarArchive.FindSignature(file);

        public override ArchiveReader Open(string path, string? password, Func<string, Stream> openFile) =>
            new RarArchiveReader(openFile(path), password, path, openFile);
    }
}
