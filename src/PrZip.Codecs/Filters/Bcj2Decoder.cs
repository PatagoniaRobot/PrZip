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
using PrZip.Exceptions;

namespace PrZip.Codecs.Filters;

/// <summary>
/// Decodificador para el filtro de bifurcación x86 BCJ2 (ID: 03 03 01 1B).
/// Reconstruye el flujo binario desensamblado a partir de 4 flujos de entrada:
/// Stream 0 (Datos principales), Stream 1 (Direcciones CALL), Stream 2 (Direcciones JMP),
/// y Stream 3 (Flujo de control codificado por rango aritmético).
/// Basado en la especificación de Igor Pavlov (C/Bcj2.c).
/// </summary>
internal static class Bcj2Decoder
{
    public static void Decode(
        Stream mainStream,
        Stream callStream,
        Stream jumpStream,
        Stream rangeStream,
        Stream outStream,
        long unpackSize)
    {
        ArgumentNullException.ThrowIfNull(mainStream);
        ArgumentNullException.ThrowIfNull(callStream);
        ArgumentNullException.ThrowIfNull(jumpStream);
        ArgumentNullException.ThrowIfNull(rangeStream);
        ArgumentNullException.ThrowIfNull(outStream);

        // Inicializar tabla de probabilidades (256 para CALL según prevByte, 1 para JMP, 1 para Jcc)
        ushort[] probs = new ushort[256 + 2];
        for (int i = 0; i < probs.Length; i++)
        {
            probs[i] = 1024; // (1 << 11) / 2 = 50% probabilidad inicial
        }

        // Port de Bcj2_Decode (C/Bcj2.c, dominio público): cada byte se evalúa con IsJ(prevByte, b),
        // de modo que un E8/E9 que sigue a 0x0F también es un salto candidato.
        var rangeDecoder = new Bcj2RangeDecoder(rangeStream);
        var output = new BufferedStream(outStream, 1 << 16);
        long outPos = 0;
        byte prevByte = 0;
        Span<byte> uintBuf = stackalloc byte[4];

        while (unpackSize < 0 || outPos < unpackSize)
        {
            int read = mainStream.ReadByte();
            if (read < 0)
            {
                break;
            }

            byte b = (byte)read;
            output.WriteByte(b);
            outPos++;

            if (!IsJ(prevByte, b))
            {
                prevByte = b;
                continue;
            }

            if (unpackSize >= 0 && outPos == unpackSize)
            {
                break;
            }

            ref ushort prob = ref (b == 0xE8 ? ref probs[prevByte] : ref (b == 0xE9 ? ref probs[256] : ref probs[257]));
            if (rangeDecoder.DecodeBit(ref prob) == 0)
            {
                prevByte = b;
                continue;
            }

            uint src = ReadUInt32BE(b == 0xE8 ? callStream : jumpStream, uintBuf);
            uint dest = src - (uint)(outPos + 4);
            BinaryPrimitives.WriteUInt32LittleEndian(uintBuf, dest);

            int toWrite = unpackSize < 0 ? 4 : (int)Math.Min(4, unpackSize - outPos);
            output.Write(uintBuf[..toWrite]);
            outPos += toWrite;
            prevByte = (byte)(dest >> 24);
        }

        output.Flush();

        if (unpackSize >= 0 && outPos != unpackSize)
        {
            throw new CorruptArchiveException($"BCJ2: el stream principal terminó en {outPos} bytes de {unpackSize} esperados.");
        }
    }

    private static bool IsJ(byte b0, byte b1) => (b1 & 0xFE) == 0xE8 || (b0 == 0x0F && (b1 & 0xF0) == 0x80);

    private static uint ReadUInt32BE(Stream stream, Span<byte> buf)
    {
        int total = 0;
        while (total < 4)
        {
            int r = stream.Read(buf[total..4]);
            if (r == 0)
            {
                throw new CorruptArchiveException("Fin inesperado en flujo de direcciones BCJ2.");
            }
            total += r;
        }
        return BinaryPrimitives.ReadUInt32BigEndian(buf);
    }

    private sealed class Bcj2RangeDecoder
    {
        private readonly Stream _stream;
        private uint _range;
        private uint _code;

        public Bcj2RangeDecoder(Stream stream)
        {
            _stream = stream;
            _range = 0xFFFFFFFF;
            _code = 0;

            for (int i = 0; i < 5; i++)
            {
                int b = _stream.ReadByte();
                _code = (_code << 8) | (b < 0 ? 0 : (uint)b);
            }
        }

        public int DecodeBit(ref ushort prob)
        {
            uint bound = (_range >> 11) * prob;
            if (_code < bound)
            {
                _range = bound;
                prob += (ushort)((2048 - prob) >> 5);
                Normalize();
                return 0;
            }
            else
            {
                _range -= bound;
                _code -= bound;
                prob -= (ushort)(prob >> 5);
                Normalize();
                return 1;
            }
        }

        private void Normalize()
        {
            while (_range < (1u << 24))
            {
                int b = _stream.ReadByte();
                _range <<= 8;
                _code = (_code << 8) | (b < 0 ? 0 : (uint)b);
            }
        }
    }
}
