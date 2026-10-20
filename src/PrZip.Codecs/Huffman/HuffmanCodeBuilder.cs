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

namespace PrZip.Codecs.Huffman;

/// <summary>
/// Largos de código de Huffman óptimos con un largo máximo, por package-merge (Larmore y Hirschberg).
/// Con dos o más símbolos usados el código queda completo (la suma de Kraft da exactamente 1): hay
/// decodificadores que rechazan códigos incompletos, así que nunca se produce uno.
/// </summary>
internal static class HuffmanCodeBuilder
{
    /// <summary>
    /// Calcula el largo de cada símbolo. Los de frecuencia cero quedan con largo 0. Con un solo símbolo
    /// usado su largo es 1 (el código queda incompleto: el formato decide cómo completarlo).
    /// </summary>
    public static void BuildLengths(ReadOnlySpan<int> frequencies, int maxLength, Span<byte> lengths)
    {
        if (lengths.Length < frequencies.Length)
        {
            throw new ArgumentException("El destino es más chico que la tabla de frecuencias.", nameof(lengths));
        }

        lengths.Clear();
        int used = 0;
        int lastUsed = -1;
        for (int i = 0; i < frequencies.Length; i++)
        {
            if (frequencies[i] > 0)
            {
                used++;
                lastUsed = i;
            }
        }

        if (used == 0)
        {
            return;
        }

        if (used == 1)
        {
            lengths[lastUsed] = 1;
            return;
        }

        if (maxLength < 1 || maxLength > 30 || used > (1L << maxLength))
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "No caben todos los símbolos en ese largo máximo.");
        }

        // Hojas ordenadas por (frecuencia, símbolo): el desempate fijo hace la salida determinista.
        var leaves = new int[used];
        for (int i = 0, k = 0; i < frequencies.Length; i++)
        {
            if (frequencies[i] > 0)
            {
                leaves[k++] = i;
            }
        }

        var freqCopy = frequencies.ToArray();
        Array.Sort(leaves, (a, b) =>
        {
            int c = freqCopy[a].CompareTo(freqCopy[b]);
            return c != 0 ? c : a.CompareTo(b);
        });

        // Nodos: hojas (símbolo >= 0) y paquetes (dos hijos). Cada nivel tiene a lo sumo 2·used nodos.
        int capacity = used + maxLength * 2 * used;
        var weight = new long[capacity];
        var left = new int[capacity];
        var right = new int[capacity];
        var symbol = new int[capacity];
        int count = 0;

        var leafNodes = new int[used];
        for (int i = 0; i < used; i++)
        {
            weight[count] = freqCopy[leaves[i]];
            symbol[count] = leaves[i];
            left[count] = right[count] = -1;
            leafNodes[i] = count++;
        }

        int[] current = (int[])leafNodes.Clone();
        for (int level = 1; level < maxLength; level++)
        {
            // Empaquetar de a pares la lista anterior (si queda uno suelto, se descarta).
            int packages = current.Length / 2;
            var packed = new int[packages];
            for (int i = 0; i < packages; i++)
            {
                int a = current[2 * i];
                int b = current[2 * i + 1];
                weight[count] = weight[a] + weight[b];
                symbol[count] = -1;
                left[count] = a;
                right[count] = b;
                packed[i] = count++;
            }

            // Mezclar con las hojas por peso; ante empate van primero las hojas.
            var merged = new int[used + packages];
            int li = 0, pi = 0, mi = 0;
            while (li < used || pi < packages)
            {
                if (pi >= packages || (li < used && weight[leafNodes[li]] <= weight[packed[pi]]))
                {
                    merged[mi++] = leafNodes[li++];
                }
                else
                {
                    merged[mi++] = packed[pi++];
                }
            }

            current = merged;
        }

        // Los primeros 2·used − 2 elementos de la última lista: el largo de cada símbolo es la cantidad
        // de veces que aparece dentro de ellos.
        var stack = new Stack<int>();
        for (int i = 0; i < 2 * used - 2; i++)
        {
            stack.Push(current[i]);
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                if (symbol[node] >= 0)
                {
                    lengths[symbol[node]]++;
                }
                else
                {
                    stack.Push(left[node]);
                    stack.Push(right[node]);
                }
            }
        }
    }

    /// <summary>Códigos canónicos (RFC 1951 §3.2.2) a partir de los largos, con el bit más significativo primero.</summary>
    public static void BuildCanonicalCodes(ReadOnlySpan<byte> lengths, Span<int> codes, int maxLength)
    {
        Span<int> blCount = stackalloc int[maxLength + 1];
        foreach (byte len in lengths)
        {
            blCount[len]++;
        }

        blCount[0] = 0;
        Span<int> nextCode = stackalloc int[maxLength + 2];
        int code = 0;
        for (int bits = 1; bits <= maxLength; bits++)
        {
            code = (code + blCount[bits - 1]) << 1;
            nextCode[bits] = code;
        }

        for (int i = 0; i < lengths.Length; i++)
        {
            int len = lengths[i];
            codes[i] = len != 0 ? nextCode[len]++ : 0;
        }
    }
}
