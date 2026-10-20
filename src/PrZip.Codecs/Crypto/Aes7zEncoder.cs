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
/// Codificador y cifrador para 7zAES (AES-256-CBC con KDF de SHA-256 de 2^19 ciclos).
/// Cero dependencias externas: utiliza exclusivamente las primitivas criptográficas del BCL de .NET 10.
/// </summary>
internal sealed class Aes7zEncoder : IDisposable
{
    private readonly byte[] _key;
    private readonly byte[] _salt;
    private readonly byte[] _iv;
    private readonly int _numCyclesPower;
    private bool _disposed;

    public byte[] Salt => _salt;
    public byte[] Iv => _iv;
    public int NumCyclesPower => _numCyclesPower;

    public Aes7zEncoder(ReadOnlySpan<char> password, int numCyclesPower = 19)
    {
        _numCyclesPower = numCyclesPower;
        _salt = new byte[16];
        _iv = new byte[16];

        RandomNumberGenerator.Fill(_salt);
        RandomNumberGenerator.Fill(_iv);

        _key = Aes7zDecoder.DeriveKey(password, _salt, _numCyclesPower);
    }

    private Aes7zEncoder(byte[] key, byte[] salt, int numCyclesPower)
    {
        _numCyclesPower = numCyclesPower;
        _salt = (byte[])salt.Clone();
        _key = (byte[])key.Clone();
        _iv = new byte[16];
        RandomNumberGenerator.Fill(_iv);
    }

    /// <summary>
    /// Otro codificador con la misma sal y clave y un IV aleatorio nuevo, sin volver a derivar. Es lo que
    /// hace 7-Zip entre folders: en CBC la clave puede repetirse mientras el IV no se repita.
    /// </summary>
    public Aes7zEncoder WithNewIv()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Aes7zEncoder(_key, _salt, _numCyclesPower);
    }

    /// <summary>
    /// Devuelve los 34 bytes canónicos de propiedades de 7zAES listos para almacenar en CoderInfo.
    /// </summary>
    public byte[] GetProperties()
    {
        byte[] props = new byte[34];
        props[0] = (byte)(0xC0 | (_numCyclesPower & 0x3F)); // 0xD3: Salt + IV presentes, 19 ciclos
        props[1] = 0xFF; // (15 << 4) | 15 -> SaltSize = 16, IvSize = 16
        _salt.CopyTo(props.AsSpan(2, 16));
        _iv.CopyTo(props.AsSpan(18, 16));
        return props;
    }

    /// <summary>
    /// Cifra los datos de 'inStream' hacia 'outStream' utilizando AES-256-CBC con PaddingMode.None,
    /// rellenando el último bloque con ceros hasta alinear a 16 bytes.
    /// Devuelve el tamaño total cifrado escrito.
    /// </summary>
    public long Encode(Stream inStream, Stream outStream)
    {
        ArgumentNullException.ThrowIfNull(inStream);
        ArgumentNullException.ThrowIfNull(outStream);

        using var encrypt = CreateEncryptStream(outStream);
        inStream.CopyTo(encrypt);
        encrypt.Finish();
        return encrypt.BytesWritten;
    }

    /// <summary>
    /// Stream de escritura que cifra en AES-256-CBC lo que recibe. Solo el último bloque se rellena con
    /// ceros (al llamar Finish): una escritura de tamaño arbitrario no introduce relleno intermedio.
    /// </summary>
    public AesEncryptStream CreateEncryptStream(Stream outStream)
    {
        ArgumentNullException.ThrowIfNull(outStream);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new AesEncryptStream(_key, _iv, outStream);
    }

    public sealed class AesEncryptStream : Stream
    {
        private readonly Aes _aes;
        private readonly ICryptoTransform _encryptor;
        private readonly Stream _output;
        private readonly byte[] _buffer = new byte[1 << 16];
        private readonly byte[] _encrypted = new byte[1 << 16];
        private int _count;
        private bool _finished;

        internal AesEncryptStream(byte[] key, byte[] iv, Stream output)
        {
            _aes = Aes.Create();
            _aes.Key = key;
            _aes.IV = iv;
            _aes.Mode = CipherMode.CBC;
            _aes.Padding = PaddingMode.None;
            _encryptor = _aes.CreateEncryptor();
            _output = output;
        }

        public long BytesWritten { get; private set; }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            while (!buffer.IsEmpty)
            {
                int n = Math.Min(buffer.Length, _buffer.Length - _count);
                buffer[..n].CopyTo(_buffer.AsSpan(_count));
                _count += n;
                buffer = buffer[n..];

                if (_count == _buffer.Length)
                {
                    EncryptBuffered(_count);
                }
            }
        }

        private void EncryptBuffered(int length)
        {
            int done = _encryptor.TransformBlock(_buffer, 0, length, _encrypted, 0);
            _output.Write(_encrypted, 0, done);
            BytesWritten += done;
            _count = 0;
        }

        /// <summary>
        /// Rellena con ceros hasta un múltiplo de 16 y cifra lo pendiente.
        /// </summary>
        public void Finish()
        {
            if (_finished)
            {
                return;
            }

            int remainder = _count % 16;
            if (remainder != 0)
            {
                int pad = 16 - remainder;
                Array.Clear(_buffer, _count, pad);
                _count += pad;
            }

            if (_count > 0)
            {
                EncryptBuffered(_count);
            }

            _finished = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _encryptor.Dispose();
                _aes.Dispose();
            }

            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
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
