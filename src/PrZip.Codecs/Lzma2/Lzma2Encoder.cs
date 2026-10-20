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

using PrZip.Codecs.Lzma;

namespace PrZip.Codecs.Lzma2;

/// <summary>
/// Codificador LZMA2 siguiendo el esquema de Lzma2Enc.c (Igor Pavlov, dominio público).
///
/// La entrada se divide en bloques independientes (cada uno empieza con reset de diccionario), y
/// dentro de cada bloque corre un único flujo LZMA continuo que se corta en chunks de hasta 2 MB
/// descomprimidos y 64 KB comprimidos, sin reiniciar el modelo entre chunks. Un chunk que no
/// comprime se emite sin comprimir y el modelo vuelve al estado previo a ese chunk, tal como
/// lo deja el decodificador.
///
/// Con varios hilos, los bloques se codifican en paralelo y se escriben en orden: la salida es
/// idéntica byte a byte para cualquier cantidad de hilos.
/// </summary>
internal sealed class Lzma2Encoder
{
    private const int ChunkUnpackMax = 1 << 21; // 2 MB, límite del formato
    private const int ChunkPackMax = 1 << 16;   // 64 KB, límite del formato
    private const int CopyChunkMax = 1 << 16;   // chunk sin comprimir: hasta 64 KB


    private readonly uint _dictSize;
    private readonly int _threads;
    private readonly LzmaProperties _lzmaProps;
    private readonly byte _propByte;

    public LzmaMode Mode { get; set; } = LzmaMode.Fast;

    /// <summary>"fb" de 7-Zip (32 en niveles 1..6, 64 en 7..9).</summary>
    public int NumFastBytes { get; set; } = 32;

    /// <summary>
    /// Tamaño de bloque independiente. Por defecto 4 × diccionario, entre 1 MB y 256 MB (como 7-Zip).
    /// Bloques más chicos dan más paralelismo y menos memoria; bloques más grandes, mejor compresión.
    /// </summary>
    public int BlockSize { get; set; }

    public Lzma2Encoder(uint dictSize = 1048576, int threads = 1)
    {
        _dictSize = Math.Max(dictSize, 4096u);
        _threads = threads <= 0 ? Environment.ProcessorCount : threads;
        _lzmaProps = new LzmaProperties(3, 0, 2, _dictSize);
        _propByte = (byte)((_lzmaProps.Pb * 5 + _lzmaProps.Lp) * 9 + _lzmaProps.Lc); // 0x5D
        BlockSize = (int)Math.Clamp(4L * _dictSize, 1L << 20, 1L << 28);
    }

    /// <summary>
    /// Codifica datos desde 'inStream' hacia 'outStream' en formato LZMA2.
    /// </summary>
    public void Encode(Stream inStream, Stream outStream)
    {
        ArgumentNullException.ThrowIfNull(inStream);
        ArgumentNullException.ThrowIfNull(outStream);

        using var encoder = CreateEncoderStream(outStream);
        inStream.CopyTo(encoder, 1 << 16);
        encoder.Finish();
    }

    /// <summary>
    /// Stream de escritura que codifica en LZMA2 lo que recibe, bloque a bloque: la memoria retenida es
    /// la de los bloques en vuelo, no la de toda la entrada. Finish emite lo pendiente y el fin de stream.
    /// </summary>
    public EncoderStream CreateEncoderStream(Stream outStream)
    {
        ArgumentNullException.ThrowIfNull(outStream);
        return new EncoderStream(this, outStream);
    }

    public sealed class EncoderStream : Stream
    {
        private readonly Lzma2Encoder _owner;
        private readonly Stream _output;
        private readonly int _blockSize;
        private readonly int _inFlight;
        private readonly LzmaEncoder?[] _encoders;
        private readonly byte[]?[] _blocks;
        private readonly int[] _lengths;
        private readonly MemoryStream?[] _outputs;
        private int _current;
        private bool _finished;

        internal EncoderStream(Lzma2Encoder owner, Stream output)
        {
            _owner = owner;
            _output = output;
            _blockSize = Math.Max(owner.BlockSize, 1 << 16);

            // Bloques en vuelo: tantos como hilos, sin pasar de ~1 GB de entrada retenida.
            _inFlight = (int)Math.Clamp((1L << 30) / _blockSize, 1, owner._threads);
            _encoders = new LzmaEncoder?[_inFlight];
            _blocks = new byte[]?[_inFlight];
            _lengths = new int[_inFlight];
            _outputs = new MemoryStream?[_inFlight];
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
            while (!buffer.IsEmpty)
            {
                int length = _lengths[_current];
                if (length == _blockSize)
                {
                    _current++;
                    if (_current == _inFlight)
                    {
                        EncodeBatch(_inFlight);
                    }

                    continue;
                }

                // El búfer del bloque crece al doble a medida que llegan datos: una entrada chica no
                // reserva un bloque entero de decenas de MB.
                ref byte[]? block = ref _blocks[_current];
                if (block == null || block.Length == length)
                {
                    int newSize = block == null ? Math.Min(_blockSize, 1 << 16) : (int)Math.Min(_blockSize, (long)block.Length * 2);
                    Array.Resize(ref block, newSize);
                }

                int n = Math.Min(buffer.Length, block.Length - length);
                buffer[..n].CopyTo(block.AsSpan(length));
                _lengths[_current] = length + n;
                buffer = buffer[n..];
            }
        }

