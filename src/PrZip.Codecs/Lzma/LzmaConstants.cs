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

namespace PrZip.Codecs.Lzma;

/// <summary>
/// Constantes y tablas del modelo LZMA según la implementación canónica de Igor Pavlov (LzmaDec.c).
/// </summary>
internal static class LzmaConstants
{
    public const int NumBitModelTotalBits = 11;
    public const uint BitModelTotal = 1u << NumBitModelTotalBits; // 2048
    public const int NumMoveBits = 5;
    public const uint TopValue = 1u << 24;

    public const int NumStates = 12;

    public const int NumPosSlotBits = 6;
    public const int NumLenToPosStates = 4;
    public const int NumAlignBits = 4;
    public const int AlignTableSize = 1 << NumAlignBits; // 16

    public const int StartPosModelIndex = 4;
    public const int EndPosModelIndex = 14;
    public const int NumPosModels = EndPosModelIndex - StartPosModelIndex; // 10
    public const int NumFullDistances = 1 << (EndPosModelIndex >> 1); // 128

    // Rango de longitudes de coincidencia (Match Lengths): 2 a 273
    public const int MatchMinLen = 2;

    /// <summary>
    /// Máximo de coincidencias que un match finder puede reportar en una posición: cada una es
    /// estrictamente más larga que la anterior, entre MatchMinLen y 273.
    /// </summary>
    public const int MaxMatchesPerPosition = 273 - MatchMinLen + 1;
    public const int NumLenBits = 3;
    public const int NumLowLenBits = 3;
    public const int NumMidLenBits = 3;
    public const int NumHighLenBits = 8;
    public const int NumLowLenSymbols = 1 << NumLowLenBits; // 8
    public const int NumMidLenSymbols = 1 << NumMidLenBits; // 8
    public const int NumHighLenSymbols = 1 << NumHighLenBits; // 256
    public const int NumLenSymbols = NumLowLenSymbols + NumMidLenSymbols + NumHighLenSymbols; // 272

    // Tabla canónica de transición de estados tras un literal
    public static readonly byte[] LiteralNextStates = [0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 4, 5];

    // Distancia especial para marcador de fin de flujo (EOS)
    public const uint EndOfStreamDistance = 0xFFFFFFFFu;
}
