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
using PrZip.Codecs.BZip2;
using PrZip.Codecs.Crypto;
using PrZip.Codecs.Deflate;
using PrZip.Codecs.Filters;
using PrZip.Codecs.Lzma;
using PrZip.Codecs.Lzma2;
using PrZip.Codecs.Ppmd;
using PrZip.Container.Headers;
using PrZip.Primitives.Checksum;

namespace PrZip;

/// <summary>
/// Arma un folder 7z escribiendo en streaming hacia el destino:
/// datos → [BCJ x86] → compresor (Copy / LZMA / LZMA2) → [AES-256] → destino.
/// Puede recibir varias entradas seguidas (folder sólido); de cada una registra tamaño y CRC.
/// Nada se carga entero en memoria salvo con LZMA1, cuyo codificador trabaja sobre un búfer.
/// </summary>
internal sealed class FolderBuilder : IDisposable
{
    private readonly WriterOptions7z _options;
    private readonly uint _dictSize;
    private readonly bool _useBcj;

    private readonly CountingStream _packCounter;
    private readonly Aes7zEncoder? _aesEncoder;
    private readonly Aes7zEncoder.AesEncryptStream? _aesStream;
    private readonly CountingStream? _compressedCounter;
    private readonly Lzma2Encoder.EncoderStream? _lzma2Stream;
    private readonly MemoryStream? _lzmaBuffer;
    private readonly Ppmd7EncoderStream? _ppmdStream;
    private readonly BZip2EncoderStream? _bzip2Stream;
    private readonly DeflateEncoderStream? _deflateStream;
    private readonly Stream _compressorInput;
    private readonly BcjEncodeStream? _bcjStream;
    private readonly Stream _head;

    private readonly List<long> _sizes = [];
    private readonly List<uint> _crcs = [];
    private bool _finished;

    /// <param name="aes">Cifrador del folder (con su propio IV); el builder pasa a ser su dueño.</param>
    public FolderBuilder(WriterOptions7z options, uint dictSize, bool useBcj, Stream target, Aes7zEncoder? aes)
    {
        _options = options;
        _dictSize = dictSize;
        _useBcj = useBcj;

        _packCounter = new CountingStream(target);
        Stream afterCompressor = _packCounter;

        if (aes != null)
        {
            _aesEncoder = aes;
            _aesStream = _aesEncoder.CreateEncryptStream(_packCounter);
            _compressedCounter = new CountingStream(_aesStream);
            afterCompressor = _compressedCounter;
        }

        if (IsCopy)
        {
            _compressorInput = afterCompressor;
        }
        else if (options.Method == CompressionMethod.Lzma)
        {
            _lzmaBuffer = new MemoryStream();
            _compressorInput = _lzmaBuffer;
        }
        else if (options.Method == CompressionMethod.BZip2)
        {
            int threads = options.Threads <= 0 ? Environment.ProcessorCount : options.Threads;
            _bzip2Stream = new BZip2EncoderStream(afterCompressor, BZip2BlockSize(options.Level), threads);
            _compressorInput = _bzip2Stream;
        }
        else if (options.Method == CompressionMethod.Deflate)
        {
            _deflateStream = new DeflateEncoderStream(afterCompressor, options.Level);
            _compressorInput = _deflateStream;
        }
        else if (options.Method == CompressionMethod.Ppmd)
        {
            var (order, mem) = Ppmd7EncoderStream.DefaultsForLevel(options.Level);
            _ppmdStream = new Ppmd7EncoderStream(afterCompressor, order, mem);
            _compressorInput = _ppmdStream;
        }
        else
        {
            int threads = options.Threads <= 0 ? Environment.ProcessorCount : options.Threads;
            var lzma2 = new Lzma2Encoder(dictSize, threads) { Mode = Mode, NumFastBytes = FastBytes };
            _lzma2Stream = lzma2.CreateEncoderStream(afterCompressor);
            _compressorInput = _lzma2Stream;
        }

        if (useBcj)
        {
            _bcjStream = new BcjEncodeStream(_compressorInput);
            _head = _bcjStream;
        }
        else
        {
            _head = _compressorInput;
        }
    }

    private bool IsCopy => _options.Method == CompressionMethod.Copy || _options.Level == 0;

    /// <summary>
    /// Bloque de BZip2 por nivel, en unidades de 100 000 bytes. Elección propia; 7z.exe 24.08 usa 1, 3, 5,
    /// 7 y 9 para los niveles 1 a 5 y 9 desde ahí (docs/FORMAT.md §6).
    /// </summary>
    internal static int BZip2BlockSize(int level) => level <= 2 ? 1 : level <= 4 ? 5 : 9;
    private LzmaMode Mode => _options.Level >= 5 ? LzmaMode.Optimal : LzmaMode.Fast;
    private int FastBytes => _options.Level >= 7 ? 64 : 32;

    public bool UsesBcj => _useBcj;
    public int SubStreamCount => _sizes.Count;
    public long TotalUnpackSize { get; private set; }
    public IReadOnlyList<long> SubStreamSizes => _sizes;
    public IReadOnlyList<uint> SubStreamCrcs => _crcs;

