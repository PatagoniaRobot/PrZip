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
using PrZip.Codecs.BZip2;
using Xunit;

namespace PrZip.Tests;

/// <summary>
/// Bloques BZip2 "randomised": los escribía bzip2 0.9.0 y los sigue escribiendo SharpZipLib con datos muy
/// repetitivos. bzip2 y 7-Zip los leen; el motor los rechazaba.
/// </summary>
public class BZip2RandomisedTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Formats", "BZip2", "randomised.txt.bz2");

    // El texto con el que se escribió el fixture (ver el csproj).
    private static byte[] Expected() =>
        new UTF8Encoding(false).GetBytes(string.Concat(Enumerable.Repeat("declaración del testigo; ", 2000)));

    [Fact]
    public void Fixture_IsReallyRandomised()
    {
        // Tras "BZh9", la marca de bloque (6 bytes) y su CRC (4), el primer bit es el de aleatorización.
        byte[] data = File.ReadAllBytes(Fixture);
        Assert.Equal("BZh9", Encoding.ASCII.GetString(data, 0, 4));
        Assert.NotEqual(0, data[14] & 0x80);
    }

    [Fact]
    public void RandomisedBlock_DecodesToTheOriginal()
    {
        using var input = File.OpenRead(Fixture);
        var output = new MemoryStream();
        BZip2Decoder.Decode(input, output);
        Assert.Equal(Expected(), output.ToArray());
    }

    [Fact]
    public void RandomisedBlock_OpensThroughArchiveReader()
    {
        using var reader = PrZip.Formats.ArchiveReader.TryOpen(Fixture);
        Assert.NotNull(reader);
        Assert.Equal("bz2", reader.FormatName);
        Assert.True(reader.MoveNext());
        var output = new MemoryStream();
        reader.CopyCurrentTo(output);
        Assert.Equal(Expected(), output.ToArray());
    }
}
