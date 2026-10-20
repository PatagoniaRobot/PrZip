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
using PrZip.Exceptions;
using PrZip.Primitives.IO;

namespace PrZip.Container.Extraction;

/// <summary>
/// Decodificador de carpetas (Folders) de 7z.
/// Evalúa el grafo de coders y bind pairs de forma genérica y entrega la salida principal como un
/// Stream que se lee a demanda: la memoria usada no depende del tamaño del folder, sino de los
/// diccionarios de los códecs y de los búferes de tránsito entre ellos.
/// Soporta: Copy, LZMA, LZMA2, PPMd, BZip2, Deflate, Deflate64, AES-256, BCJ2 y los filtros BCJ (x86, ARM, ARMT,
/// ARM64, PPC, SPARC, IA64, RISC-V) y Delta.
/// </summary>
internal static class FolderDecoder
{
    public static void Decode(
        Folder folder,
        Stream packedStream,
        Stream outStream,
        long unpackSize,
        string? password = null,
        Aes7zKeyCache? keys = null)
    {
        ArgumentNullException.ThrowIfNull(packedStream);
        Decode(folder, [packedStream], outStream, unpackSize, password, keys);
    }

    /// <summary>
    /// Decodifica el folder completo hacia outStream. Conveniencia sobre <see cref="Open"/>.
    /// </summary>
    public static void Decode(
        Folder folder,
        IReadOnlyList<Stream> packedStreams,
        Stream outStream,
        long unpackSize,
        string? password = null,
        Aes7zKeyCache? keys = null)
    {
        ArgumentNullException.ThrowIfNull(outStream);
        using Stream decoded = Open(folder, packedStreams, password, keys);
        long copied = CopyExactly(decoded, outStream, unpackSize);
        if (unpackSize >= 0 && copied != unpackSize)
        {
            throw new CorruptArchiveException($"El folder produjo {copied} bytes de {unpackSize} declarados.");
        }
    }

    /// <summary>
    /// Abre la salida principal del folder como un Stream de lectura. Al cerrarlo se liberan todos
    /// los coders intermedios y se detienen sus hilos. Con <paramref name="keys"/> (del mismo
    /// <paramref name="password"/>), las claves AES ya derivadas para otros folders se reutilizan.
    /// </summary>
    public static Stream Open(Folder folder, IReadOnlyList<Stream> packedStreams, string? password = null, Aes7zKeyCache? keys = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(packedStreams);

        if (folder.Coders.Count == 0)
        {
            throw new CorruptArchiveException("Folder sin coders.");
        }

        var graph = new FolderGraph(folder, packedStreams, password, keys);
        try
        {
            Stream main = graph.OpenCoderOutput(graph.MainCoder);
            return new OwnedStream(main, graph);
        }
        catch
        {
            graph.Dispose();
            throw;
        }
    }

    private static long CopyExactly(Stream source, Stream destination, long limit)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        while (limit < 0 || total < limit)
        {
            int toRead = limit < 0 ? buffer.Length : (int)Math.Min(buffer.Length, limit - total);
            int read = source.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            destination.Write(buffer, 0, read);
            total += read;
        }

