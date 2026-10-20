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

using PrZip.Exceptions;

namespace PrZip.Codecs.Crypto;

/// <summary>
/// Propiedades del método de cifrado 7zAES (ID 06 F1 07 01): exponente de vueltas de la derivación, sal e IV.
/// La disposición se estableció por pruebas de caja negra contra 7z.exe 24.08 (docs/FORMAT.md §5); los casos
/// están en BlackBoxFormatTests, que comparan lo que acepta 7z.exe con lo que acepta este lector.
/// </summary>
internal readonly struct Aes7zProperties
{
    /// <summary>Exponente de vueltas de la derivación (2^n). 0x3F: la clave es la sal más la contraseña.</summary>
    public int NumCyclesPower { get; }

    /// <summary>Sal de la derivación de clave (puede estar vacía).</summary>
    public byte[] Salt { get; }

    /// <summary>IV de AES-256-CBC, siempre de 16 bytes: el guardado, completado con ceros.</summary>
    public byte[] Iv { get; }

    public Aes7zProperties(int numCyclesPower, byte[] salt, byte[] iv)
    {
        NumCyclesPower = numCyclesPower;
        Salt = salt;
        Iv = iv;
    }

    /// <summary>
    /// Lee las propiedades según lo observado (FORMAT.md §5):
    /// <list type="bullet">
    /// <item>Vacías: 2^0 vueltas, sin sal, IV en ceros.</item>
    /// <item>Primer byte: bits 0–5, el exponente; bit 7, "la sal tiene uno más"; bit 6, "el IV tiene uno más".</item>
    /// <item>Con los bits 7 y 6 en cero, las propiedades son ese único byte.</item>
    /// <item>Si no, un segundo byte: nibble alto, sal; nibble bajo, IV (cada uno más su bit). Siguen la sal y
    /// el IV, y nada más.</item>
    /// </list>
    /// </summary>
    public static Aes7zProperties Decode(ReadOnlySpan<byte> properties)
    {
        if (properties.IsEmpty)
        {
            return new Aes7zProperties(0, [], new byte[16]);
        }

        byte flags = properties[0];
        int exponent = flags & 0x3F;
        int saltExtra = flags >> 7;
        int ivExtra = (flags >> 6) & 1;

        if (saltExtra == 0 && ivExtra == 0)
        {
            if (properties.Length != 1)
            {
                throw new CorruptArchiveException(
                    $"Propiedades 7zAES inválidas: sin sal ni IV declarados deberían ocupar 1 byte y ocupan {properties.Length}.");
            }

            return new Aes7zProperties(exponent, [], new byte[16]);
        }

        if (properties.Length < 2)
        {
            throw new CorruptArchiveException("Propiedades 7zAES truncadas: falta el byte con los tamaños de sal e IV.");
        }

        int saltLength = saltExtra + (properties[1] >> 4);
        int ivLength = ivExtra + (properties[1] & 0x0F);
        int expected = 2 + saltLength + ivLength;
        if (properties.Length != expected)
        {
            throw new CorruptArchiveException(
                $"Propiedades 7zAES inválidas: declaran {saltLength} bytes de sal y {ivLength} de IV ({expected} en total) y ocupan {properties.Length}.");
        }

        ReadOnlySpan<byte> rest = properties[2..];
        byte[] salt = rest[..saltLength].ToArray();
        byte[] iv = new byte[16];
        rest.Slice(saltLength, ivLength).CopyTo(iv);
        return new Aes7zProperties(exponent, salt, iv);
    }
}
