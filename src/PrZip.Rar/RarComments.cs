// Port a C# de unrar 7.3.1 (RARLAB), de arccmt.cpp (GetComment, DoGetComment, ReadCommentData), de
// arcread.cpp (SearchSubBlock, ReadSubData) y de los nombres de flujo de win32stm.cpp (GetStreamNameNTFS)
// y extinfo.cpp. Copyright (c) Alexander L. Roshal. Los comentarios de archivo de RAR 1.5-2.x, que unrar 7
// ya no lee, siguen el bloque de comentario descripto en el TECHNOTE.DOC de RAR 1.55 (RARLAB).
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

using PrZip.Exceptions;
using PrZip.Primitives.Checksum;

namespace PrZip.Rar;

/// <summary>
/// Un comentario de RAR: el texto, o <see cref="Problem"/> si estaba pero no se pudo leer (dañado, cifrado
/// sin contraseña, método desconocido). Nunca impide extraer el resto.
/// </summary>
internal sealed record RarComment(string? Text, string? Problem);

internal sealed partial class RarArchive
{
    // Como unrar (ReadSubData): un comentario o un flujo que se lee a memoria no pasa de 16 MB.
    private const long MaxSubDataToMemory = 0x1000000;

    private string? _lastFileName;
    private bool _currentIsStream;

    /// <summary>
    /// Comentario del archivo RAR (el de la cabecera principal en RAR 1.4-2.x o la cabecera de servicio "CMT"
    /// en RAR 3.x y 5), o null si no tiene. Con las cabeceras cifradas solo se lee si se dio la contraseña.
    /// </summary>
    public RarComment? Comment { get; private set; }

    /// <summary>Lee el comentario al abrir, como unrar, y deja la posición como estaba.</summary>
    private void ReadArchiveComment()
    {
        if (!_mainComment)
        {
            return;
        }

        long savePos = _stream.Position, saveCur = _curBlockPos, saveNext = _nextBlockPos;
        var saveType = _curHeaderType;
        try
        {
            Comment = DoGetComment();
        }
        catch (Exception ex) when (ex is PrZipException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            Comment = new RarComment(null, "no se pudo leer: " + ex.Message);
        }
        finally
        {
            _brokenHeader = false;
            _truncated = false;
            _failedHeaderDecryption = false;
            _endArcNextVolume = false;
            _curBlockPos = saveCur;
            _nextBlockPos = saveNext;
            _curHeaderType = saveType;
            _stream.Position = savePos;
        }
    }

    private RarComment? DoGetComment()
    {
        uint cmtLength;
        if (Format == RarFormat.Rar14)
        {
            _stream.Position = _sfxSize + RarConst.SizeofMainHead14;
            int lo = _stream.ReadByte(), hi = _stream.ReadByte();
            if (lo < 0 || hi < 0)
            {
                return new RarComment(null, "dañado (truncado)");
            }

            cmtLength = (uint)(lo | (hi << 8));
        }
        else
        {
            if (_mainCommentInHeader)
            {
                // Comentario viejo (RAR 2.9 y anteriores) dentro de la cabecera principal.
                _stream.Position = _sfxSize + RarConst.SizeofMarkHead3 + RarConst.SizeofMainHead3;
                if (ReadHeader() == 0 || _curHeaderType != HeaderType.Head3Cmt)
                {
                    return null;
                }
            }
            else
            {
                // Comentario actual (RAR 3.0 y posteriores): cabecera de servicio "CMT".
                _stream.Position = _firstBlockPos;
                if (!SearchSubBlock(RarConst.SubheadTypeCmt))
                {
                    return null;
                }

                return ReadCommentData();
            }

            if (_brokenHeader || _commHead.HeadSize < RarConst.SizeofCommHead)
            {
                return new RarComment(null, "dañado (CRC de la cabecera)");
            }

            cmtLength = _commHead.HeadSize - RarConst.SizeofCommHead;
        }

        bool packed = Format == RarFormat.Rar14 ? _mainPackComment14 : _commHead.Method != 0x30;
        if (packed)
        {
            uint unpVer = _commHead.UnpVer, unpLength = _commHead.UnpSize;
            if (Format != RarFormat.Rar14 && (unpVer < 15 || unpVer > RarConst.VerUnpack || _commHead.Method > 0x35))
            {
                return new RarComment(null, "comprimido con un método desconocido");
            }

            bool cmt13 = false;
            if (Format == RarFormat.Rar14)
            {
                int lo = _stream.ReadByte(), hi = _stream.ReadByte();
                if (lo < 0 || hi < 0 || cmtLength < 2)
                {
                    return new RarComment(null, "dañado (truncado)");
                }

                unpLength = (uint)(lo | (hi << 8));
                cmtLength -= 2;
                cmt13 = true;
                unpVer = 15;
            }

            byte[] packedData = ReadUpTo(cmtLength);
            byte[] data = UnpackComment(packedData, unpVer, unpLength, cmt13);
            if (Format != RarFormat.Rar14 && (Crc32Algorithm.Compute(data) & 0xffff) != _commHead.CommCrc)
            {
                return new RarComment(null, "dañado (CRC)");
            }

            return Text(RarStrings.OemToWide(data));
        }

        if (cmtLength == 0)
        {
            return null;
        }

        byte[] raw = ReadUpTo(cmtLength); // Un comentario más corto que lo declarado se toma como venga.
        if (Format != RarFormat.Rar14 && (Crc32Algorithm.Compute(raw) & 0xffff) != _commHead.CommCrc)
        {
            return new RarComment(null, "dañado (CRC)");
        }

        return Text(RarStrings.OemToWide(raw));
    }

