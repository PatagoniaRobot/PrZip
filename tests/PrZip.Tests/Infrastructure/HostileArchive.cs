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
using System.Text;
using PrZip.Primitives.Checksum;

namespace PrZip.Tests.Infrastructure;

/// <summary>
/// Arma 7z con nombres que el escritor rechaza ("../x", "/x"), para probar al lector contra archivos
/// hostiles: se escribe un nombre válido del mismo largo y después se lo reemplaza en la cabecera, que
/// el escritor deja sin comprimir ni cifrar, recalculando sus CRC.
/// </summary>
internal static class HostileArchive
{
    public static void ReplaceName(string archivePath, string writtenName, string hostileName)
    {
        if (writtenName.Length != hostileName.Length)
        {
            throw new ArgumentException("Los dos nombres tienen que tener el mismo largo.");
        }

        byte[] data = File.ReadAllBytes(archivePath);
        long headerOffset = 32 + BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(12));
        int headerSize = checked((int)BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(20)));
        Span<byte> header = data.AsSpan(checked((int)headerOffset), headerSize);

        byte[] from = Encoding.Unicode.GetBytes(writtenName + "\0");
        byte[] to = Encoding.Unicode.GetBytes(hostileName + "\0");
        int at = header.IndexOf(from);
        if (at < 0 || header[(at + 1)..].IndexOf(from) >= 0)
        {
            throw new InvalidOperationException($"'{writtenName}' no aparece una sola vez en la cabecera.");
        }

        to.CopyTo(header[at..]);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), Crc32Algorithm.Compute(header));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), Crc32Algorithm.Compute(data.AsSpan(12, 20)));
        File.WriteAllBytes(archivePath, data);
    }
}