        private void EncodeBatch(int count)
        {
            if (count == 1)
            {
                EncodeSlot(0);
            }
            else if (count > 1)
            {
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = _owner._threads }, EncodeSlot);
            }

            for (int i = 0; i < count; i++)
            {
                _outputs[i]!.Position = 0;
                _outputs[i]!.CopyTo(_output);
                _lengths[i] = 0;
            }

            _current = 0;
        }

        private void EncodeSlot(int i)
        {
            _outputs[i] ??= new MemoryStream();
            var encoder = _owner.GetEncoder(_encoders, i, _lengths[i]);
            _owner.EncodeBlock(encoder, _blocks[i]!.AsSpan(0, _lengths[i]), _outputs[i]!);
        }

        /// <summary>
        /// Codifica los bloques pendientes y escribe el marcador de fin (0x00).
        /// </summary>
        public void Finish()
        {
            if (_finished)
            {
                return;
            }

            int count = _lengths[_current] > 0 ? _current + 1 : _current;
            EncodeBatch(count);
            _output.WriteByte(0x00);
            _finished = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var ms in _outputs)
                {
                    ms?.Dispose();
                }
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

    /// <summary>
    /// Un codificador por ranura, reutilizado entre bloques. Para entradas chicas el diccionario
    /// interno se recorta al tamaño del bloque: no tiene sentido reservar tablas de 64 MB para 10 KB.
    /// </summary>
    private LzmaEncoder GetEncoder(LzmaEncoder?[] encoders, int slot, int blockLength)
    {
        uint needed = (uint)Math.Clamp(blockLength, 4096, (long)_dictSize);
        var enc = encoders[slot];
        if (enc == null || enc.Properties.DictionarySize < needed)
        {
            enc = new LzmaEncoder(new LzmaProperties(_lzmaProps.Lc, _lzmaProps.Lp, _lzmaProps.Pb, needed))
            {
                Mode = Mode,
                NumFastBytes = NumFastBytes
            };
            encoders[slot] = enc;
        }

        return enc;
    }

    private void EncodeBlock(LzmaEncoder encoder, ReadOnlySpan<byte> block, MemoryStream output)
    {
        output.SetLength(0);
        encoder.BeginBlock();

        using var chunk = new MemoryStream(ChunkPackMax + 64);
        Span<byte> header = stackalloc byte[6];

        bool dictReset = true;      // el primer chunk del bloque reinicia el diccionario
        bool needProps = true;      // y el primer chunk LZMA debe declarar propiedades

        int pos = 0;
        while (pos < block.Length)
        {
            encoder.SaveState();
            chunk.SetLength(0);

            // El codificador corta el chunk con las reservas de LzmaEnc.c (kNumOpts + 300 de entrada,
            // 16 KB de salida), así que nunca pasa los límites del formato.
            int newPos = encoder.EncodeChunk(block, chunk, ChunkUnpackMax, ChunkPackMax);
            int unpack = newPos - pos;
            int pack = (int)chunk.Length;

            // Criterio de Lzma2Enc.c: si no ahorra al menos el encabezado, va sin comprimir.
            if (pack + 2 >= unpack || pack > ChunkPackMax)
            {
                encoder.RestoreState();
                for (int p = pos; p < newPos; p += CopyChunkMax)
                {
                    int len = Math.Min(CopyChunkMax, newPos - p);
                    output.WriteByte(dictReset ? (byte)0x01 : (byte)0x02);
                    output.WriteByte((byte)((len - 1) >> 8));
                    output.WriteByte((byte)(len - 1));
                    output.Write(block.Slice(p, len));
                    dictReset = false;
                }
            }
            else
            {
                // 0xE0: reset de diccionario + estado + propiedades (inicio de bloque)
                // 0xC0: estado + propiedades (tras un chunk sin comprimir que reinició el diccionario)
                // 0x80: sin reset, el modelo continúa
                int control = dictReset ? 0xE0 : needProps ? 0xC0 : 0x80;
                int u = unpack - 1;
                int c = pack - 1;
                header[0] = (byte)(control | (u >> 16));
                header[1] = (byte)(u >> 8);
                header[2] = (byte)u;
                header[3] = (byte)(c >> 8);
                header[4] = (byte)c;
                header[5] = _propByte;
                output.Write(header[..(control >= 0xC0 ? 6 : 5)]);
                chunk.Position = 0;
                chunk.CopyTo(output);

                dictReset = false;
                needProps = false;
            }

            pos = newPos;
        }
    }
}