    private static RarComment? Text(string text) => text.Length > 0 ? new RarComment(text, null) : null;

    private byte[] ReadUpTo(uint length)
    {
        var buffer = new byte[length];
        int read = _stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>Busca una cabecera de servicio por nombre desde la posición actual (SearchSubBlock).</summary>
    private bool SearchSubBlock(string type)
    {
        while (ReadHeader() != 0 && _curHeaderType != HeaderType.EndArc)
        {
            if (_curHeaderType == HeaderType.Service && _subHead.FileName == type)
            {
                return true;
            }

            SeekToNext();
        }

        return false;
    }

    /// <summary>Texto del comentario de la cabecera de servicio actual (ReadCommentData).</summary>
    private RarComment? ReadCommentData()
    {
        var (data, problem) = ReadSubData(_subHead);
        if (data == null)
        {
            return new RarComment(null, problem);
        }

        if (Format == RarFormat.Rar50)
        {
            return Text(RarStrings.UtfToWide(data));
        }

        return (_subHead.FileAttr & RarConst.SubheadFlagsCmtUnicode) != 0
            ? Text(RarStrings.RawToWide(data))
            : Text(RarStrings.AnsiToWide(data));
    }

    /// <summary>
    /// Datos de una cabecera de servicio a memoria, con su suma verificada (ReadSubData). Devuelve el problema
    /// en vez de lanzar: un comentario ilegible se informa, no detiene la lectura del archivo.
    /// </summary>
    private (byte[]? Data, string? Problem) ReadSubData(RarFileHeader hd)
    {
        if (_brokenHeader)
        {
            return (null, "dañado (CRC de la cabecera)");
        }

        if (hd.Method > 5 || hd.UnpVer > (Format == RarFormat.Rar50 ? RarConst.VerUnpack7 : RarConst.VerUnpack))
        {
            return (null, "comprimido con un método desconocido");
        }

        if (hd.PackSize == 0 && !hd.SplitAfter)
        {
            return ([], null);
        }

        if (hd.UnknownUnpSize || hd.UnpSize < 0 || hd.UnpSize > MaxSubDataToMemory)
        {
            return (null, "tamaño desconocido o mayor que 16 MB");
        }

        using var crypt = hd.Encrypted ? new RarCryptData(_keyCache) : null;
        byte[]? hashKey = null;
        if (crypt != null)
        {
            if (_password == null)
            {
                return (null, "cifrado: hace falta la contraseña");
            }

            hashKey = new byte[32];
            var pswCheck = new byte[RarConst.SizePswCheck];
            if (!crypt.SetCryptKeys(hd.CryptMethod, FilePassword(hd, _password), hd.SaltSet ? hd.Salt : null, hd.InitV,
                    hd.Lg2Count, hashKey, pswCheck))
            {
                return (null, "cifrado desconocido");
            }

            if (hd.UsePswCheck && !pswCheck.AsSpan().SequenceEqual(hd.PswCheck))
            {
                return (null, "contraseña incorrecta");
            }
        }

        var hash = new RarHash(hd.HashType);
        using var memory = new MemoryStream();
        var output = new HashingWriter(memory, hash);
        _stream.Position = _nextBlockPos - hd.PackSize;
        var input = new PackedDataReader(this, hd.PackSize, crypt, followVolumes: false);
        try
        {
            if (hd.Method == 0)
            {
                Unstore(input, output, hd.UnpSize);
            }
            else
            {
                using var unpack = new Unpack();
                unpack.SetIO((buf, off, count) => input.Read(buf.AsSpan(off, count)), output);
                unpack.Init(hd.WinSize, false);
                unpack.SetDestSize(hd.UnpSize);
                unpack.DoUnpack(hd.UnpVer, false);
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException or PrZipException)
        {
            return (null, "dañado (datos comprimidos)");
        }

        if (!hash.Matches(hd, hd.UseHashKey ? hashKey : null) || output.Written != hd.UnpSize)
        {
            return (null, hd.Encrypted && !hd.UsePswCheck ? "contraseña incorrecta o datos dañados" : "dañado (suma)");
        }

        return (memory.ToArray(), null);
    }

    /// <summary>
    /// Descomprime un comentario viejo que ya está en memoria (RAR 1.4-2.x), con ventana de 64 KB y, en RAR
    /// 1.4, el cifrado fijo de comentarios.
    /// </summary>
    private byte[] UnpackComment(byte[] packed, uint unpVer, uint unpLength, bool cmt13)
    {
        if (cmt13)
        {
            using var crypt = new RarCryptData(_keyCache);
            crypt.SetCmt13Encryption();
            crypt.DecryptBlock(packed);
        }

        using var memory = new MemoryStream();
        var output = new HashingWriter(memory, new RarHash(HashType.None));
        int pos = 0;
        using var unpack = new Unpack();
        unpack.SetIO((buf, off, count) =>
        {
            int n = Math.Min(count, packed.Length - pos);
            Array.Copy(packed, pos, buf, off, n);
            pos += n;
            return n;
        }, output);
        unpack.Init(0x10000, false);
        unpack.SetDestSize(unpLength);
        unpack.DoUnpack(unpVer, false);
        return memory.ToArray();
    }

    /// <summary>
    /// Comentario guardado en la cabecera de un archivo (RAR 1.5-2.x, comando "cf"). Bloque del TECHNOTE.DOC:
    /// HEAD_CRC(2) HEAD_TYPE(1)=0x75 HEAD_FLAGS(2) HEAD_SIZE(2) UNP_SIZE(2) UNP_VER(1) METHOD(1) COMM_CRC(2)
    /// y el texto, comprimido si METHOD no es 0x30.
    /// </summary>
    private RarComment? DecodeFileComment(RarFileHeader hd)
    {
        var block = hd.CommentBlock;
        if (block == null)
        {
            return null;
        }

        try
        {
            if (block[2] != 0x75)
            {
                return new RarComment(null, "dañado (cabecera del comentario)");
            }

            uint headSize = (uint)(block[5] | (block[6] << 8));
            uint unpSize = (uint)(block[7] | (block[8] << 8));
            uint unpVer = block[9], method = block[10];
            uint commCrc = (uint)(block[11] | (block[12] << 8));
            if (headSize < RarConst.SizeofCommHead || headSize > block.Length)
            {
                return new RarComment(null, "dañado (tamaño del comentario)");
            }

            byte[] data = block[RarConst.SizeofCommHead..(int)headSize];
            if (method != 0x30)
            {
                if (unpVer < 15 || unpVer > RarConst.VerUnpack || method > 0x35)
                {
                    return new RarComment(null, "comprimido con un método desconocido");
                }

                data = UnpackComment(data, unpVer, unpSize, cmt13: false);
            }

            if ((Crc32Algorithm.Compute(data) & 0xffff) != commCrc)
            {
                return new RarComment(null, "dañado (CRC)");
            }

            return Text(RarStrings.OemToWide(data));
        }
        catch (Exception ex) when (ex is PrZipException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return new RarComment(null, "dañado (datos comprimidos)");
        }
    }

    /// <summary>
    /// Flujo NTFS alternativo de la cabecera actual, si lo es: "STM" en RAR 3.x y 5 (nombre en UTF-16 o UTF-8,
    /// GetStreamNameNTFS) o el subbloque STREAM_HEAD de RAR 2.x (nombre en ANSI). Pertenece al último archivo
    /// leído y se informa como "archivo:flujo"; no se extrae. La continuación de un flujo partido entre
    /// volúmenes no se repite.
    /// </summary>
    private RarEntry? TryStreamEntry()
    {
        if (_lastFileName == null)
        {
            return null;
        }

        string streamName;
        long size;
        bool encrypted = false;
        if (_curHeaderType == HeaderType.Service && _subHead.FileName == RarConst.SubheadTypeStream && !_subHead.SplitBefore)
        {
            streamName = Format == RarFormat.Rar50 ? RarStrings.UtfToWide(_subHead.SubData) : RarStrings.RawToWide(_subHead.SubData);
            size = _subHead.UnknownUnpSize ? -1 : _subHead.UnpSize;
            encrypted = _subHead.Encrypted;
        }
        else if (_curHeaderType == HeaderType.Head3OldService && _subBlockSubType == RarConst.StreamHead)
        {
            streamName = RarStrings.AnsiToWide(_streamHead.Name);
            size = _streamHead.UnpSize;
        }
        else
        {
            return null;
        }

        if (!streamName.StartsWith(':'))
        {
            streamName = ":" + streamName;
        }

        return new RarEntry(_lastFileName + streamName, false, size, size < 0, 0, null, null, null, 0,
            RarRedirection.None, null, encrypted, 0, 0, 0, false, false, false, "ninguna", IsAlternateStream: true);
    }
}