    /// <summary>
    /// Agrega el contenido de una entrada. Devuelve su tamaño y CRC. Una entrada de 0 bytes no se
    /// registra como substream (7z la representa como archivo vacío, sin datos).
    /// </summary>
    public (long Size, uint Crc) AddSubStream(Stream content)
    {
        ObjectDisposedException.ThrowIf(_finished, this);

        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1 << 16);
        try
        {
            long size = 0;
            uint crc = 0;
            int read;
            while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
            {
                crc = Crc32Algorithm.Update(crc, buffer.AsSpan(0, read));
                _head.Write(buffer, 0, read);
                size += read;
            }

            if (size > 0)
            {
                _sizes.Add(size);
                _crcs.Add(crc);
                TotalUnpackSize += size;
            }

            return (size, crc);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Cierra la cadena (vacía filtros, compresor y cifrado) y describe el folder resultante.
    /// </summary>
    public Folder Finish(out long packSize)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _finished = true;

        _bcjStream?.Finish();

        if (_lzmaBuffer != null)
        {
            var props = new LzmaProperties(3, 0, 2, _dictSize);
            var lzma = new LzmaEncoder(props) { Mode = Mode, NumFastBytes = FastBytes };
            Stream sink = (Stream?)_compressedCounter ?? _packCounter;
            lzma.Encode(_lzmaBuffer.GetBuffer().AsSpan(0, (int)_lzmaBuffer.Length), sink, writeEos: false);
        }

        _lzma2Stream?.Finish();
        _ppmdStream?.Finish();
        _bzip2Stream?.Finish();
        _deflateStream?.Finish();
        _aesStream?.Finish();

        packSize = _packCounter.Count;
        long compressedSize = _compressedCounter?.Count ?? _packCounter.Count;

        var folder = new Folder();
        AssembleCoders(folder, compressedSize);
        folder.Crc = null; // los CRC viajan por archivo en SubStreamsInfo, como en 7-Zip
        return folder;
    }

    /// <summary>
    /// Grafo en orden de decodificación: [AES] → compresor → [BCJ]. UnpackSizes va en el orden de los coders.
    /// </summary>
    private void AssembleCoders(Folder folder, long compressedSize)
    {
        long unpack = TotalUnpackSize;
        bool aes = _aesEncoder != null;

        if (aes)
        {
            folder.Coders.Add(new CoderInfo { MethodId = MethodId.Aes256, Properties = _aesEncoder!.GetProperties(), NumInStreams = 1, NumOutStreams = 1 });
            folder.UnpackSizes.Add(compressedSize);
        }

        folder.Coders.Add(CreateCompressorCoder());
        folder.UnpackSizes.Add(unpack);

        if (_useBcj)
        {
            folder.Coders.Add(new CoderInfo { MethodId = MethodId.BcjX86, NumInStreams = 1, NumOutStreams = 1 });
            folder.UnpackSizes.Add(unpack);
        }

        // Cada coder (salvo el primero) recibe la salida del anterior: entrada i <- salida i-1.
        for (int i = 1; i < folder.Coders.Count; i++)
        {
            folder.BindPairs.Add(new BindPair(i, i - 1));
        }
    }

    private CoderInfo CreateCompressorCoder()
    {
        if (IsCopy)
        {
            return new CoderInfo { MethodId = MethodId.Copy, NumInStreams = 1, NumOutStreams = 1 };
        }

        if (_bzip2Stream != null)
        {
            return new CoderInfo { MethodId = MethodId.BZip2, NumInStreams = 1, NumOutStreams = 1 };
        }

        if (_deflateStream != null)
        {
            return new CoderInfo { MethodId = MethodId.Deflate, NumInStreams = 1, NumOutStreams = 1 };
        }

        if (_ppmdStream != null)
        {
            return new CoderInfo { MethodId = MethodId.Ppmd, Properties = _ppmdStream.GetProperties(), NumInStreams = 1, NumOutStreams = 1 };
        }

        if (_options.Method == CompressionMethod.Lzma)
        {
            byte[] props = new byte[5];
            props[0] = 0x5D; // (pb * 5 + lp) * 9 + lc = (2 * 5 + 0) * 9 + 3
            BinaryPrimitives.WriteUInt32LittleEndian(props.AsSpan(1, 4), _dictSize);
            return new CoderInfo { MethodId = MethodId.Lzma, Properties = props, NumInStreams = 1, NumOutStreams = 1 };
        }

        return new CoderInfo
        {
            MethodId = MethodId.Lzma2,
            Properties = [Lzma2Properties.GetPropertyByte(_dictSize)],
            NumInStreams = 1,
            NumOutStreams = 1
        };
    }

    public void Dispose()
    {
        _lzma2Stream?.Dispose();
        _ppmdStream?.Dispose();
        _bzip2Stream?.Dispose();
        _deflateStream?.Dispose();
        _aesStream?.Dispose();
        _aesEncoder?.Dispose();
        _lzmaBuffer?.Dispose();
    }

    /// <summary>Pasa los datos al destino contando los bytes.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Count += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Count += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            inner.WriteByte(value);
            Count++;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;

        public override long Position
        {
            get => Count;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Aplica BCJ x86 en streaming: convierte lo que el filtro puede procesar y retiene la cola (una
    /// instrucción cortada en el borde) hasta tener más datos; al final la emite tal cual, igual que
    /// la deja el decodificador.
    /// </summary>
    private sealed class BcjEncodeStream(Stream inner) : Stream
    {
        private readonly BcjX86Filter _filter = new();
        private readonly byte[] _buffer = new byte[1 << 16];
        private int _count;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int n = Math.Min(buffer.Length, _buffer.Length - _count);
                buffer[..n].CopyTo(_buffer.AsSpan(_count));
                _count += n;
                buffer = buffer[n..];

                if (_count == _buffer.Length)
                {
                    int done = _filter.Encode(_buffer.AsSpan(0, _count));
                    inner.Write(_buffer, 0, done);
                    Buffer.BlockCopy(_buffer, done, _buffer, 0, _count - done);
                    _count -= done;
                }
            }
        }

        public void Finish()
        {
            int done = _filter.Encode(_buffer.AsSpan(0, _count));
            _ = done; // la cola que no se pudo convertir va tal cual
            inner.Write(_buffer, 0, _count);
            _count = 0;
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
}
