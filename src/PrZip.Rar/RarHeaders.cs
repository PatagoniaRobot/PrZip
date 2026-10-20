// Port a C# de unrar 7.3.1 (RARLAB), de headers.hpp y headers5.hpp. Copyright (c) Alexander L. Roshal.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

namespace PrZip.Rar;

/// <summary>Versión del formato de archivo RAR.</summary>
internal enum RarFormat
{
    /// <summary>RAR 1.4 (firma "RE~^").</summary>
    Rar14,

    /// <summary>RAR 1.5 a 4.x.</summary>
    Rar15,

    /// <summary>RAR 5.0 en adelante (también WinRAR 7).</summary>
    Rar50
}

internal enum HeaderType
{
    // RAR 5.0. Unknown tiene que ser el mayor: lo que pase de ahí se rechaza.
    Mark = 0x00,
    Main = 0x01,
    File = 0x02,
    Service = 0x03,
    Crypt = 0x04,
    EndArc = 0x05,
    Unknown = 0xff,

    // RAR 1.5 - 4.x (los comunes se traducen a los de RAR 5.0 al leer).
    Head3Mark = 0x72,
    Head3Main = 0x73,
    Head3File = 0x74,
    Head3Cmt = 0x75,
    Head3Av = 0x76,
    Head3OldService = 0x77,
    Head3Protect = 0x78,
    Head3Sign = 0x79,
    Head3Service = 0x7a,
    Head3EndArc = 0x7b
}

internal enum HostSystemType
{
    Windows,
    Unix,
    Unknown
}

/// <summary>Redirecciones del sistema de archivos (mismos valores que en el campo extra de RAR5).</summary>
internal enum RarRedirection
{
    None = 0,
    UnixSymlink,
    WindowsSymlink,
    Junction,
    HardLink,
    FileCopy,
    Unknown
}

internal enum HashType
{
    None,
    Crc32,
    Rar14,
    Blake2
}

internal enum CryptMethod
{
    None,
    Rar13,
    Rar15,
    Rar20,
    Rar30,
    Rar50,
    Unknown
}

internal static class RarConst
{
    public const int SizeofMarkHead3 = 7;
    public const int SizeofMainHead14 = 7;
    public const int SizeofMainHead3 = 13;
    public const int SizeofFileHead14 = 21;
    public const int SizeofFileHead3 = 32;
    public const int SizeofShortBlockHead = 7;
    public const int SizeofCommHead = 13;
    public const int SizeofMarkHead5 = 8;
    public const int SizeofShortBlockHead5 = 7;

    public const uint VerPack5 = 50;
    public const uint VerPack7 = 70;
    public const uint VerUnpack = 29;
    public const uint VerUnpack5 = 50;
    public const uint VerUnpack7 = 70;
    public const uint VerUnknown = 9999;

    public const int MaxSfxSize = 0x400000;
    public const int MaxPathSize = 0x10000;
    public const int SizeSalt30 = 8;
    public const int SizeSalt50 = 16;
    public const int SizeInitV = 16;
    public const int SizePswCheck = 8;
    public const int SizePswCheckCsum = 4;
    public const int CryptVersion = 0;
    public const int Crypt5KdfLg2CountMax = 24;
    public const int Blake2DigestSize = 32;

    /// <summary>Diccionario máximo que acepta unrar 7 (UNPACK_MAX_DICT, 64 GB).</summary>
    public const ulong UnpackMaxDict = 0x1000000000;

    // RAR 1.5 - 4.x: cabecera principal.
    public const uint MhdVolume = 0x0001;
    public const uint MhdComment = 0x0002;
    public const uint MhdLock = 0x0004;
    public const uint MhdSolid = 0x0008;
    public const uint MhdPackComment = 0x0010;
    public const uint MhdNewNumbering = 0x0010;
    public const uint MhdAv = 0x0020;
    public const uint MhdProtect = 0x0040;
    public const uint MhdPassword = 0x0080;
    public const uint MhdFirstVolume = 0x0100;

    // RAR 1.5 - 4.x: cabecera de archivo.
    public const uint LhdSplitBefore = 0x0001;
    public const uint LhdSplitAfter = 0x0002;
    public const uint LhdPassword = 0x0004;
    public const uint LhdComment = 0x0008;
    public const uint LhdSolid = 0x0010;
    public const uint LhdWindowMask = 0x00e0;
    public const uint LhdDirectory = 0x00e0;
    public const uint LhdLarge = 0x0100;
    public const uint LhdUnicode = 0x0200;
    public const uint LhdSalt = 0x0400;
    public const uint LhdVersion = 0x0800;
    public const uint LhdExtTime = 0x1000;
    public const uint SkipIfUnknown = 0x4000;
    public const uint LongBlock = 0x8000;

    public const uint EarcNextVolume = 0x0001;
    public const uint EarcDataCrc = 0x0002;
    public const uint EarcRevSpace = 0x0004;
    public const uint EarcVolNumber = 0x0008;

