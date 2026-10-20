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

using System.Text;
using PrZip.Codecs.Crypto;
using Xunit;

namespace PrZip.Tests;

public class Aes7zEncoderTests
{
    [Theory]
    [InlineData("clave123", "Hola mundo cifrado con AES-256 en 7z")]
    [InlineData("password_muy_segura_!@#$", "Patagonia Robot forense de alta fidelidad")]
    [InlineData("1", "A")] // Texto de 1 solo byte para forzar padding de 15 ceros
    [InlineData("abc", "1234567890123456")] // Exactamente 16 bytes (1 bloque exacto)
    public void AesEncoder_AesDecoder_RoundTrip_Success(string password, string plaintext)
    {
        byte[] original = Encoding.UTF8.GetBytes(plaintext);

        using var encoder = new Aes7zEncoder(password.AsSpan(), numCyclesPower: 12); // 12 ciclos para pruebas ultra rápidas
        byte[] propsBytes = encoder.GetProperties();

        using var inMs = new MemoryStream(original);
        using var encryptedMs = new MemoryStream();

        long encryptedSize = encoder.Encode(inMs, encryptedMs);
        Assert.Equal(0, encryptedSize % 16);

        encryptedMs.Position = 0;
        using var decryptedMs = new MemoryStream();

        var props = Aes7zProperties.Decode(propsBytes);
        using var decoder = new Aes7zDecoder(props, password.AsSpan());
        decoder.Decode(encryptedMs, decryptedMs, original.Length);

        byte[] result = decryptedMs.ToArray();
        Assert.Equal(original, result);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(40)]
    [InlineData(62)]
    public void DeriveKey_CyclesAbove7zExeMaximum_AreRejectedAtOnce(int power)
    {
        // Revisión de lectores 2026-10-13: 2^62 vueltas colgaban el lector para siempre. 7-Zip admite hasta 2^24.
        var props = new Aes7zProperties(power, new byte[8], new byte[16]);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Throws<PrZip.Exceptions.UnsupportedMethodException>(() => new Aes7zDecoder(props, "clave".AsSpan()));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(0x3F)]
    public void DeriveKey_UpToTheMaximumAndRawKey_StillWork(int power)
    {
        var key = Aes7zDecoder.DeriveKey("clave".AsSpan(), new byte[8], power);
        Assert.Equal(32, key.Length);
    }
}
