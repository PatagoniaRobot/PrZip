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

using System.IO.Compression;
using System.Text;
using PrZip.Codecs.Deflate;
using Xunit;

namespace PrZip.Tests;

/// <summary>
/// Compresor Deflate propio. Árbitro independiente: el DeflateStream de .NET (zlib), además del Inflater
/// propio. Los tamaños cruzan los bordes del búfer interno (32 KB de ventana + 256 KB).
/// </summary>
public class DeflateEncoderTests
{
    public static TheoryData<string> Inputs => new()
    {
        "vacio", "un_byte", "texto", "azar", "ceros_1mb", "codigo", "mezcla_frontera", "periodico"
    };

    internal static byte[] Make(string name)
    {
        switch (name)
        {
            case "vacio":
                return [];
            case "un_byte":
                return [42];
            case "texto":
                return Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 20_000).Select(i => $"renglón {i % 977} del acta, foja {i / 13};\n")));
            case "azar":
            {
                var data = new byte[300_001];
                new Random(1).NextBytes(data);
                return data;
            }

            case "ceros_1mb":
                return new byte[1 << 20];
            case "codigo":
            {
                string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
                byte[] code = File.ReadAllBytes(Path.Combine(runtimeDir, OperatingSystem.IsWindows() ? "coreclr.dll" : "libcoreclr.so"));
                return code.AsSpan(0, Math.Min(code.Length, 1_500_000)).ToArray();
            }

            case "mezcla_frontera":
            {
                // Exactamente alrededor del borde del búfer (294 912 bytes) y de la ventana.
                var data = new byte[294_912 * 2 + 777];
                var rnd = new Random(2);
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = (byte)((i / 300) % 7 == 0 ? rnd.Next(256) : (i % 251));
                }

                return data;
            }

            default:
            {
                // Período de 3 bytes: coincidencias de distancia 3 y largo 258 encadenadas.
                var data = new byte[100_000];
                for (int i = 0; i < data.Length; i++) data[i] = (byte)"abc"[i % 3];
                return data;
            }
        }
    }

    internal static byte[] Compress(byte[] data, int level, int writeChunk = 4096)
    {
        using var ms = new MemoryStream();
        using (var enc = new DeflateEncoderStream(ms, level))
        {
            for (int i = 0; i < data.Length; i += writeChunk)
            {
                enc.Write(data, i, Math.Min(writeChunk, data.Length - i));
            }

            enc.Finish();
        }

        return ms.ToArray();
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void RoundTrip_AllLevels_DotNetAndOwnInflaterAgree(string name)
    {
        byte[] data = Make(name);
        foreach (int level in new[] { 1, 3, 5, 9 })
        {
            byte[] packed = Compress(data, level, writeChunk: level == 3 ? 1 : 65536);

            using (var bcl = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                bcl.CopyTo(output);
                Assert.True(data.AsSpan().SequenceEqual(output.ToArray()), $"{name} nivel {level}: .NET no reconstruye lo mismo");
            }

            using var own = new MemoryStream();
            Inflater.Decode(new MemoryStream(packed), own, data.Length, deflate64: false);
            Assert.True(data.AsSpan().SequenceEqual(own.ToArray()), $"{name} nivel {level}: el Inflater no reconstruye lo mismo");
        }
    }

    [Fact]
    public void Ratio_IsCompetitiveWithZlib_AndStoredBlocksCapRandomGrowth()
    {
        byte[] code = Make("codigo");
        byte[] ours = Compress(code, 9);
        using var zlib = new MemoryStream();
        using (var d = new DeflateStream(zlib, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            d.Write(code);
        }

        // Mismo formato y mismos límites: no debería quedar lejos de zlib -9.
        Assert.True(ours.Length < zlib.Length * 1.03, $"propio {ours.Length:N0} contra zlib {zlib.Length:N0}");

        byte[] random = Make("azar");
        byte[] packed = Compress(random, 5);
        // Azar: se emite sin comprimir, con 5 bytes de cabecera por bloque (un bloque cada 16 384 tokens).
        Assert.True(packed.Length <= random.Length + 5 * (random.Length / 16384 + 1), $"{packed.Length:N0} para {random.Length:N0} de azar");
    }

    /// <summary>
    /// Pocos bytes salen en un bloque de códigos fijos, donde los literales 144..255 llevan 9 bits. Un error
    /// al asignar esos códigos canónicos solo se ve cuando conviene un bloque fijo: acá se lo fuerza.
    /// </summary>
    [Fact]
    public void FixedBlock_HighLiterals_RoundTrip()
    {
        byte[] data = Enumerable.Range(0, 200).Select(i => (byte)(144 + (i % 40 * 37) % 112)).ToArray();
        byte[] packed = Compress(data, 5);
        Assert.Equal(1, (packed[0] >> 1) & 3); // BTYPE = 01: códigos fijos

        using var bcl = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        bcl.CopyTo(output);
        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public void Output_IsDeterministic_RegardlessOfWriteSizes()
    {
        byte[] data = Make("mezcla_frontera");
        Assert.Equal(Compress(data, 5, writeChunk: 1), Compress(data, 5, writeChunk: 1 << 20));
    }
}
