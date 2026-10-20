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

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PrZip.Codecs.Crypto;
using PrZip.Container.Headers;
using PrZip.Exceptions;
using PrZip.Primitives.Checksum;
using PrZip.Primitives.IO;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

/// <summary>
/// Evidencia de caja negra de docs/FORMAT.md §5 y §6: lo que estos tests le preguntan a 7z.exe (qué acepta,
/// qué escribe) es la fuente de las reglas que implementa el motor, no el código de 7-Zip. Cada caso arma
/// un 7z a mano, lo prueba con 7z.exe y exige que el lector del motor llegue al mismo resultado.
/// </summary>
public class BlackBoxFormatTests
{
    private const string Password = "clave";
    private static readonly byte[] Plain = Encoding.ASCII.GetBytes("experimento de caja negra contra 7z.exe, 0123456789");

    private static (int Code, string Output) Run7z(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(Official7zFinder.ExecutablePath!, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        var stderr = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd() + stderr.Result;
        process.WaitForExit(120000);
        return (process.ExitCode, output);
    }

    /// <summary>Un 7z con un solo archivo cifrado: 7zAES con las propiedades dadas, tal cual, sobre Copy.</summary>
    private static byte[] BuildAesArchive(byte[] properties, byte[] key, byte[] iv)
    {
        byte[] input = new byte[(Plain.Length + 15) / 16 * 16];
        Plain.CopyTo(input, 0);
        byte[] packed;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            packed = aes.EncryptCbc(input, iv, PaddingMode.None);
        }

        var folder = new Folder();
        folder.Coders.Add(new CoderInfo { MethodId = MethodId.Aes256, Properties = properties, NumInStreams = 1, NumOutStreams = 1 });
        folder.UnpackSizes.Add(Plain.Length);
        folder.Coders.Add(new CoderInfo { MethodId = MethodId.Copy, NumInStreams = 1, NumOutStreams = 1 });
        folder.UnpackSizes.Add(Plain.Length);
        folder.BindPairs.Add(new BindPair(1, 0));

        var pack = new PackInfo { PackPos = 0 };
        pack.PackSizes.Add(packed.Length);
        pack.PackCrcs.Add(null);
        var file = new FileItem { Name = "dato.txt", Size = Plain.Length, Crc = Crc32Algorithm.Compute(Plain), FolderIndex = 0, SubStreamIndex = 0 };
        byte[] header = ArchiveHeaderWriter.WriteHeader([folder], [file], pack, [1], null, [file.Crc]);

        byte[] archive = new byte[SignatureHeader.HeaderSize + packed.Length + header.Length];
        new SignatureHeader(0, 4, 0, packed.Length, header.Length, Crc32Algorithm.Compute(header), 0)
            .Write(archive.AsSpan(0, SignatureHeader.HeaderSize));
        packed.CopyTo(archive, SignatureHeader.HeaderSize);
        header.CopyTo(archive, SignatureHeader.HeaderSize + packed.Length);
        return archive;
    }

