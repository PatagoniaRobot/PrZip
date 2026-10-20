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

using PrZip.Exceptions;
using PrZip.Primitives.Memory;
using Xunit;

namespace PrZip.Tests;

public class NativeBufferTests
{
    [Fact]
    public void AllocationAndIndexing_Success()
    {
        const int count = 1000;
        using var buffer = new NativeBuffer<uint>(count);

        Assert.Equal((nuint)count, buffer.Length);
        Assert.Equal((nuint)(count * sizeof(uint)), buffer.ByteLength);

        // Verificar que viene en ceros
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(0u, buffer[i]);
        }

        // Modificar valores
        for (int i = 0; i < count; i++)
        {
            buffer[i] = (uint)(i * 10);
        }

        // Leer valores
        for (int i = 0; i < count; i++)
        {
            Assert.Equal((uint)(i * 10), buffer[i]);
        }
    }

    [Fact]
    public void AsSpan_WorksAsExpected()
    {
        using var buffer = new NativeBuffer<byte>(256);
        Span<byte> span = buffer.AsSpan();

        for (int i = 0; i < 256; i++)
        {
            span[i] = (byte)i;
        }

        for (int i = 0; i < 256; i++)
        {
            Assert.Equal((byte)i, buffer[i]);
        }
    }

    [Fact]
    public void DoubleDispose_DoesNotThrow()
    {
        var buffer = new NativeBuffer<int>(10);
        buffer.Dispose();
        buffer.Dispose(); // No debe fallar
    }

    [Fact]
    public void AccessAfterDispose_ThrowsObjectDisposedException()
    {
        var buffer = new NativeBuffer<int>(10);
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffer[0]);
    }
}

public class MemoryBudgetTests
{
    [Fact]
    public void BudgetTracking_AcquireAndRelease_Success()
    {
        var budget = new MemoryBudget(maxMemoryBytes: 1000);

        Assert.Equal(0, budget.CurrentUsageBytes);
        Assert.Equal(1000, budget.MaxMemoryBytes);

        budget.Acquire(400);
        Assert.Equal(400, budget.CurrentUsageBytes);

        budget.Acquire(600);
        Assert.Equal(1000, budget.CurrentUsageBytes);

        budget.Release(500);
        Assert.Equal(500, budget.CurrentUsageBytes);

        budget.Release(500);
        Assert.Equal(0, budget.CurrentUsageBytes);
    }

    [Fact]
    public void ExceedingBudget_ThrowsLimitExceededException()
    {
        var budget = new MemoryBudget(maxMemoryBytes: 500);

        budget.Acquire(300);

        Assert.Throws<LimitExceededException>(() => budget.Acquire(201));
        Assert.Equal(300, budget.CurrentUsageBytes); // No debe haberse incrementado
    }

    [Fact]
    public void AllocateBuffer_ConsumesBudgetAndReleasesOnDispose()
    {
        var budget = new MemoryBudget(maxMemoryBytes: 10000);

        using (var buf = budget.AllocateBuffer<uint>(100)) // 100 * 4 = 400 bytes
        {
            Assert.Equal(400, budget.CurrentUsageBytes);
            buf[0] = 42;
            Assert.Equal(42u, buf[0]);
        }

        budget.Release(400);
        Assert.Equal(0, budget.CurrentUsageBytes);
    }
}
