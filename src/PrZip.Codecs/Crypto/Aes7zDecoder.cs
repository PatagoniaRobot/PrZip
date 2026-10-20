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

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PrZip.Exceptions;

namespace PrZip.Codecs.Crypto;

/// <summary>
/// Decodificador para el códec de cifrado 7zAES (AES-256-CBC con KDF de SHA-256 por ciclos).
/// Cero dependencias externas: utiliza exclusivamente las primitivas criptográficas del BCL de .NET 10.
/// </summary>
internal sealed class Aes7zDecoder : IDisposable
{
    private readonly Aes7zProperties _properties;
    private readonly byte[] _key;
    private bool _disposed;

    public Aes7zDecoder(Aes7zProperties properties, ReadOnlySpan<char> password)
    {
        _properties = properties;
        _key = DeriveKey(password, properties.Salt, properties.NumCyclesPower);
    }

    /// <summary>Toma la clave del caché (lo habitual con muchos folders cifrados con la misma sal).</summary>
    public Aes7zDecoder(Aes7zProperties properties, Aes7zKeyCache keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _properties = properties;
        _key = keys.GetKey(properties.Salt, properties.NumCyclesPower);
    }

    /// <summary>
    /// Exponente máximo de la derivación: 2^24 vueltas (unas décimas de segundo). Es el tope observado en
    /// 7z.exe 24.08, que acepta 24 y rechaza 25 como método no soportado (docs/FORMAT.md §5,
    /// BlackBoxFormatTests). El valor viene del archivo: sin tope, un 7z armado con 62 pedía 2^62 vueltas y
    /// el lector quedaba calculando para siempre, sin que cancelar lo destrabara.
    /// </summary>
    public const int MaxNumCyclesPower = 24;

    /// <summary>Valor especial del formato: la clave es la sal más la contraseña, sin derivación.</summary>
    public const int RawKeyCyclesPower = 0x3F;

    /// <summary>
    /// Deriva la clave AES-256 (32 bytes) de la contraseña (UTF-16LE), la sal y el exponente (FORMAT.md §5):
    /// con 0x3F, la clave es la sal seguida de la contraseña, cortada o completada con ceros a 32 bytes; si no,
    /// es el SHA-256 de 2^n repeticiones de sal + contraseña + contador de 64 bits little-endian. Las dos
    /// formas se verifican contra 7z.exe en BlackBoxFormatTests.
    /// </summary>
    public static byte[] DeriveKey(ReadOnlySpan<char> password, byte[] salt, int numCyclesPower)
    {
        if (numCyclesPower < 0 || (numCyclesPower > MaxNumCyclesPower && numCyclesPower != RawKeyCyclesPower))
        {
            throw new UnsupportedMethodException(
                $"7zAES: derivación de clave con 2^{numCyclesPower} vueltas; el máximo admitido es 2^{MaxNumCyclesPower}, como en 7-Zip.");
        }

        if (numCyclesPower == RawKeyCyclesPower)
        {
            byte[] rawKey = new byte[32];
            int pos = 0;
            if (salt.Length > 0)
            {
                int copySalt = Math.Min(salt.Length, 32);
                salt.AsSpan(0, copySalt).CopyTo(rawKey);
                pos += copySalt;
            }

            if (pos < 32 && !password.IsEmpty)
            {
                ReadOnlySpan<byte> passBytes = MemoryMarshal.AsBytes(password);
                int copyPass = Math.Min(passBytes.Length, 32 - pos);
                passBytes.Slice(0, copyPass).CopyTo(rawKey.AsSpan(pos));
            }

            return rawKey;
        }

        ulong numRounds = 1UL << numCyclesPower;
        ReadOnlySpan<byte> passBytesRaw = MemoryMarshal.AsBytes(password);
        int cycleLen = salt.Length + passBytesRaw.Length + 8;

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Optimización por lotes: empaquetamos 512 ciclos en memoria para reducir llamadas al runtime
        const int batchSize = 512;
        byte[] batchBuffer = new byte[batchSize * cycleLen];

        // Pre-poblar los campos fijos (salt + password) en cada ciclo del lote
        for (int b = 0; b < batchSize; b++)
        {
            int baseOffset = b * cycleLen;
            if (salt.Length > 0)
            {
                salt.CopyTo(batchBuffer.AsSpan(baseOffset));
            }
            if (!passBytesRaw.IsEmpty)
            {
                passBytesRaw.CopyTo(batchBuffer.AsSpan(baseOffset + salt.Length));
            }
        }

        ulong currentRound = 0;
        while (currentRound < numRounds)
        {
            ulong remaining = numRounds - currentRound;
            int currentBatch = (int)Math.Min((ulong)batchSize, remaining);

            for (int b = 0; b < currentBatch; b++)
            {
                int counterOffset = b * cycleLen + salt.Length + passBytesRaw.Length;
                BinaryPrimitives.WriteUInt64LittleEndian(batchBuffer.AsSpan(counterOffset, 8), currentRound + (ulong)b);
            }

            sha256.AppendData(batchBuffer.AsSpan(0, currentBatch * cycleLen));
            currentRound += (ulong)currentBatch;
        }

        // Higiene criptográfica: limpiar buffer temporal
        CryptographicOperations.ZeroMemory(batchBuffer);

        byte[] key = sha256.GetHashAndReset();
        return key;
    }

    /// <summary>
    /// Descifra un flujo empaquetado cifrado con AES-256-CBC y escribe los datos en outStream.
    /// Recorta la salida al tamaño exacto de unpackSize.
    /// </summary>
    public void Decode(Stream inStream, Stream outStream, long unpackSize)
    {
        ArgumentNullException.ThrowIfNull(inStream);
        ArgumentNullException.ThrowIfNull(outStream);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _properties.Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();

        byte[] inBuffer = new byte[65536];
        byte[] outBuffer = new byte[65536];
        long written = 0;

        while (unpackSize < 0 || written < unpackSize)
        {
            // Un Read puede devolver menos de lo pedido sin estar en el final: se completa el
            // buffer antes de exigir la alineación a bloques de 16 bytes.
            int read = inStream.ReadAtLeast(inBuffer, inBuffer.Length, throwOnEndOfStream: false);
            if (read == 0) break;

            if (read % 16 != 0)
            {
                throw new CorruptArchiveException($"Flujo de datos AES-256 con bloque incompleto no alineado a 16 bytes: {read} bytes.");
            }

            int decrypted = decryptor.TransformBlock(inBuffer, 0, read, outBuffer, 0);

            int toWrite = unpackSize < 0 ? decrypted : (int)Math.Min((long)decrypted, unpackSize - written);
            outStream.Write(outBuffer, 0, toWrite);
            written += toWrite;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
        }
    }
}
