// BLAKE2sp tal como lo usa RAR5. Port a C# de blake2s.cpp y blake2sp.cpp de unrar 7.3.1, que a su vez
// se basan en el código de referencia de BLAKE2 de Samuel Neves (2012, dominio público / CC0).
// Copyright de la adaptación de unrar: (c) Alexander L. Roshal.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

using System.Buffers.Binary;
using System.Numerics;

namespace PrZip.Rar;

/// <summary>BLAKE2sp de 32 bytes (8 hojas BLAKE2s en paralelo lógico y una raíz).</summary>
internal sealed class Blake2sp
{
    private const int BlockBytes = 64;
    private const int OutBytes = 32;
    private const int Parallelism = 8;

    private static readonly uint[] IV =
    [
        0x6A09E667u, 0xBB67AE85u, 0x3C6EF372u, 0xA54FF53Au,
        0x510E527Fu, 0x9B05688Cu, 0x1F83D9ABu, 0x5BE0CD19u
    ];

    private static readonly byte[,] Sigma =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
        { 11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4 },
        { 7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8 },
        { 9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13 },
        { 2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9 },
        { 12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11 },
        { 13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10 },
        { 6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5 },
        { 10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0 },
    };

    private sealed class State
    {
        public readonly byte[] Buf = new byte[2 * BlockBytes];
        public readonly uint[] H = new uint[8];
        public readonly uint[] T = new uint[2];
        public readonly uint[] F = new uint[2];
        public int BufLen;
        public bool LastNode;

        public void InitParam(uint nodeOffset, uint nodeDepth)
        {
            Array.Clear(Buf);
            Array.Clear(T);
            Array.Clear(F);
            BufLen = 0;
            LastNode = false;
            IV.CopyTo(H, 0);
            H[0] ^= 0x02080020; // bloque de parámetros de BLAKE2sp
            H[2] ^= nodeOffset;
            H[3] ^= (nodeDepth << 16) | 0x20000000;
        }

        private void IncrementCounter(uint inc)
        {
            T[0] += inc;
            T[1] += T[0] < inc ? 1u : 0u;
        }

        private void SetLastBlock()
        {
            if (LastNode)
            {
                F[1] = ~0u;
            }

            F[0] = ~0u;
        }

        private void Compress(ReadOnlySpan<byte> block)
        {
            Span<uint> m = stackalloc uint[16];
            Span<uint> v = stackalloc uint[16];
            for (int i = 0; i < 16; i++)
            {
                m[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(i * 4)..]);
            }

            for (int i = 0; i < 8; i++)
            {
                v[i] = H[i];
            }

            v[8] = IV[0];
            v[9] = IV[1];
            v[10] = IV[2];
            v[11] = IV[3];
            v[12] = T[0] ^ IV[4];
            v[13] = T[1] ^ IV[5];
            v[14] = F[0] ^ IV[6];
            v[15] = F[1] ^ IV[7];

            for (int r = 0; r <= 9; r++)
            {
                G(r, 0, m, v, 0, 4, 8, 12);
                G(r, 1, m, v, 1, 5, 9, 13);
                G(r, 2, m, v, 2, 6, 10, 14);
                G(r, 3, m, v, 3, 7, 11, 15);
                G(r, 4, m, v, 0, 5, 10, 15);
                G(r, 5, m, v, 1, 6, 11, 12);
                G(r, 6, m, v, 2, 7, 8, 13);
                G(r, 7, m, v, 3, 4, 9, 14);
            }

            for (int i = 0; i < 8; i++)
            {
                H[i] = H[i] ^ v[i] ^ v[i + 8];
            }
        }

        private static void G(int r, int i, Span<uint> m, Span<uint> v, int a, int b, int c, int d)
        {
            v[a] = v[a] + v[b] + m[Sigma[r, 2 * i]];
            v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
            v[c] = v[c] + v[d];
            v[b] = BitOperations.RotateRight(v[b] ^ v[c], 12);
            v[a] = v[a] + v[b] + m[Sigma[r, 2 * i + 1]];
            v[d] = BitOperations.RotateRight(v[d] ^ v[a], 8);
            v[c] = v[c] + v[d];
            v[b] = BitOperations.RotateRight(v[b] ^ v[c], 7);
        }

        public void Update(ReadOnlySpan<byte> input)
        {
            while (input.Length > 0)
            {
                int left = BufLen;
                int fill = 2 * BlockBytes - left;
                if (input.Length > fill)
                {
                    input[..fill].CopyTo(Buf.AsSpan(left));
                    BufLen += fill;
                    IncrementCounter(BlockBytes);
                    Compress(Buf);
                    Buf.AsSpan(BlockBytes, BlockBytes).CopyTo(Buf);
                    BufLen -= BlockBytes;
                    input = input[fill..];
                }
                else
                {
                    input.CopyTo(Buf.AsSpan(left));
                    BufLen += input.Length;
                    input = default;
                }
            }
        }

        public void Final(Span<byte> digest)
        {
            if (BufLen > BlockBytes)
            {
                IncrementCounter(BlockBytes);
                Compress(Buf);
                BufLen -= BlockBytes;
                Buf.AsSpan(BlockBytes, BufLen).CopyTo(Buf);
            }

            IncrementCounter((uint)BufLen);
            SetLastBlock();
            Buf.AsSpan(BufLen).Clear();
            Compress(Buf);
            for (int i = 0; i < 8; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(digest[(4 * i)..], H[i]);
            }
        }
    }

    private readonly State[] _s = new State[Parallelism];
    private readonly State _r = new();
    private readonly byte[] _buf = new byte[Parallelism * BlockBytes];
    private int _bufLen;

    public Blake2sp()
    {
        _r.InitParam(0, 1);
        for (int i = 0; i < Parallelism; i++)
        {
            _s[i] = new State();
            _s[i].InitParam((uint)i, 0);
        }

        _r.LastNode = true;
        _s[Parallelism - 1].LastNode = true;
    }

    public void Update(ReadOnlySpan<byte> input)
    {
        int left = _bufLen;
        int fill = _buf.Length - left;
        if (left != 0 && input.Length >= fill)
        {
            input[..fill].CopyTo(_buf.AsSpan(left));
            for (int i = 0; i < Parallelism; i++)
            {
                _s[i].Update(_buf.AsSpan(i * BlockBytes, BlockBytes));
            }

            input = input[fill..];
            left = 0;
        }

        for (int id = 0; id < Parallelism; id++)
        {
            var leaf = input[Math.Min(id * BlockBytes, input.Length)..];
            int len = input.Length;
            int pos = 0;
            while (len >= Parallelism * BlockBytes)
            {
                _s[id].Update(leaf.Slice(pos, BlockBytes));
                pos += Parallelism * BlockBytes;
                len -= Parallelism * BlockBytes;
            }
        }

        int consumed = input.Length - input.Length % (Parallelism * BlockBytes);
        input = input[consumed..];
        if (input.Length > 0)
        {
            input.CopyTo(_buf.AsSpan(left));
        }

        _bufLen = left + input.Length;
    }

    public byte[] Final()
    {
        var hash = new byte[Parallelism * OutBytes];
        for (int i = 0; i < Parallelism; i++)
        {
            if (_bufLen > i * BlockBytes)
            {
                int left = Math.Min(_bufLen - i * BlockBytes, BlockBytes);
                _s[i].Update(_buf.AsSpan(i * BlockBytes, left));
            }

            _s[i].Final(hash.AsSpan(i * OutBytes, OutBytes));
        }

        for (int i = 0; i < Parallelism; i++)
        {
            _r.Update(hash.AsSpan(i * OutBytes, OutBytes));
        }

        var digest = new byte[OutBytes];
        _r.Final(digest);
        return digest;
    }
}
