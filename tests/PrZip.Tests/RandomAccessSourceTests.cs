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

using PrZip.Primitives.IO;
using Xunit;

namespace PrZip.Tests;

public class RandomAccessSourceTests
{
    [Fact]
    public void MemoryRandomAccessSource_ConcurrentReads_Success()
    {
        byte[] data = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            data[i] = (byte)i;
        }

        using var source = new MemoryRandomAccessSource(data);
        Assert.Equal(256, source.Length);

        // Lectura síncrona
        byte[] buffer = new byte[10];
        int read = source.Read(50, buffer);
        Assert.Equal(10, read);
        Assert.Equal(50, buffer[0]);
        Assert.Equal(59, buffer[9]);

        // Lectura posicional arbitraria sin alterar estado
        byte[] buffer2 = new byte[5];
        source.ReadExact(20, buffer2);
        Assert.Equal(20, buffer2[0]);
    }

    [Fact]
    public async Task FileRandomAccessSource_ReadExactAsync_Success()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"rand_test_{Guid.NewGuid():N}.dat");
        try
        {
            byte[] expected = new byte[1024];
            new Random(101).NextBytes(expected);
            File.WriteAllBytes(tempPath, expected);

            await using var source = new FileRandomAccessSource(tempPath);
            Assert.Equal(1024, source.Length);

            byte[] actual = new byte[512];
            await source.ReadExactAsync(256, actual);

            Assert.True(expected.AsSpan(256, 512).SequenceEqual(actual));
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