        return total;
    }

    /// <summary>
    /// Resolución del grafo: cada entrada de un coder es la salida de otro coder (BindPair) o un
    /// stream empaquetado (PackedStreamIndices). En 7z cada coder tiene exactamente una salida.
    /// </summary>
    private sealed class FolderGraph : IDisposable
    {
        private readonly Folder _folder;
        private readonly IReadOnlyList<Stream> _packedStreams;
        private readonly Aes7zKeyCache? _keys;
        private readonly bool _ownsKeys;
        private readonly int[] _inBase;
        private readonly bool[] _opened;
        private readonly List<Stream> _created = [];

        public int MainCoder { get; }

        public FolderGraph(Folder folder, IReadOnlyList<Stream> packedStreams, string? password, Aes7zKeyCache? keys)
        {
            _folder = folder;
            _packedStreams = packedStreams;
            if (keys != null)
            {
                _keys = keys;
            }
            else if (!string.IsNullOrEmpty(password))
            {
                _keys = new Aes7zKeyCache(password);
                _ownsKeys = true;
            }

            _inBase = new int[folder.Coders.Count];
            _opened = new bool[folder.Coders.Count];

            int totalIn = 0;
            for (int c = 0; c < folder.Coders.Count; c++)
            {
                if (folder.Coders[c].NumOutStreams != 1)
                {
                    throw new UnsupportedMethodException(
                        $"Coder con {folder.Coders[c].NumOutStreams} salidas: el formato 7z actual solo usa coders de una salida.");
                }

                _inBase[c] = totalIn;
                totalIn += folder.Coders[c].NumInStreams;
            }

            if (folder.UnpackSizes.Count != folder.Coders.Count)
            {
                throw new CorruptArchiveException(
                    $"El folder declara {folder.UnpackSizes.Count} tamaños para {folder.Coders.Count} coders.");
            }

            MainCoder = -1;
            for (int c = 0; c < folder.Coders.Count; c++)
            {
                if (!folder.BindPairs.Any(bp => bp.OutIndex == c))
                {
                    if (MainCoder >= 0)
                    {
                        throw new CorruptArchiveException("El folder tiene más de una salida sin ligar.");
                    }

                    MainCoder = c;
                }
            }

            if (MainCoder < 0)
            {
                throw new CorruptArchiveException("El folder no tiene salida principal (grafo cíclico).");
            }
        }

        public Stream OpenCoderOutput(int coderIndex)
        {
            if (coderIndex < 0 || coderIndex >= _folder.Coders.Count)
            {
                throw new CorruptArchiveException($"BindPair apunta a un coder inexistente ({coderIndex}).");
            }

            // Cada salida alimenta a una sola entrada: abrirla dos veces delataría un ciclo o un grafo malformado.
            if (_opened[coderIndex])
            {
                throw new CorruptArchiveException("Grafo de coders inválido: una salida se consume dos veces.");
            }

            _opened[coderIndex] = true;

            CoderInfo coder = _folder.Coders[coderIndex];
            var inputs = new Stream[coder.NumInStreams];
            for (int i = 0; i < inputs.Length; i++)
            {
                inputs[i] = OpenInput(_inBase[coderIndex] + i);
            }

            Stream output = CreateCoderStream(coder, inputs, _folder.UnpackSizes[coderIndex], _keys);
            _created.Add(output);
            return output;
        }

        private Stream OpenInput(int inIndex)
        {
            foreach (var bp in _folder.BindPairs)
            {
                if (bp.InIndex == inIndex)
                {
                    return OpenCoderOutput(bp.OutIndex);
                }
            }

            int packed = _folder.PackedStreamIndices.IndexOf(inIndex);
            if (packed < 0 || packed >= _packedStreams.Count)
            {
                throw new CorruptArchiveException($"La entrada {inIndex} del folder no tiene stream empaquetado.");
            }

            return _packedStreams[packed];
        }

        public void Dispose()
        {
            // Los consumidores se crean después que sus entradas: se cierran en orden inverso.
            for (int i = _created.Count - 1; i >= 0; i--)
            {
                _created[i].Dispose();
            }

            _created.Clear();
            if (_ownsKeys)
            {
                _keys!.Dispose();
            }
        }
    }

    private static Stream CreateCoderStream(CoderInfo coder, Stream[] inputs, long unpackSize, Aes7zKeyCache? keys)
    {
        byte[] id = coder.MethodId;

        if (MethodId.Equals(id, MethodId.Bcj2))
        {
            if (inputs.Length != 4)
            {
                throw new CorruptArchiveException($"BCJ2 requiere 4 entradas y el folder declara {inputs.Length}.");
            }

            return new ProducerStream(
                output => Bcj2Decoder.Decode(inputs[0], inputs[1], inputs[2], inputs[3], output, unpackSize),
                "BCJ2");
        }

        if (inputs.Length != 1)
        {
            throw new UnsupportedMethodException(
                $"Coder {Convert.ToHexString(id)} con {inputs.Length} entradas no soportado.");
        }

        Stream input = inputs[0];

        if (MethodId.Equals(id, MethodId.Copy))
        {
            return new NonOwningStream(input);
        }

        if (TryCreateFilter(coder) is { } filter)
        {
            return new FilterReadStream(input, filter);
        }

        Action<Stream> decode = CreatePushDecoder(coder, input, unpackSize, keys);
        return new ProducerStream(decode, Convert.ToHexString(id));
    }

    private static Action<Stream> CreatePushDecoder(CoderInfo coder, Stream input, long unpackSize, Aes7zKeyCache? keys)
    {
        byte[] id = coder.MethodId;

        if (MethodId.Equals(id, MethodId.Lzma2))
        {
            uint dictSize = EffectiveDictionary(
                coder.Properties is { Length: > 0 } ? Lzma2Properties.GetDictionarySize(coder.Properties[0]) : 1u << 20,
                unpackSize);

            return output =>
            {
                var decoder = new Lzma2Decoder();
                decoder.SetDictionarySize(dictSize);
                decoder.Decode(input, output, unpackSize);
            };
        }

        if (MethodId.Equals(id, MethodId.Lzma))
        {
            if (coder.Properties == null || coder.Properties.Length < 5)
            {
                throw new CorruptArchiveException("Faltan las propiedades de 5 bytes requeridas por LZMA.");
            }

            var declared = LzmaProperties.Decode(coder.Properties);
            var props = new LzmaProperties(declared.Lc, declared.Lp, declared.Pb,
                EffectiveDictionary(declared.DictionarySize, unpackSize));
            return output =>
            {
                var decoder = new LzmaDecoder();
                decoder.SetProperties(props);
                decoder.Decode(input, output, unpackSize);
            };
        }

        if (MethodId.Equals(id, MethodId.Deflate))
        {
            return output => DeflateDecoder.Decode(input, output, unpackSize);
        }

        if (MethodId.Equals(id, MethodId.BZip2))
        {
            return output => BZip2Decoder.Decode(input, output, unpackSize);
        }

        if (MethodId.Equals(id, MethodId.Aes256))
        {
            if (keys == null)
            {
                throw new InvalidPasswordOrCorruptDataException(
                    "Se requiere una contraseña para abrir o extraer este contenido protegido por AES-256.");
            }

            var aesProps = Aes7zProperties.Decode(coder.Properties);
            return output =>
            {
                using var aes = new Aes7zDecoder(aesProps, keys);
                aes.Decode(input, output, unpackSize);
            };
        }

        if (MethodId.Equals(id, MethodId.Deflate64))
        {
            return output => DeflateDecoder.DecodeDeflate64(input, output, unpackSize);
        }

        if (MethodId.Equals(id, MethodId.Ppmd))
        {
            var (order, memSize) = Ppmd7Decoder.ParseProperties(coder.Properties ?? []);

            // El modelo PPMd ocupa toda la memoria declarada (no se puede recortar a la salida).
            if (memSize > ReaderLimits.MaxPpmdMemory)
            {
                throw new LimitExceededException(
                    $"El archivo requiere {memSize:N0} bytes de memoria PPMd: supera el máximo admitido ({ReaderLimits.MaxPpmdMemory:N0}).");
            }

            return output => Ppmd7Decoder.Decode(input, output, unpackSize, order, memSize);
        }

        throw new UnsupportedMethodException($"Método de compresión 7z no soportado (ID: {Convert.ToHexString(id)}).");
    }

    /// <summary>
    /// Diccionario a reservar: el declarado, pero nunca más que lo que el coder va a producir (ninguna
    /// distancia puede superar los datos ya emitidos) y nunca más que el máximo de 7-Zip. Así un archivo
    /// chico con diccionario de 1,5 GB no reserva 1,5 GB, y uno hostil no puede pedir 4 GB.
    /// </summary>
    private static uint EffectiveDictionary(uint declared, long unpackSize)
    {
        long needed = unpackSize >= 0 ? Math.Min(declared, Math.Max(unpackSize, 4096)) : declared;
        if (needed > ReaderLimits.MaxDictionarySize)
        {
            throw new LimitExceededException(
                $"El archivo requiere un diccionario de {needed:N0} bytes: supera el máximo admitido ({ReaderLimits.MaxDictionarySize:N0}).");
        }

        return (uint)needed;
    }

    /// <summary>
    /// Devuelve una función que convierte in-place los primeros n bytes del búfer y retorna cuántos
    /// dejó listos; el resto (una instrucción partida) se vuelve a presentar con los datos siguientes.
    /// </summary>
    private static Func<byte[], int, int>? TryCreateFilter(CoderInfo coder)
    {
        byte[] id = coder.MethodId;
        uint ip = GetBranchStartIp(coder);

        if (MethodId.Equals(id, MethodId.BcjX86))
        {
            var f = new BcjX86Filter(ip);
            return (buf, n) => f.Decode(buf.AsSpan(0, n));
        }

        if (MethodId.Equals(id, MethodId.BcjArm))
        {
            var f = new BcjArmFilter(ip);
            return (buf, n) => f.Decode(buf.AsSpan(0, n));
        }

        if (MethodId.Equals(id, MethodId.BcjArm64))
        {
            var f = new BcjArm64Filter(ip);
            return (buf, n) => f.Decode(buf.AsSpan(0, n));
        }

        if (MethodId.Equals(id, MethodId.Delta))
        {
            int dist = coder.Properties is { Length: > 0 } ? coder.Properties[0] + 1 : 1;
            var f = new DeltaFilter(dist);
            return (buf, n) =>
            {
                f.Decode(buf.AsSpan(0, n));
                return n;
            };
        }

        BranchConvert? convert =
            MethodId.Equals(id, MethodId.BcjArmThumb) ? BranchConverters.ArmThumb
            : MethodId.Equals(id, MethodId.BcjPpc) ? BranchConverters.Ppc
            : MethodId.Equals(id, MethodId.BcjSparc) ? BranchConverters.Sparc
            : MethodId.Equals(id, MethodId.BcjIa64) ? BranchConverters.Ia64
            : MethodId.Equals(id, MethodId.BcjRiscV) ? BranchConverters.RiscV
            : null;

        if (convert == null)
        {
            return null;
        }

        return (buf, n) =>
        {
            int done = convert(buf.AsSpan(0, n), ip, false);
            ip += (uint)done;
            return done;
        };
    }

    private delegate int BranchConvert(Span<byte> data, uint ip, bool encoding);

    /// <summary>
    /// Los filtros de salto admiten una propiedad opcional de 4 bytes con el PC inicial (little-endian).
    /// </summary>
    private static uint GetBranchStartIp(CoderInfo coder)
    {
        return coder.Properties is { Length: 4 } props
            ? BinaryPrimitives.ReadUInt32LittleEndian(props)
            : 0;
    }

    /// <summary>
    /// Aplica un filtro in-place sobre lo que lee de su entrada. Los bytes que el filtro no pudo
    /// procesar todavía (una instrucción cortada en el borde del bloque) se retienen hasta tener
    /// más datos; al final del stream se entregan tal cual, como hace 7-Zip.
    /// </summary>
    private sealed class FilterReadStream(Stream input, Func<byte[], int, int> convert) : Stream
    {
        private readonly byte[] _buffer = new byte[1 << 16];
        private int _start;
        private int _ready;
        private int _filled;
        private bool _eof;
        private long _position;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            if (destination.IsEmpty)
            {
                return 0;
            }

            while (_start == _ready)
            {
                if (!Refill())
                {
                    return 0;
                }
            }

            int n = Math.Min(destination.Length, _ready - _start);
            _buffer.AsSpan(_start, n).CopyTo(destination);
            _start += n;
            _position += n;
            return n;
        }

        private bool Refill()
        {
            // Mover al frente la cola sin convertir.
            int tail = _filled - _ready;
            if (tail > 0)
            {
                Buffer.BlockCopy(_buffer, _ready, _buffer, 0, tail);
            }

            _filled = tail;
            _start = 0;
            _ready = 0;

            while (!_eof && _filled < _buffer.Length)
            {
                int read = input.Read(_buffer, _filled, _buffer.Length - _filled);
                if (read == 0)
                {
                    _eof = true;
                    break;
                }

                _filled += read;
            }

            if (_filled == 0)
            {
                return false;
            }

            int done = convert(_buffer, _filled);
            _ready = _eof ? _filled : done;
            return true;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Vista de lectura de un stream empaquetado que no lo cierra: los streams empaquetados
    /// pertenecen a quien llamó a <see cref="Open"/>.
    /// </summary>
    private class NonOwningStream(Stream inner) : Stream
    {
        protected Stream Inner => inner;

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override int ReadByte() => inner.ReadByte();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Stream principal del folder que, al cerrarse, libera todo el grafo.
    /// </summary>
    private sealed class OwnedStream(Stream inner, IDisposable owner) : NonOwningStream(inner)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
