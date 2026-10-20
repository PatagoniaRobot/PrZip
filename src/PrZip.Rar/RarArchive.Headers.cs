// Port a C# de unrar 7.3.1 (RARLAB), de arcread.cpp y archive.cpp. Copyright (c) Alexander L. Roshal.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

using System.Text;
using PrZip.Exceptions;

namespace PrZip.Rar;

internal sealed partial class RarArchive
{
    // Estado del archivo (miembros de la clase Archive de unrar).
    private long _sfxSize;
    private long _curBlockPos;
    private long _nextBlockPos;
    private HeaderType _curHeaderType = HeaderType.Unknown;
    private bool _brokenHeader;
    private bool _failedHeaderDecryption;
    private bool _encrypted;
    private uint _volNumber;

    private readonly RarFileHeader _fileHead = new();
    private readonly RarFileHeader _subHead = new();

    // Cabecera principal.
    private uint _mainHeadSize;
    private bool _mainCommentInHeader;

    // Cabecera de cifrado de RAR5.
    private uint _cryptHeadSize;
    private bool _cryptUsePswCheck;
    private uint _cryptLg2Count;
    private readonly byte[] _cryptSalt = new byte[RarConst.SizeSalt50];
    private readonly byte[] _cryptPswCheck = new byte[RarConst.SizePswCheck];

    // Fin de archivo.
    private bool _endArcNextVolume;

    // Subbloque viejo (RAR 2.x) leído por último, para la excepción de CRC de UO_HEAD.
    private int _subBlockSubType;

    // Comentario del archivo (arccmt.cpp): si hay, dónde empieza la primera cabecera después de la principal,
    // la cabecera de comentario de RAR 2.x y la marca de comentario comprimido de RAR 1.4.
    private bool _mainComment;
    private bool _mainPackComment14;
    private long _firstBlockPos;
    private CommentHeader _commHead;

    // Subbloque de flujo NTFS de RAR 2.x (STREAM_HEAD).
    private StreamHeader20 _streamHead;

    private record struct CommentHeader(uint HeadSize, uint UnpSize, uint UnpVer, uint Method, uint CommCrc);

    private record struct StreamHeader20(uint UnpSize, uint UnpVer, uint Method, uint StreamCrc, byte[] Name);