    /// <summary>Si 7z.exe lo prueba sin errores, y si el motor lo abre y obtiene el contenido original.</summary>
    private static (bool PrZip, bool Engine) Outcome(byte[] archive)
    {
        string path = Path.Combine(Path.GetTempPath(), $"caja_negra_{Guid.NewGuid():N}.7z");
        File.WriteAllBytes(path, archive);
        try
        {
            bool exe7z = Run7z($"t \"{path}\" -p{Password}").Code == 0;

            bool engine;
            try
            {
                using var reader = Archive7z.Open(new MemoryRandomAccessSource(archive), Password);
                var output = new MemoryStream();
                reader.Extract(reader.Entries[0], output);
                engine = output.ToArray().AsSpan().SequenceEqual(Plain);
            }
            catch (PrZipException)
            {
                engine = false;
            }

            return (exe7z, engine);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Random(int length)
    {
        byte[] bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static byte[] Iv16(byte[] iv)
    {
        byte[] full = new byte[16];
        iv.CopyTo(full, 0);
        return full;
    }

    public static TheoryData<int, int, int, int> SizeEncodings()
    {
        // Sal e IV de 0, 1, 8, 15 y 16 bytes, con cada forma de escribir el tamaño: bit + nibble.
        var data = new TheoryData<int, int, int, int>();
        foreach (int salt in new[] { 0, 1, 8, 15, 16 })
        foreach (int iv in new[] { 0, 1, 8, 15, 16 })
        foreach (int saltBit in new[] { 0, 1 })
        foreach (int ivBit in new[] { 0, 1 })
        {
            if (salt - saltBit is >= 0 and <= 15 && iv - ivBit is >= 0 and <= 15)
            {
                data.Add(salt, saltBit, iv, ivBit);
            }
        }

        return data;
    }

    [Requires7zTheory]
    [MemberData(nameof(SizeEncodings))]
    public void AesProperties_SizeEncoding_EngineAgreesWith7zExe(int saltSize, int saltBit, int ivSize, int ivBit)
    {
        Official7zFinder.EnsureAvailable();
        byte[] salt = Random(saltSize), iv = Random(ivSize);
        byte first = (byte)((saltBit << 7) | (ivBit << 6) | 19);
        byte second = (byte)(((saltSize - saltBit) << 4) | (ivSize - ivBit));
        byte[] properties = [first, second, .. salt, .. iv];

        var (exe7z, engine) = Outcome(BuildAesArchive(properties, Aes7zDecoder.DeriveKey(Password, salt, 19), Iv16(iv)));

        // Observado en 7z.exe 24.08: con los bits 7 y 6 en cero el segundo byte no se admite; con alguno
        // encendido, sal = bit 7 + nibble alto e IV = bit 6 + nibble bajo, y el IV se completa con ceros.
        Assert.Equal(saltBit == 1 || ivBit == 1, exe7z);
        Assert.Equal(exe7z, engine);
    }

    [Requires7zTheory]
    [InlineData(new byte[] { 0x13 }, true)]                  // un byte: solo el exponente
    [InlineData(new byte[] { 0x53, 0x0F, 1, 2, 3, 4 }, false)] // dice IV de 16 y trae 4
    public void AesProperties_ShortForms_EngineAgreesWith7zExe(byte[] properties, bool accepted)
    {
        Official7zFinder.EnsureAvailable();
        var (exe7z, engine) = Outcome(BuildAesArchive(properties, Aes7zDecoder.DeriveKey(Password, [], 19), new byte[16]));
        Assert.Equal(accepted, exe7z);
        Assert.Equal(exe7z, engine);
    }

    [Requires7zFact]
    public void AesProperties_Empty_MeanNoRoundsNoSaltZeroIv()
    {
        Official7zFinder.EnsureAvailable();
        // Observado: sin propiedades, la clave es la de 2^0 vueltas (no la de 2^19).
        var (exe7z, engine) = Outcome(BuildAesArchive([], Aes7zDecoder.DeriveKey(Password, [], 0), new byte[16]));
        Assert.True(exe7z);
        Assert.True(engine);
    }

    [Requires7zFact]
    public void AesProperties_ExtraTrailingByte_IsRejected()
    {
        Official7zFinder.EnsureAvailable();
        byte[] iv = Random(16);
        var (exe7z, engine) = Outcome(BuildAesArchive([0x53, 0x0F, .. iv, 0xAA], Aes7zDecoder.DeriveKey(Password, [], 19), iv));
        Assert.False(exe7z);
        Assert.False(engine);
    }

    [Requires7zTheory]
    [InlineData(0, true)]
    [InlineData(19, true)]
    [InlineData(24, true)]
    [InlineData(25, false)]
    [InlineData(62, false)]
    public void AesRounds_UpTo2Pow24_AreAccepted(int exponent, bool accepted)
    {
        Official7zFinder.EnsureAvailable();
        byte[] salt = Random(16), iv = Random(16);
        // Más de 24 no se deriva: se rechaza antes de mirar la clave (el motor también lo rechaza).
        byte[] key = accepted ? Aes7zDecoder.DeriveKey(Password, salt, exponent) : Random(32);
        var (exe7z, engine) = Outcome(BuildAesArchive([(byte)(0xC0 | exponent), 0xFF, .. salt, .. iv], key, iv));
        Assert.Equal(accepted, exe7z);
        Assert.Equal(exe7z, engine);
    }

    [Requires7zFact]
    public void AesRounds_0x3F_KeyIsSaltThenPasswordPaddedWithZeros()
    {
        Official7zFinder.EnsureAvailable();
        byte[] salt = Random(4), iv = Random(16);
        byte[] key = new byte[32];
        salt.CopyTo(key, 0);
        Encoding.Unicode.GetBytes(Password).CopyTo(key, salt.Length);
        byte[] properties = [0xFF, (byte)(((salt.Length - 1) << 4) | 15), .. salt, .. iv];

        var (exe7z, engine) = Outcome(BuildAesArchive(properties, key, iv));
        Assert.True(exe7z);
        Assert.True(engine);
    }

    [Requires7zFact]
    public void What7zExeWrites_AesPpmdAndBZip2()
    {
        Official7zFinder.EnsureAvailable();
        string dir = Path.Combine(Path.GetTempPath(), $"caja_negra_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string text = Path.Combine(dir, "texto.txt");
            File.WriteAllText(text, string.Concat(Enumerable.Range(0, 20000).Select(i => $"linea {i} del experimento\n")));

            // 7zAES: 2^19 vueltas, sin sal, IV de 16 bytes.
            string aes = Path.Combine(dir, "aes.7z");
            Run7z($"a \"{aes}\" \"{text}\" -p{Password}");
            byte[] aesProperties = FirstFolder(aes).Coders.Single(c => c.MethodId.AsSpan().SequenceEqual(MethodId.Aes256)).Properties!;
            Assert.Equal([0x53, 0x0F], aesProperties.AsSpan(0, 2).ToArray());
            Assert.Equal(18, aesProperties.Length);

            int[] expectedOrders = [4, 4, 5, 5, 6, 8, 16, 24, 32];
            string[] expectedBZip2 = ["BZh1", "BZh3", "BZh5", "BZh7", "BZh9", "BZh9", "BZh9", "BZh9", "BZh9"];
            for (int level = 1; level <= 9; level++)
            {
                string ppmd = Path.Combine(dir, $"ppmd{level}.7z");
                Run7z($"a \"{ppmd}\" \"{text}\" -m0=PPMd -mx={level}");
                Assert.Equal(expectedOrders[level - 1], FirstFolder(ppmd).Coders[0].Properties![0]);

                string bzip2 = Path.Combine(dir, $"bzip2_{level}.7z");
                Run7z($"a \"{bzip2}\" \"{text}\" -m0=BZip2 -mx={level}");
                Assert.Equal(expectedBZip2[level - 1], Encoding.ASCII.GetString(File.ReadAllBytes(bzip2), SignatureHeader.HeaderSize, 4));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Folder FirstFolder(string path)
    {
        using var source = new FileRandomAccessSource(path);
        return ArchiveHeaderReader.Read(source).Folders[0];
    }
}