    public const uint SubheadFlagsInherited = 0x80000000;
    public const uint SubheadFlagsCmtUnicode = 0x00000001;

    public const string SubheadTypeCmt = "CMT";
    public const string SubheadTypeStream = "STM";

    // Subbloques de RAR 2.9 y anteriores.
    public const int NtAclHead = 0x104;
    public const int StreamHead = 0x105;
    public const int UoHead = 0x101;

    // Sistemas de origen.
    public const int Host5Windows = 0;
    public const int Host5Unix = 1;
    public const int HostMsDos = 0;
    public const int HostOs2 = 1;
    public const int HostWin32 = 2;
    public const int HostUnix = 3;
    public const int HostMacOs = 4;
    public const int HostBeOs = 5;
    public const int HostMax = 6;

    // RAR 5.0: banderas comunes de bloque.
    public const uint HflExtra = 0x0001;
    public const uint HflData = 0x0002;
    public const uint HflSkipIfUnknown = 0x0004;
    public const uint HflSplitBefore = 0x0008;
    public const uint HflSplitAfter = 0x0010;
    public const uint HflChild = 0x0020;
    public const uint HflInherited = 0x0040;

    public const uint MhflVolume = 0x0001;
    public const uint MhflVolNumber = 0x0002;
    public const uint MhflSolid = 0x0004;
    public const uint MhflProtect = 0x0008;
    public const uint MhflLock = 0x0010;

    public const uint FhflDirectory = 0x0001;
    public const uint FhflUTime = 0x0002;
    public const uint FhflCrc32 = 0x0004;
    public const uint FhflUnpUnknown = 0x0008;

    public const uint EhflNextVolume = 0x0001;
    public const uint ChflCryptPswCheck = 0x0001;

    public const uint FciSolid = 0x00000040;
    public const uint FciRar5Compat = 0x00100000;

    public const ulong MhExtraLocator = 0x01;
    public const ulong MhExtraMetadata = 0x02;

    public const ulong FhExtraCrypt = 0x01;
    public const ulong FhExtraHash = 0x02;
    public const ulong FhExtraHTime = 0x03;
    public const ulong FhExtraVersion = 0x04;
    public const ulong FhExtraRedir = 0x05;
    public const ulong FhExtraUOwner = 0x06;
    public const ulong FhExtraSubData = 0x07;

    public const uint FhExtraHashBlake2 = 0x00;

    public const uint FhExtraHTimeUnixTime = 0x01;
    public const uint FhExtraHTimeMTime = 0x02;
    public const uint FhExtraHTimeCTime = 0x04;
    public const uint FhExtraHTimeATime = 0x08;
    public const uint FhExtraHTimeUnixNs = 0x10;

    public const uint FhExtraCryptPswCheck = 0x01;
    public const uint FhExtraCryptHashMac = 0x02;

    public const uint FhExtraRedirDir = 0x01;
}

/// <summary>Cabecera de archivo o de servicio (FileHeader de unrar), con lo que usa el lector.</summary>
internal sealed class RarFileHeader
{
    public HeaderType HeaderType;
    public uint Flags;
    public uint HeadSize;
    public uint HeadCrc;

    public byte HostOS;
    public uint UnpVer;
    public byte Method;
    public uint FileAttr;
    public string FileName = "";
    public byte[] SubData = [];

    public DateTimeOffset? MTime;
    public DateTimeOffset? CTime;
    public DateTimeOffset? ATime;

    public long PackSize;
    public long UnpSize;
    public bool UnknownUnpSize;

    public HashType HashType;
    public uint Crc32;
    public byte[] Digest = new byte[RarConst.Blake2DigestSize];

    public uint FileFlags;
    public bool SplitBefore;
    public bool SplitAfter;

    public bool Encrypted;
    public CryptMethod CryptMethod;
    public bool SaltSet;
    public byte[] Salt = new byte[RarConst.SizeSalt50];
    public byte[] InitV = new byte[RarConst.SizeInitV];
    public bool UsePswCheck;
    public byte[] PswCheck = new byte[RarConst.SizePswCheck];
    public bool UseHashKey;
    public uint Lg2Count;

    public bool Solid;
    public bool Dir;
    public bool CommentInHeader;
    public bool Version;
    public ulong WinSize;
    public bool Inherited;
    public bool LargeFile;
    public bool SubBlock;

    public HostSystemType HSType;
    public RarRedirection RedirType;
    public string RedirName = "";
    public bool DirTarget;

    /// <summary>
    /// Bloque de comentario (0x75) que RAR 1.5-2.x guarda al final de la cabecera de un archivo, tal cual.
    /// unrar 7 ya no lo lee; el formato sale del TECHNOTE.DOC de RAR 1.55.
    /// </summary>
    public byte[]? CommentBlock;
}
