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


using Xunit;

namespace PrZip.Tests.Infrastructure;

/// <summary>
/// [Fact] que se saltea si no está el 7z.exe oficial: las pruebas de interoperabilidad no pueden fallar en una
/// máquina (o un CI) sin 7-Zip. Un salteado se ve en el resumen ("Omitido"), no pasa en silencio.
/// </summary>
public sealed class Requires7zFactAttribute : FactAttribute
{
    public Requires7zFactAttribute()
    {
        if (!Official7zFinder.IsAvailable)
        {
            Skip = Requires7z.SkipReason;
        }
    }
}

/// <summary>[Theory] que se saltea si no está el 7z.exe oficial (ver <see cref="Requires7zFactAttribute"/>).</summary>
public sealed class Requires7zTheoryAttribute : TheoryAttribute
{
    public Requires7zTheoryAttribute()
    {
        if (!Official7zFinder.IsAvailable)
        {
            Skip = Requires7z.SkipReason;
        }
    }
}

internal static class Requires7z
{
    public const string SkipReason =
        "Necesita el 7z.exe oficial (PATH, Program Files u OFFICIAL_7Z_EXE) para comparar contra 7-Zip.";
}