    /// <summary>Busca la firma (también tras un autoextraíble) y lee hasta la cabecera principal.</summary>
    private void IsArchive()
    {
        _encrypted = false;
        _brokenHeader = false;

        Span<byte> mark = stackalloc byte[RarConst.SizeofMarkHead3 + 1];
        _stream.Position = 0;
        if (_stream.ReadAtLeast(mark[..RarConst.SizeofMarkHead3], RarConst.SizeofMarkHead3, throwOnEndOfStream: false) != RarConst.SizeofMarkHead3)
        {
            throw new CorruptArchiveException("No es un archivo RAR (demasiado corto).");
        }

        _sfxSize = 0;
        RarFormat? type = IsSignature(mark[..RarConst.SizeofMarkHead3], out bool future);
        if (type != null)
        {
            Format = type.Value;
            if (Format == RarFormat.Rar14)
            {
                _stream.Position -= RarConst.SizeofMarkHead3;
            }
        }
        else if (!future)
        {
            var buffer = new byte[RarConst.MaxSfxSize];
            const int curPos = 1;
            _stream.Position = curPos;
            int readSize = _stream.ReadAtLeast(buffer.AsSpan(0, buffer.Length - 16), buffer.Length - 16, throwOnEndOfStream: false);
            for (int i = 0; i < readSize; i++)
            {
                if (buffer[i] != 0x52)
                {
                    continue;
                }

                type = IsSignature(buffer.AsSpan(i, readSize - i), out future);
                if (future)
                {
                    break;
                }

                if (type == null)
                {
                    continue;
                }

                if (type == RarFormat.Rar14 && i > 0 && curPos < 28 && readSize > 31)
                {
                    int d = 28 - curPos;
                    if (buffer[d] != 0x52 || buffer[d + 1] != 0x53 || buffer[d + 2] != 0x46 || buffer[d + 3] != 0x58)
                    {
                        type = null;
                        continue;
                    }
                }

                Format = type.Value;
                _sfxSize = curPos + i;
                _stream.Position = _sfxSize;
                if (Format is RarFormat.Rar15 or RarFormat.Rar50)
                {
                    _stream.ReadAtLeast(mark[..RarConst.SizeofMarkHead3], RarConst.SizeofMarkHead3, throwOnEndOfStream: false);
                }

                break;
            }

            if (_sfxSize == 0 && !future)
            {
                throw new CorruptArchiveException("No es un archivo RAR: no se encontró la firma.");
            }
        }

        if (future)
        {
            throw new UnsupportedMethodException("RAR: versión de formato posterior a RAR5, no soportada.");
        }

        if (Format == RarFormat.Rar50)
        {
            if (_stream.ReadAtLeast(mark.Slice(RarConst.SizeofMarkHead3, 1), 1, throwOnEndOfStream: false) != 1 || mark[RarConst.SizeofMarkHead3] != 0)
            {
                throw new CorruptArchiveException("RAR5: firma incompleta.");
            }

        }

        bool startFound = false;
        while (ReadHeader() != 0)
        {
            SeekToNext();
            if (_curHeaderType == HeaderType.Main)
            {
                startFound = true;
                break;
            }
        }

        if (_brokenHeader || !startFound)
        {
            throw new CorruptArchiveException("RAR: la cabecera principal está dañada o falta.");
        }

        _firstBlockPos = _stream.Position;
        _mainComment = _mainCommentInHeader;

        // Como unrar, se leen las cabeceras hasta el primer archivo. En RAR 2.x es la única forma de saber si
        // este es el primer volumen (la cabecera principal no lo marca; en los nuevos queda verificado), y en
        // todos los formatos así se sabe si hay comentario (una cabecera de servicio "CMT"). Con las cabeceras
        // cifradas hace falta la contraseña; si es incorrecta no se dice acá: lo dice la lectura de entradas.
        if (!_encrypted || _password != null)
        {
            long savePos = _stream.Position, saveCur = _curBlockPos, saveNext = _nextBlockPos;
            var saveType = _curHeaderType;
            try
            {
                while (ReadHeader() != 0)
                {
                    if (_curHeaderType == HeaderType.Service)
                    {
                        if (IsVolume)
                        {
                            IsFirstVolume = !_subHead.SplitBefore;
                        }
                    }
                    else if (_curHeaderType == HeaderType.File)
                    {
                        if (IsVolume)
                        {
                            IsFirstVolume = !_fileHead.SplitBefore;
                        }

                        break;
                    }
                    else if (_curHeaderType == HeaderType.EndArc)
                    {
                        break;
                    }

                    SeekToNext();
                }
            }
            catch (PrZipException) when (_encrypted)
            {
                // Contraseña incorrecta para las cabeceras: se informa al recorrer las entradas.
            }

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

    private static RarFormat? IsSignature(ReadOnlySpan<byte> d, out bool future)
    {
        future = false;
        if (d.Length >= 1 && d[0] == 0x52)
        {
            if (d.Length >= 4 && d[1] == 0x45 && d[2] == 0x7e && d[3] == 0x5e)
            {
                return RarFormat.Rar14;
            }

            if (d.Length >= 7 && d[1] == 0x61 && d[2] == 0x72 && d[3] == 0x21 && d[4] == 0x1a && d[5] == 0x07)
            {
                if (d[6] == 0)
                {
                    return RarFormat.Rar15;
                }

                if (d[6] == 1)
                {
                    return RarFormat.Rar50;
                }

                if (d[6] > 1 && d[6] < 5)
                {
                    future = true;
                }
            }
        }

        return null;
    }

    /// <summary>Hay firma RAR (RAR 1.4, 1.5–4 o 5) al comienzo de estos bytes.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> head) => IsSignature(head, out _) != null;

    /// <summary>
    /// Busca una firma RAR como lo hace unrar con un autoextraíble: desde el byte 1, en los primeros 4 MB.
    /// </summary>
    public static bool FindSignature(Stream stream)
    {
        var buffer = new byte[RarConst.MaxSfxSize];
        stream.Position = 0;
        int readSize = stream.ReadAtLeast(buffer.AsSpan(0, buffer.Length - 16), buffer.Length - 16, throwOnEndOfStream: false);
        for (int i = 0; i < readSize; i++)
        {
            if (buffer[i] == 0x52 && IsSignature(buffer.AsSpan(i, readSize - i), out _) is RarFormat f && f != RarFormat.Rar14)
            {
                return true;
            }
        }

        return false;
    }

    private void SeekToNext() => _stream.Position = _nextBlockPos;

    private uint FullHeaderSize(uint size)
    {
        if (_encrypted)
        {
            size = (size + RawRead.CryptBlockSize - 1) & ~(uint)(RawRead.CryptBlockSize - 1);
            size += Format == RarFormat.Rar50 ? (uint)RarConst.SizeInitV : (uint)RarConst.SizeSalt30;
        }

        return size;
    }

    private static long SafeAdd(long v1, long v2, long f) =>
        v1 >= 0 && v2 >= 0 && v1 <= long.MaxValue - v2 ? v1 + v2 : f;

    /// <summary>Lee la cabecera en la posición actual. Devuelve su tamaño, o 0 al final o si está rota.</summary>
    private int ReadHeader()
    {
        if (_failedHeaderDecryption)
        {
            return 0;
        }

        _curBlockPos = _stream.Position;
        int readSize = Format switch
        {
            RarFormat.Rar14 => ReadHeader14(),
            RarFormat.Rar15 => ReadHeader15(),
            _ => ReadHeader50()
        };

        if (readSize > 0 && _nextBlockPos <= _curBlockPos)
        {
            _brokenHeader = true;
            readSize = 0;
        }

        if (readSize == 0)
        {
            _curHeaderType = HeaderType.Unknown;
        }

        return readSize;
    }

    /// <summary>Fin inesperado: es error salvo que la posición sea justo el final (RAR 1.5 sin fin de archivo).</summary>
    private bool UnexpectedEnd()
    {
        long arcSize = _stream.Length;
        return _curBlockPos != arcSize || _nextBlockPos != arcSize;
    }

    private bool _truncated;

    private int ReadHeader15()
    {
        var raw = new RawRead(_stream);
        bool decrypt = _encrypted && _curBlockPos > _sfxSize + RarConst.SizeofMarkHead3;

        if (decrypt)
        {
            var salt = new byte[RarConst.SizeSalt30];
            if (_stream.ReadAtLeast(salt, salt.Length, throwOnEndOfStream: false) != salt.Length)
            {
                _truncated = UnexpectedEnd();
                return 0;
            }

            raw.SetCrypt(CreateHeaderDecryptor30(salt));
        }

        raw.Read(RarConst.SizeofShortBlockHead);
        if (raw.Size == 0)
        {
            _truncated = UnexpectedEnd();
            return 0;
        }

        uint headCrc = raw.Get2();
        uint headerTypeRaw = raw.Get1();
        uint flags = raw.Get2();
        uint headSize = raw.Get2();
        var headerType = (HeaderType)headerTypeRaw;

        if (headSize < RarConst.SizeofShortBlockHead)
        {
            _brokenHeader = true;
            return 0;
        }

        headerType = headerType switch
        {
            HeaderType.Head3Main => HeaderType.Main,
            HeaderType.Head3File => HeaderType.File,
            HeaderType.Head3Service => HeaderType.Service,
            HeaderType.Head3EndArc => HeaderType.EndArc,
            _ => headerType
        };
        _curHeaderType = headerType;

        if (headerType == HeaderType.Head3Cmt)
        {
            raw.Read(RarConst.SizeofCommHead - RarConst.SizeofShortBlockHead);
        }
        else if (headerType == HeaderType.Main && (flags & RarConst.MhdComment) != 0)
        {
            raw.Read(RarConst.SizeofMainHead3 - RarConst.SizeofShortBlockHead);
        }
        else
        {
            raw.Read((int)headSize - RarConst.SizeofShortBlockHead);
        }

        _nextBlockPos = _curBlockPos + FullHeaderSize(headSize);

        switch (headerType)
        {
            case HeaderType.Main:
                _mainHeadSize = headSize;
                raw.Get2(); // HighPosAV
                raw.Get4(); // PosAV
                IsVolume = (flags & RarConst.MhdVolume) != 0;
                IsSolid = (flags & RarConst.MhdSolid) != 0;
                IsLocked = (flags & RarConst.MhdLock) != 0;
                HasRecoveryRecord = (flags & RarConst.MhdProtect) != 0;
                _encrypted = (flags & RarConst.MhdPassword) != 0;
                _mainCommentInHeader = (flags & RarConst.MhdComment) != 0;
                IsFirstVolume = (flags & RarConst.MhdFirstVolume) != 0;
                NewNumbering = (flags & RarConst.MhdNewNumbering) != 0;
                break;
            case HeaderType.File:
            case HeaderType.Service:
                {
                    bool fileBlock = headerType == HeaderType.File;
                    var hd = fileBlock ? _fileHead : _subHead;
                    ResetHeader(hd);
                    hd.HeaderType = headerType;
                    hd.Flags = flags;
                    hd.HeadSize = headSize;
                    hd.HeadCrc = headCrc;

                    hd.SplitBefore = (flags & RarConst.LhdSplitBefore) != 0;
                    hd.SplitAfter = (flags & RarConst.LhdSplitAfter) != 0;
                    hd.Encrypted = (flags & RarConst.LhdPassword) != 0;
                    hd.SaltSet = (flags & RarConst.LhdSalt) != 0;
                    hd.Solid = fileBlock && (flags & RarConst.LhdSolid) != 0;
                    hd.SubBlock = !fileBlock && (flags & RarConst.LhdSolid) != 0;
                    hd.Dir = (flags & RarConst.LhdWindowMask) == RarConst.LhdDirectory;
                    hd.WinSize = hd.Dir ? 0 : 0x10000UL << (int)((flags & RarConst.LhdWindowMask) >> 5);
                    hd.CommentInHeader = (flags & RarConst.LhdComment) != 0;
                    hd.Version = (flags & RarConst.LhdVersion) != 0;

                    uint dataSize = raw.Get4();
                    uint lowUnpSize = raw.Get4();
                    hd.HostOS = raw.Get1();
                    hd.HashType = HashType.Crc32;
                    hd.Crc32 = raw.Get4();
                    uint fileTime = raw.Get4();
                    hd.UnpVer = raw.Get1();
                    hd.Method = (byte)(raw.Get1() - 0x30);
                    int nameSize = raw.Get2();
                    hd.FileAttr = raw.Get4();

                    if (hd.UnpVer < 20 && (hd.FileAttr & 0x10) != 0)
                    {
                        hd.Dir = true;
                    }

                    hd.CryptMethod = CryptMethod.None;
                    if (hd.Encrypted)
                    {
                        hd.CryptMethod = hd.UnpVer switch
                        {
                            13 => CryptMethod.Rar13,
                            15 => CryptMethod.Rar15,
                            20 or 26 => CryptMethod.Rar20,
                            _ => CryptMethod.Rar30
                        };
                    }

                    hd.HSType = HostSystemType.Unknown;
                    if (hd.HostOS is RarConst.HostUnix or RarConst.HostBeOs)
                    {
                        hd.HSType = HostSystemType.Unix;
                    }
                    else if (hd.HostOS < RarConst.HostMax)
                    {
                        hd.HSType = HostSystemType.Windows;
                    }

                    hd.RedirType = RarRedirection.None;
                    if (hd.HostOS == RarConst.HostUnix && (hd.FileAttr & 0xF000) == 0xA000)
                    {
                        hd.RedirType = RarRedirection.UnixSymlink;
                        hd.RedirName = "";
                    }

                    hd.Inherited = !fileBlock && (hd.FileAttr & RarConst.SubheadFlagsInherited) != 0;
                    hd.LargeFile = (flags & RarConst.LhdLarge) != 0;

                    uint highPackSize, highUnpSize;
                    if (hd.LargeFile)
                    {
                        highPackSize = raw.Get4();
                        highUnpSize = raw.Get4();
                        hd.UnknownUnpSize = lowUnpSize == 0xffffffff && highUnpSize == 0xffffffff;
                    }
                    else
                    {
                        highPackSize = highUnpSize = 0;
                        hd.UnknownUnpSize = lowUnpSize == 0xffffffff;
                    }

                    hd.PackSize = (long)(((ulong)highPackSize << 32) | dataSize);
                    hd.UnpSize = (long)(((ulong)highUnpSize << 32) | lowUnpSize);
                    if (hd.UnknownUnpSize)
                    {
                        hd.UnpSize = long.MaxValue;
                    }

                    int readNameSize = Math.Min(nameSize, RarConst.MaxPathSize);
                    var fileName = raw.GetBytes(readNameSize);

                    if (fileBlock)
                    {
                        hd.FileName = "";
                        if ((flags & RarConst.LhdUnicode) != 0)
                        {
                            int length = fileName.AsSpan().IndexOf((byte)0);
                            if (length < 0)
                            {
                                length = readNameSize;
                            }

                            length++;
                            if (readNameSize > length)
                            {
                                hd.FileName = RarStrings.DecodeUnicodeName(fileName.AsSpan(0, length - 1), fileName.AsSpan(length));
                            }
                        }

                        if (hd.FileName.Length == 0)
                        {
                            hd.FileName = RarStrings.OemToWide(fileName);
                        }

                        ConvertFileHeader(hd);
                    }
                    else
                    {
                        hd.FileName = Encoding.ASCII.GetString(RarStrings.UntilZero(fileName));
                        int optSize = (int)hd.HeadSize - nameSize - RarConst.SizeofFileHead3;
                        if ((flags & RarConst.LhdSalt) != 0)
                        {
                            optSize -= RarConst.SizeSalt30;
                        }

                        if (optSize > 0)
                        {
                            hd.SubData = raw.GetBytes(optSize);
                        }

                        if (hd.FileName == RarConst.SubheadTypeCmt)
                        {
                            _mainComment = true;
                        }
                    }

                    if ((flags & RarConst.LhdSalt) != 0)
                    {
                        raw.GetB(hd.Salt.AsSpan(0, RarConst.SizeSalt30));
                    }

                    hd.MTime = RarTimes.FromDos(fileTime, out var mtimeLocal);
                    if ((flags & RarConst.LhdExtTime) != 0)
                    {
                        uint tflags = raw.Get2();
                        // Como unrar: la tabla apunta a los tiempos de FileHead aunque sea un subbloque.
                        DateTime? baseM = mtimeLocal;
                        for (int i = 0; i < 4; i++)
                        {
                            uint rmode = tflags >> ((3 - i) * 4);
                            if ((rmode & 8) == 0 || i == 3)
                            {
                                continue;
                            }

                            DateTime? local = baseM;
                            if (i != 0)
                            {
                                RarTimes.FromDos(raw.Get4(), out local);
                            }

                            uint reminder = 0;
                            uint count = rmode & 3;
                            for (uint j = 0; j < count; j++)
                            {
                                byte cur = raw.Get1();
                                reminder |= (uint)cur << (int)((j + 3 - count) * 8);
                            }

                            var t = RarTimes.ExtendLocal(local, (rmode & 4) != 0, reminder);
                            switch (i)
                            {
                                case 0: _fileHead.MTime = t; break;
                                case 1: _fileHead.CTime = t; break;
                                case 2: _fileHead.ATime = t; break;
                            }
                        }
                    }

                    _nextBlockPos = SafeAdd(_nextBlockPos, hd.PackSize, 0);

                    uint headerCrc = raw.GetCrc15(hd.CommentInHeader);
                    if (hd.HeadCrc != headerCrc)
                    {
                        _brokenHeader = true;
                    }

                    // El comentario de archivo de RAR 1.5-2.x va al final de la cabecera y queda fuera de su CRC
                    // (por eso GetCrc15 cuenta solo lo leído): se guarda el bloque para decodificarlo aparte.
                    if (fileBlock && hd.CommentInHeader && raw.DataLeft >= RarConst.SizeofCommHead)
                    {
                        hd.CommentBlock = raw.GetBytes(raw.DataLeft);
                    }
                }

                break;
            case HeaderType.EndArc:
                _endArcNextVolume = (flags & RarConst.EarcNextVolume) != 0;
                if ((flags & RarConst.EarcDataCrc) != 0)
                {
                    raw.Get4();
                }

                if ((flags & RarConst.EarcVolNumber) != 0)
                {
                    _volNumber = raw.Get2();
                }

                break;
            case HeaderType.Head3Cmt:
                _commHead = new CommentHeader(headSize, raw.Get2(), raw.Get1(), raw.Get1(), raw.Get2());
                break;
            case HeaderType.Head3Protect:
                {
                    uint dataSize = raw.Get4();
                    _nextBlockPos += dataSize;
                }

                break;
            case HeaderType.Head3OldService:
                {
                    uint dataSize = raw.Get4();
                    _nextBlockPos += dataSize;
                    _subBlockSubType = raw.Get2();
                    raw.Get1(); // Level
                    if (_subBlockSubType == RarConst.StreamHead)
                    {
                        uint unpSize = raw.Get4();
                        uint unpVer = raw.Get1();
                        uint method = raw.Get1();
                        uint streamCrc = raw.Get4();
                        // Como unrar: un nombre de flujo de RAR 2.x tiene a lo sumo 260 bytes.
                        int nameSize = Math.Min((int)raw.Get2(), 260);
                        _streamHead = new StreamHeader20(unpSize, unpVer, method, streamCrc, raw.GetBytes(nameSize));
                    }
                }

                break;
            default:
                if ((flags & RarConst.LongBlock) != 0)
                {
                    _nextBlockPos += raw.Get4();
                }

                break;
        }

        // Diferencia deliberada con unrar 7.3.1: una cabecera de archivo con comentario (RAR 1.5-2.x, "cf") ya
        // se verificó arriba hasta el nombre, que es lo que cubre su CRC según el TECHNOTE.DOC. unrar la vuelve
        // a verificar entera, comentario incluido, y la da por dañada: UnRAR 7.01 cuenta un error en todo
        // archivo así, aunque RAR 2.90 lo prueba sin errores.
        bool fileWithComment = headerType == HeaderType.File && _fileHead.CommentInHeader;
        uint crc = raw.GetCrc15(false);
        if (headCrc != crc && !fileWithComment && headerType != HeaderType.Head3Sign && headerType != HeaderType.Head3Av &&
            (headerType != HeaderType.Head3OldService || _subBlockSubType != RarConst.UoHead))
        {
            bool recovered = false;
            if (headerType == HeaderType.EndArc && (flags & RarConst.EarcRevSpace) != 0)
            {
                long save = _stream.Position;
                _stream.Position = _stream.Length - 7;
                recovered = true;
                for (int j = 0; j < 7; j++)
                {
                    if (_stream.ReadByte() != 0)
                    {
                        recovered = false;
                    }
                }

                _stream.Position = save;
            }

            if (!recovered)
            {
                _brokenHeader = true;
                if (decrypt)
                {
                    _failedHeaderDecryption = true;
                    return 0;
                }
            }
        }

        return raw.Size;
    }

    private int ReadHeader50()
    {
        var raw = new RawRead(_stream);
        bool decrypt = _encrypted && _curBlockPos > _sfxSize + RarConst.SizeofMarkHead5;

        if (decrypt)
        {
            var headersInitV = new byte[RarConst.SizeInitV];
            if (_stream.ReadAtLeast(headersInitV, headersInitV.Length, throwOnEndOfStream: false) != headersInitV.Length)
            {
                _truncated = UnexpectedEnd();
                return 0;
            }

            raw.SetCrypt(CreateHeaderDecryptor50(headersInitV));
        }

        const int firstReadSize = 7;
        if (raw.Read(firstReadSize) < firstReadSize)
        {
            _truncated = UnexpectedEnd();
            return 0;
        }

        uint headCrc = raw.Get4();
        int sizeBytes = raw.GetVSize(4);
        ulong blockSize = raw.GetV();

        if (blockSize == 0 || sizeBytes == 0)
        {
            _brokenHeader = true;
            return 0;
        }

        // Como mucho 3 bytes de tamaño: cabeceras de hasta 2 MB.
        if (blockSize > 0x200000)
        {
            _brokenHeader = true;
            return 0;
        }

        int sizeToRead = (int)blockSize - (firstReadSize - sizeBytes - 4);
        uint headerSize = 4 + (uint)sizeBytes + (uint)blockSize;

        if (sizeToRead < 0 || headerSize < RarConst.SizeofShortBlockHead5)
        {
            _brokenHeader = true;
            return 0;
        }

        raw.Read(sizeToRead);
        if (raw.Size < headerSize)
        {
            _truncated = true;
            return 0;
        }

        uint headerCrc = raw.GetCrc50();
        ulong headerTypeRaw = raw.GetV();
        var headerType = headerTypeRaw >= (ulong)HeaderType.Unknown ? HeaderType.Unknown : (HeaderType)headerTypeRaw;
        uint flags = (uint)raw.GetV();
        _curHeaderType = headerType;

        bool badCrc = headCrc != headerCrc;
        if (badCrc)
        {
            _brokenHeader = true;
            if (decrypt)
            {
                _failedHeaderDecryption = true;
                return 0;
            }
        }

        ulong extraSize = 0;
        if ((flags & RarConst.HflExtra) != 0)
        {
            extraSize = raw.GetV();
            if (extraSize >= headerSize)
            {
                _brokenHeader = true;
                return 0;
            }
        }

        ulong dataSize = 0;
        if ((flags & RarConst.HflData) != 0)
        {
            dataSize = raw.GetV();
        }

        _nextBlockPos = _curBlockPos + FullHeaderSize(headerSize);
        _nextBlockPos = dataSize > long.MaxValue ? 0 : SafeAdd(_nextBlockPos, (long)dataSize, 0);

        switch (headerType)
        {
            case HeaderType.Crypt:
                {
                    _cryptHeadSize = headerSize;
                    uint cryptVersion = (uint)raw.GetV();
                    if (cryptVersion > RarConst.CryptVersion)
                    {
                        throw new UnsupportedMethodException($"RAR5: versión de cifrado de cabeceras desconocida ({cryptVersion}).");
                    }

                    uint encFlags = (uint)raw.GetV();
                    _cryptUsePswCheck = (encFlags & RarConst.ChflCryptPswCheck) != 0;
                    _cryptLg2Count = raw.Get1();
                    if (_cryptLg2Count > RarConst.Crypt5KdfLg2CountMax)
                    {
                        throw new UnsupportedMethodException($"RAR5: cantidad de iteraciones del cifrado fuera de rango (2^{_cryptLg2Count}).");
                    }

                    raw.GetB(_cryptSalt);
                    if (_cryptUsePswCheck)
                    {
                        raw.GetB(_cryptPswCheck);
                        var csum = new byte[RarConst.SizePswCheckCsum];
                        raw.GetB(csum);
                        var digest = System.Security.Cryptography.SHA256.HashData(_cryptPswCheck);
                        _cryptUsePswCheck = csum.AsSpan().SequenceEqual(digest.AsSpan(0, RarConst.SizePswCheckCsum));
                    }

                    _encrypted = true;
                }

                break;
            case HeaderType.Main:
                {
                    _mainHeadSize = headerSize;
                    uint arcFlags = (uint)raw.GetV();
                    IsVolume = (arcFlags & RarConst.MhflVolume) != 0;
                    IsSolid = (arcFlags & RarConst.MhflSolid) != 0;
                    IsLocked = (arcFlags & RarConst.MhflLock) != 0;
                    HasRecoveryRecord = (arcFlags & RarConst.MhflProtect) != 0;
                    NewNumbering = true;
                    _volNumber = (arcFlags & RarConst.MhflVolNumber) != 0 ? (uint)raw.GetV() : 0;
                    IsFirstVolume = IsVolume && _volNumber == 0;
                    // El campo extra (localizador, metadatos) no hace falta para extraer.
                }

                break;
            case HeaderType.File:
            case HeaderType.Service:
                {
                    bool fileBlock = headerType == HeaderType.File;
                    var hd = fileBlock ? _fileHead : _subHead;
                    ResetHeader(hd);
                    hd.HeaderType = headerType;
                    hd.Flags = flags;
                    hd.HeadSize = headerSize;
                    hd.HeadCrc = headCrc;
                    hd.LargeFile = true;

                    hd.PackSize = (long)Math.Min(dataSize, long.MaxValue);
                    hd.FileFlags = (uint)raw.GetV();
                    ulong unpSize = raw.GetV();
                    hd.UnknownUnpSize = (hd.FileFlags & RarConst.FhflUnpUnknown) != 0;
                    hd.UnpSize = hd.UnknownUnpSize || unpSize > long.MaxValue ? long.MaxValue : (long)unpSize;
                    hd.FileAttr = (uint)raw.GetV();
                    if ((hd.FileFlags & RarConst.FhflUTime) != 0)
                    {
                        hd.MTime = RarTimes.FromUnix(raw.Get4());
                    }

                    hd.HashType = HashType.None;
                    if ((hd.FileFlags & RarConst.FhflCrc32) != 0)
                    {
                        hd.HashType = HashType.Crc32;
                        hd.Crc32 = raw.Get4();
                    }

                    hd.RedirType = RarRedirection.None;

                    uint compInfo = (uint)raw.GetV();
                    hd.Method = (byte)((compInfo >> 7) & 7);
                    uint unpVer = compInfo & 0x3f;
                    hd.UnpVer = unpVer switch
                    {
                        0 => RarConst.VerPack5,
                        1 => RarConst.VerPack7,
                        _ => RarConst.VerUnknown
                    };

                    hd.HostOS = (byte)raw.GetV();
                    int nameSize = (int)Math.Min(raw.GetV(), int.MaxValue);
                    hd.Inherited = (flags & RarConst.HflInherited) != 0;

                    hd.HSType = hd.HostOS switch
                    {
                        RarConst.Host5Unix => HostSystemType.Unix,
                        RarConst.Host5Windows => HostSystemType.Windows,
                        _ => HostSystemType.Unknown
                    };

                    hd.SplitBefore = (flags & RarConst.HflSplitBefore) != 0;
                    hd.SplitAfter = (flags & RarConst.HflSplitAfter) != 0;
                    hd.SubBlock = (flags & RarConst.HflChild) != 0;
                    hd.Solid = fileBlock && (compInfo & RarConst.FciSolid) != 0;
                    hd.Dir = (hd.FileFlags & RarConst.FhflDirectory) != 0;
                    if (hd.Dir || unpVer > 1)
                    {
                        hd.WinSize = 0;
                    }
                    else
                    {
                        hd.WinSize = 0x20000UL << (int)((compInfo >> 10) & (unpVer == 0 ? 0x0fu : 0x1fu));
                        if (unpVer == 1)
                        {
                            hd.WinSize += hd.WinSize / 32 * ((compInfo >> 15) & 0x1f);
                            if ((compInfo & RarConst.FciRar5Compat) != 0)
                            {
                                hd.UnpVer = RarConst.VerPack5;
                            }

                            if (hd.WinSize > RarConst.UnpackMaxDict)
                            {
                                hd.UnpVer = RarConst.VerUnknown;
                            }
                        }
                    }

                    int readNameSize = Math.Min(nameSize, RarConst.MaxPathSize);
                    var fileName = raw.GetBytes(readNameSize);
                    hd.FileName = RarStrings.UtfToWide(fileName);

                    if (extraSize != 0)
                    {
                        ProcessExtra50(raw, (int)extraSize, hd);
                    }

                    if (fileBlock)
                    {
                        ConvertFileHeader(hd);
                    }
                    else if (hd.FileName == RarConst.SubheadTypeCmt)
                    {
                        _mainComment = true;
                    }
                }

                break;
            case HeaderType.EndArc:
                {
                    uint arcFlags = (uint)raw.GetV();
                    _endArcNextVolume = (arcFlags & RarConst.EhflNextVolume) != 0;
                }

                break;
        }

        return raw.Size;
    }

    private static void ProcessExtra50(RawRead raw, int extraSize, RarFileHeader hd)
    {
        int extraStart = raw.Size - extraSize;
        if (extraStart < raw.Position)
        {
            return;
        }

        raw.Position = extraStart;
        while (raw.DataLeft >= 2)
        {
            long fieldSize = (long)raw.GetV();
            if (fieldSize <= 0 || raw.DataLeft == 0 || fieldSize > raw.DataLeft)
            {
                break;
            }

            int nextPos = (int)(raw.Position + fieldSize);
            ulong fieldType = raw.GetV();
            fieldSize = nextPos - raw.Position;
            if (fieldSize < 0)
            {
                break;
            }

            switch (fieldType)
            {
                case RarConst.FhExtraCrypt:
                    {
                        uint encVersion = (uint)raw.GetV();
                        if (encVersion > RarConst.CryptVersion)
                        {
                            hd.CryptMethod = CryptMethod.Unknown;
                            hd.Encrypted = true;
                        }
                        else
                        {
                            uint cflags = (uint)raw.GetV();
                            hd.Lg2Count = raw.Get1();
                            if (hd.Lg2Count > RarConst.Crypt5KdfLg2CountMax)
                            {
                                hd.CryptMethod = CryptMethod.Unknown;
                                hd.Encrypted = true;
                            }
                            else
                            {
                                hd.UsePswCheck = (cflags & RarConst.FhExtraCryptPswCheck) != 0;
                                hd.UseHashKey = (cflags & RarConst.FhExtraCryptHashMac) != 0;
                                raw.GetB(hd.Salt);
                                raw.GetB(hd.InitV);
                                if (hd.UsePswCheck)
                                {
                                    raw.GetB(hd.PswCheck);
                                    var csum = new byte[RarConst.SizePswCheckCsum];
                                    raw.GetB(csum);
                                    var digest = System.Security.Cryptography.SHA256.HashData(hd.PswCheck);
                                    hd.UsePswCheck = csum.AsSpan().SequenceEqual(digest.AsSpan(0, RarConst.SizePswCheckCsum));

                                    // RAR 5.21 y anteriores ponían ceros en servicios aunque marcaran el chequeo.
                                    if (hd.HeaderType == HeaderType.Service && hd.PswCheck.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                                    {
                                        hd.UsePswCheck = false;
                                    }
                                }

                                hd.SaltSet = true;
                                hd.CryptMethod = CryptMethod.Rar50;
                                hd.Encrypted = true;
                            }
                        }
                    }

                    break;
                case RarConst.FhExtraHash:
                    {
                        uint type = (uint)raw.GetV();
                        if (type == RarConst.FhExtraHashBlake2)
                        {
                            hd.HashType = HashType.Blake2;
                            raw.GetB(hd.Digest);
                        }
                    }

                    break;
                case RarConst.FhExtraHTime:
                    if (fieldSize >= 5)
                    {
                        uint tflags = (uint)raw.GetV();
                        bool unixTime = (tflags & RarConst.FhExtraHTimeUnixTime) != 0;
                        if ((tflags & RarConst.FhExtraHTimeMTime) != 0)
                        {
                            hd.MTime = unixTime ? RarTimes.FromUnix(raw.Get4()) : RarTimes.FromWin(raw.Get8());
                        }

                        if ((tflags & RarConst.FhExtraHTimeCTime) != 0)
                        {
                            hd.CTime = unixTime ? RarTimes.FromUnix(raw.Get4()) : RarTimes.FromWin(raw.Get8());
                        }

                        if ((tflags & RarConst.FhExtraHTimeATime) != 0)
                        {
                            hd.ATime = unixTime ? RarTimes.FromUnix(raw.Get4()) : RarTimes.FromWin(raw.Get8());
                        }

                        if (unixTime && (tflags & RarConst.FhExtraHTimeUnixNs) != 0)
                        {
                            uint ns;
                            if ((tflags & RarConst.FhExtraHTimeMTime) != 0 && (ns = raw.Get4() & 0x3fffffff) < 1000000000)
                            {
                                hd.MTime = RarTimes.AddNs(hd.MTime, ns);
                            }

                            if ((tflags & RarConst.FhExtraHTimeCTime) != 0 && (ns = raw.Get4() & 0x3fffffff) < 1000000000)
                            {
                                hd.CTime = RarTimes.AddNs(hd.CTime, ns);
                            }

                            if ((tflags & RarConst.FhExtraHTimeATime) != 0 && (ns = raw.Get4() & 0x3fffffff) < 1000000000)
                            {
                                hd.ATime = RarTimes.AddNs(hd.ATime, ns);
                            }
                        }
                    }

                    break;
                case RarConst.FhExtraVersion:
                    if (fieldSize >= 1)
                    {
                        raw.GetV();
                        uint version = (uint)raw.GetV();
                        if (version != 0)
                        {
                            hd.Version = true;
                            hd.FileName += ";" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }

                    break;
                case RarConst.FhExtraRedir:
                    {
                        ulong redirType = raw.GetV();
                        uint rflags = (uint)raw.GetV();
                        ulong nameSize = raw.GetV();
                        if (nameSize > 0 && nameSize < RarConst.MaxPathSize)
                        {
                            hd.RedirType = redirType < (ulong)RarRedirection.Unknown ? (RarRedirection)redirType : RarRedirection.Unknown;
                            hd.DirTarget = (rflags & RarConst.FhExtraRedirDir) != 0;
                            hd.RedirName = RarStrings.UtfToWide(raw.GetBytes((int)nameSize));
                        }
                    }

                    break;
                case RarConst.FhExtraSubData:
                    {
                        if (hd.HeaderType == HeaderType.Service && raw.Size - nextPos == 1)
                        {
                            fieldSize++;
                        }

                        hd.SubData = raw.GetBytes((int)fieldSize);
                    }

                    break;
            }

            raw.Position = nextPos;
        }
    }

    private int ReadHeader14()
    {
        var raw = new RawRead(_stream);
        if (_curBlockPos <= _sfxSize)
        {
            raw.Read(RarConst.SizeofMainHead14);
            Span<byte> mark = stackalloc byte[4];
            raw.GetB(mark);
            uint headSize = raw.Get2();
            if (headSize < 7)
            {
                return 0;
            }

            byte flags = raw.Get1();
            _nextBlockPos = _curBlockPos + headSize;
            _curHeaderType = HeaderType.Main;
            _mainHeadSize = headSize;
            IsVolume = (flags & RarConst.MhdVolume) != 0;
            IsSolid = (flags & RarConst.MhdSolid) != 0;
            IsLocked = (flags & RarConst.MhdLock) != 0;
            _mainCommentInHeader = (flags & RarConst.MhdComment) != 0;
            _mainPackComment14 = (flags & RarConst.MhdPackComment) != 0;
        }
        else
        {
            raw.Read(RarConst.SizeofFileHead14);
            var hd = _fileHead;
            ResetHeader(hd);
            hd.HeaderType = HeaderType.File;
            uint dataSize = raw.Get4();
            hd.UnpSize = raw.Get4();
            hd.HashType = HashType.Rar14;
            hd.Crc32 = raw.Get2();
            hd.HeadSize = raw.Get2();
            if (hd.HeadSize < 21)
            {
                return 0;
            }

            uint fileTime = raw.Get4();
            hd.FileAttr = raw.Get1();
            hd.Flags = raw.Get1() | RarConst.LongBlock;
            hd.UnpVer = raw.Get1() == 2 ? 13u : 10u;
            int nameSize = raw.Get1();
            hd.Method = raw.Get1();

            hd.SplitBefore = (hd.Flags & RarConst.LhdSplitBefore) != 0;
            hd.SplitAfter = (hd.Flags & RarConst.LhdSplitAfter) != 0;
            hd.Encrypted = (hd.Flags & RarConst.LhdPassword) != 0;
            hd.CryptMethod = hd.Encrypted ? CryptMethod.Rar13 : CryptMethod.None;

            hd.PackSize = dataSize;
            hd.WinSize = 0x10000;
            hd.Dir = (hd.FileAttr & 0x10) != 0;
            hd.HostOS = RarConst.HostMsDos;
            hd.HSType = HostSystemType.Windows;
            hd.MTime = RarTimes.FromDos(fileTime, out _);

            raw.Read(nameSize);
            hd.FileName = RarStrings.OemToWide(raw.GetBytes(nameSize));
            ConvertFileHeader(hd);

            if (raw.Size != 0)
            {
                _nextBlockPos = _curBlockPos + hd.HeadSize + hd.PackSize;
            }

            _curHeaderType = HeaderType.File;
        }

        return _nextBlockPos > _curBlockPos ? raw.Size : 0;
    }

    private void ConvertFileHeader(RarFileHeader hd)
    {
        string name = hd.FileName;
        if (hd.HSType == HostSystemType.Unix)
        {
            // Nombres descompuestos de Unix/macOS a precompuestos, como unrar en Windows.
            try
            {
                name = name.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                // Unicode mal formado: se deja como viene.
            }
        }

        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            // En RAR5 '\' no es separador: es parte de un nombre Unix que Windows no admite.
            if (chars[i] == '\\' && Format == RarFormat.Rar50)
            {
                chars[i] = '_';
            }
            else if (chars[i] == '\\')
            {
                chars[i] = '/';
            }
        }

        hd.FileName = RarStrings.TruncateAtZero(new string(chars));
    }

    private static void ResetHeader(RarFileHeader hd)
    {
        hd.HeaderType = HeaderType.Unknown;
        hd.Flags = 0;
        hd.HeadSize = 0;
        hd.HeadCrc = 0;
        hd.HostOS = 0;
        hd.UnpVer = 0;
        hd.Method = 0;
        hd.FileAttr = 0;
        hd.FileName = "";
        hd.SubData = [];
        hd.MTime = hd.CTime = hd.ATime = null;
        hd.PackSize = hd.UnpSize = 0;
        hd.UnknownUnpSize = false;
        hd.HashType = HashType.None;
        hd.Crc32 = 0;
        Array.Clear(hd.Digest);
        hd.FileFlags = 0;
        hd.SplitBefore = hd.SplitAfter = false;
        hd.Encrypted = false;
        hd.CryptMethod = CryptMethod.None;
        hd.SaltSet = false;
        Array.Clear(hd.Salt);
        Array.Clear(hd.InitV);
        hd.UsePswCheck = false;
        Array.Clear(hd.PswCheck);
        hd.UseHashKey = false;
        hd.Lg2Count = 0;
        hd.Solid = hd.Dir = hd.CommentInHeader = hd.Version = false;
        hd.WinSize = 0;
        hd.Inherited = hd.LargeFile = hd.SubBlock = false;
        hd.HSType = HostSystemType.Unknown;
        hd.RedirType = RarRedirection.None;
        hd.RedirName = "";
        hd.DirTarget = false;
        hd.CommentBlock = null;
    }
}
