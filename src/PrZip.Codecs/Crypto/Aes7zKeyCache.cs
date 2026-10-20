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

using System.Security.Cryptography;

namespace PrZip.Codecs.Crypto;

/// <summary>
/// Claves 7zAES ya derivadas para una contraseña, por sal y cantidad de ciclos. Derivar cuesta 2^19
/// rondas de SHA-256 (decenas de ms) y un archivo sin modo sólido tiene un folder cifrado por entrada,
/// casi siempre con la misma sal: 7-Zip escribe sal vacía, y este escritor una sola sal por archivo.
/// Vive lo que el archivo abierto; Dispose borra las claves de la memoria.
/// </summary>
internal sealed class Aes7zKeyCache : IDisposable
{
    private readonly string _password;
    private readonly List<(byte[] Salt, int Cycles, byte[] Key)> _keys = [];
    private readonly Lock _lock = new();
    private bool _disposed;

    public Aes7zKeyCache(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        _password = password;
    }

    /// <summary>Cantidad de derivaciones hechas (las demás consultas salieron del caché).</summary>
    public int Derivations { get; private set; }

    /// <summary>
    /// Devuelve una copia de la clave: el que la recibe puede borrarla al terminar sin afectar al caché.
    /// </summary>
    public byte[] GetKey(byte[] salt, int numCyclesPower)
    {
        ArgumentNullException.ThrowIfNull(salt);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var (s, cycles, key) in _keys)
            {
                if (cycles == numCyclesPower && s.AsSpan().SequenceEqual(salt))
                {
                    return (byte[])key.Clone();
                }
            }

            // Se deriva dentro del lock: varios hilos que piden la misma clave esperan a la primera
            // derivación en lugar de repetirla.
            byte[] derived = Aes7zDecoder.DeriveKey(_password.AsSpan(), salt, numCyclesPower);
            _keys.Add(((byte[])salt.Clone(), numCyclesPower, derived));
            Derivations++;
            return (byte[])derived.Clone();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var (_, _, key) in _keys)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            _keys.Clear();
            _disposed = true;
        }
    }
}
