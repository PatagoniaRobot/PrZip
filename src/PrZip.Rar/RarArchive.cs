// Port a C# de unrar 7.3.1 (RARLAB), de archive.cpp y extract.cpp (CheckUnpVer, UnstoreFile y la
// verificación de la suma). Copyright (c) Alexander L. Roshal.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

using PrZip.Exceptions;

namespace PrZip.Rar;

/// <summary>Una entrada de un archivo RAR. El nombre usa '/' como separador.</summary>
internal sealed record RarEntry(
    string Name,
    bool IsDirectory,
    long Size,
    bool SizeUnknown,
    long PackedSize,
    DateTimeOffset? LastWriteTime,
    DateTimeOffset? CreationTime,
    DateTimeOffset? LastAccessTime,
    uint Attributes,
    RarRedirection Redirection,
    string? RedirectionTarget,
    bool IsEncrypted,
    int Method,
    int UnpackVersion,
    ulong DictionarySize,
    bool IsSolid,
    bool SplitBefore,
    bool SplitAfter,
    string Checksum,
    bool IsAlternateStream = false,
    RarComment? Comment = null);

/// <summary>
/// Lector de archivos RAR (1.4 a 7) recorrido en orden, como lo hace unrar: <see cref="MoveNext"/> avanza a
/// la próxima entrada y <see cref="ExtractCurrent"/> la descomprime verificando su suma.
/// </summary>
internal sealed partial class RarArchive : IDisposable
{
    private Stream _stream;
    private bool _ownsStream;
    private readonly string? _password;
    private readonly Func<string, Stream>? _openVolume;
    private string? _volumeName;
    private string? _pendingSplit;
    private bool _started;
    private bool _extracted;
    private int _fileCount;
    private Unpack? _unpack;
    private bool _disposed;

    /// <summary>Versión del formato del archivo.</summary>
    public RarFormat Format { get; private set; }

    public bool IsSolid { get; private set; }

    public bool IsVolume { get; private set; }

    public bool IsFirstVolume { get; private set; }

    public bool NewNumbering { get; private set; }

    public bool IsLocked { get; private set; }

    public bool HasRecoveryRecord { get; private set; }

    /// <summary>Las cabeceras (nombres incluidos) están cifradas (-hp).</summary>
    public bool HeadersEncrypted => _encrypted;

    /// <summary>Bytes del autoextraíble antes del archivo RAR (0 si no hay).</summary>
    public long SfxSize => _sfxSize;

    /// <summary>Entrada actual; válida después de un <see cref="MoveNext"/> que devolvió true.</summary>
    public RarEntry Current { get; private set; } = null!;

    /// <summary>Volumen que se está leyendo (null si se abrió desde un stream sin nombre).</summary>
    public string? VolumeName => _volumeName;

    /// <summary>Abre un RAR desde un stream. Sin <paramref name="path"/> no se pueden seguir volúmenes.</summary>
    public RarArchive(Stream stream, string? password = null, bool ownsStream = false)
        : this(stream, password, ownsStream, null, null, exactVolume: true)
    {
    }

    /// <summary>
    /// Abre un RAR con nombre: si es un volumen que no es el primero y el primero existe, se lee desde el
    /// primero (como unrar), y los volúmenes siguientes se abren con <paramref name="openVolume"/>.
    /// </summary>
    public RarArchive(Stream stream, string? password, bool ownsStream, string? path, Func<string, Stream>? openVolume, bool exactVolume = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("El lector RAR necesita un stream legible y con posicionamiento.", nameof(stream));
        }

        _stream = stream;
        _ownsStream = ownsStream;
        _password = string.IsNullOrEmpty(password) ? null : password;
        _volumeName = path;
        _openVolume = openVolume ?? (path != null ? OpenFile : null);
        try
        {
            IsArchive();
            if (!exactVolume && IsVolume && !IsFirstVolume && path != null)
            {
                string first = RarVolumes.VolNameToFirstName(path, NewNumbering);
                if (!string.Equals(Path.GetFullPath(first), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && File.Exists(first))
                {
                    SwitchVolume(first);
                    IsArchive();
                }
            }

            ReadArchiveComment();
        }
        catch
        {
            if (_ownsStream)
            {
                _stream.Dispose();
            }

            throw;
        }
    }

    private static FileStream OpenFile(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

    public static RarArchive Open(string path, string? password = null) => Open(path, password, exactVolume: false);

    internal static RarArchive Open(string path, string? password, bool exactVolume)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new RarArchive(OpenFile(path), password, ownsStream: true, path, null, exactVolume);
    }

    private void SwitchVolume(string name)
    {
        var next = _openVolume!(name);
        if (_ownsStream)
        {
            _stream.Dispose();
        }

        _stream = next;
        _ownsStream = true;
        _volumeName = name;
    }

    /// <summary>
    /// Pasa al volumen siguiente (MergeArchive de unrar). Con <paramref name="forData"/> queda posicionado en
    /// los datos de la continuación del archivo partido y devuelve su tamaño comprimido.
    /// </summary>
    private long MergeArchive(bool forData)
    {
        if (_volumeName == null || _openVolume == null)
        {
            throw new UnsupportedMethodException("RAR: el archivo sigue en otro volumen, pero se abrió sin nombre de archivo.");
        }

        bool prevEncrypted = _encrypted;
        string next = RarVolumes.NextVolumeName(_volumeName, !NewNumbering);
        if (!File.Exists(next))
        {
            string alt = RarVolumes.NextVolumeName(_volumeName, true);
            if (!File.Exists(alt))
            {
                throw new ArchiveTruncatedException($"RAR: falta el volumen '{Path.GetFileName(next)}'.");
            }

            next = alt;
        }

        SwitchVolume(next);
        IsArchive();
        if (_encrypted != prevEncrypted)
        {
            // Como unrar: el cifrado de cabeceras no puede cambiar en medio de un juego de volúmenes.
            throw new CorruptArchiveException($"RAR: el volumen '{Path.GetFileName(next)}' no corresponde al juego (cambia el cifrado de cabeceras).");
        }

        if (!forData)
        {
            return 0;
        }

        while (true)
        {
            if (ReadHeader() == 0 || _curHeaderType == HeaderType.EndArc)
            {
                throw new CorruptArchiveException($"RAR: el volumen '{Path.GetFileName(next)}' no trae la continuación de '{_fileHead.FileName}'.");
            }

            if (_curHeaderType == HeaderType.File)
            {
                if (_brokenHeader)
                {
                    throw new CorruptArchiveException($"RAR: la cabecera de '{_fileHead.FileName}' está dañada (CRC) en '{Path.GetFileName(next)}'.");
                }

                _stream.Position = _nextBlockPos - _fileHead.PackSize;
                return _fileHead.PackSize;
            }

            SeekToNext();
        }
    }

    /// <summary>
    /// Avanza a la próxima entrada: archivo, carpeta o flujo NTFS alternativo de la entrada anterior
    /// (<see cref="RarEntry.IsAlternateStream"/>, que se informa pero no se extrae). Las demás cabeceras de
    /// servicio (comentario, permisos, registro de recuperación) se saltean.
    /// </summary>
    public bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            SkipSolidIfNeeded();
            SeekToNext();
        }

        _started = true;
        _extracted = false;
        _currentIsStream = false;
        while (true)
        {
            int size = ReadHeader();
            if (size == 0)
            {
                if (_failedHeaderDecryption)
                {
                    throw new InvalidPasswordOrCorruptDataException("RAR: contraseña incorrecta o cabeceras cifradas dañadas.");
                }

                if (_brokenHeader)
                {
                    throw new CorruptArchiveException("RAR: cabecera dañada.");
                }

                if (_truncated)
                {
                    throw new ArchiveTruncatedException("RAR: el archivo está truncado.");
                }

                return false;
            }

            switch (_curHeaderType)
            {
                case HeaderType.File:
                    if (_brokenHeader)
                    {
                        throw new CorruptArchiveException($"RAR: la cabecera de '{_fileHead.FileName}' está dañada (CRC).");
                    }

                    if (_fileHead.SplitBefore)
                    {
                        // Continuación de un archivo partido que empezó en el volumen anterior.
                        if (_pendingSplit == null)
                        {
                            throw new ArchiveTruncatedException($"RAR: '{_fileHead.FileName}' empieza en un volumen anterior que no está.");
                        }

                        _pendingSplit = _fileHead.SplitAfter ? _fileHead.FileName : null;
                        SeekToNext();
                        continue;
                    }

                    _pendingSplit = _fileHead.SplitAfter ? _fileHead.FileName : null;
                    Current = ToEntry(_fileHead) with { Comment = DecodeFileComment(_fileHead) };
                    _lastFileName = _fileHead.FileName;
                    _fileCount++;
                    return true;
                case HeaderType.EndArc:
                    if (_endArcNextVolume)
                    {
                        MergeArchive(forData: false);
                        continue;
                    }

                    if (_pendingSplit != null)
                    {
                        throw new ArchiveTruncatedException($"RAR: '{_pendingSplit}' sigue en un volumen siguiente que no está.");
                    }

                    return false;
                default:
                    if (_brokenHeader)
                    {
                        throw new CorruptArchiveException("RAR: cabecera de servicio dañada (CRC).");
                    }

                    if (TryStreamEntry() is { } stream)
                    {
                        Current = stream;
                        _currentIsStream = true;
                        return true;
                    }

                    SeekToNext();
                    break;
            }
        }
    }

    /// <summary>
    /// En un archivo sólido cada entrada comprimida depende de las anteriores: si no se extrajo, se la
    /// decodifica igual (a ninguna parte) para que la ventana quede como la espera la siguiente.
    /// </summary>
    private void SkipSolidIfNeeded()
    {
        var hd = _fileHead;
        if (_extracted || _currentIsStream || !IsSolid || hd.Dir || hd.Method == 0 || hd.RedirType != RarRedirection.None)
        {
            return;
        }

        ExtractCurrent(Stream.Null);
    }

    private static RarEntry ToEntry(RarFileHeader hd)
    {
        string checksum = hd.HashType switch
        {
            HashType.Crc32 => "CRC32",
            HashType.Blake2 => "BLAKE2sp",
            HashType.Rar14 => "RAR14",
            _ => "ninguna"
        };

        return new RarEntry(
            hd.FileName,
            hd.Dir,
            hd.Dir ? 0 : (hd.UnknownUnpSize ? -1 : hd.UnpSize),
            hd.UnknownUnpSize,
            hd.PackSize,
            hd.MTime,
            hd.CTime,
            hd.ATime,
            hd.FileAttr,
            hd.RedirType,
            hd.RedirName.Length > 0 ? hd.RedirName : null,
            hd.Encrypted,
            hd.Method,
            (int)hd.UnpVer,
            hd.WinSize,
            hd.Solid,
            hd.SplitBefore,
            hd.SplitAfter,
            checksum);
    }

    /// <summary>
    /// Descomprime la entrada actual en <paramref name="destination"/> y verifica su suma. Las carpetas y las
    /// redirecciones (enlaces) no tienen contenido que extraer.
    /// </summary>
    public void ExtractCurrent(Stream destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        if (!_started || Current == null)
        {
            throw new InvalidOperationException("No hay entrada actual: llamar antes a MoveNext.");
        }

        if (_extracted)
        {
            throw new InvalidOperationException("La entrada actual ya se extrajo.");
        }

        if (_currentIsStream)
        {
            throw new NotSupportedException("RAR: los flujos NTFS alternativos se informan, no se extraen.");
        }

        _extracted = true;
        var hd = _fileHead;
        if (hd.Dir || hd.RedirType != RarRedirection.None)
        {
            return;
        }

        CheckUnpVer(hd);

        if (hd.SplitBefore)
        {
            throw new ArchiveTruncatedException($"RAR: '{hd.FileName}' empieza en un volumen anterior.");
        }

        RarCryptData? crypt = null;
        byte[]? hashKey = null;
        if (hd.Encrypted)
        {
            string pwd = FilePassword(hd, RequirePassword($"'{hd.FileName}' está cifrado"));
            crypt = _fileCrypt ??= new RarCryptData(_keyCache);
            hashKey = new byte[32];
            var pswCheck = new byte[RarConst.SizePswCheck];
            bool set = crypt.SetCryptKeys(hd.CryptMethod, pwd, hd.SaltSet ? hd.Salt : null, hd.InitV, hd.Lg2Count, hashKey, pswCheck);
            if (!set)
            {
                throw new UnsupportedMethodException($"RAR: '{hd.FileName}' usa un cifrado desconocido.");
            }

            if (hd.UsePswCheck && !_brokenHeader && !pswCheck.AsSpan().SequenceEqual(hd.PswCheck))
            {
                throw new PasswordIncorrectException($"RAR: contraseña incorrecta para '{hd.FileName}'.");
            }
        }

        // Sin valor de chequeo (RAR 1.x-4.x), una contraseña incorrecta solo se nota como datos dañados.
        bool cantTellPassword = hd.Encrypted && !hd.UsePswCheck;

        var hash = new RarHash(hd.HashType);
        _stream.Position = _nextBlockPos - hd.PackSize;
        var input = new PackedDataReader(this, hd.PackSize, crypt);
        var output = new HashingWriter(destination, hash);

        try
        {
            if (hd.Method == 0)
            {
                Unstore(input, output, hd.UnknownUnpSize ? long.MaxValue : hd.UnpSize);
            }
            else
            {
                _unpack ??= new Unpack();
                _unpack.SetIO((buf, off, count) => input.Read(buf.AsSpan(off, count)), output);
                _unpack.Init(EffectiveWindow(hd), hd.Solid);
                _unpack.SetDestSize(hd.UnknownUnpSize ? long.MaxValue : hd.UnpSize);

                // RAR 1.3 - 1.5 no marcan cada archivo como sólido: vale la marca del archivo completo.
                if (Format != RarFormat.Rar50 && hd.UnpVer <= 15)
                {
                    _unpack.DoUnpack(15, _fileCount > 1 && IsSolid);
                }
                else
                {
                    _unpack.DoUnpack(hd.UnpVer, hd.Solid);
                }
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or OverflowException)
        {
            // Un dato hostil o dañado que lleva el descompresor fuera de sus tablas o de la ventana.
            if (cantTellPassword)
            {
                throw new InvalidPasswordOrCorruptDataException($"RAR: no se pudo descomprimir '{hd.FileName}': contraseña incorrecta o datos dañados.", ex);
            }

            throw new CorruptArchiveException($"RAR: los datos comprimidos de '{hd.FileName}' están dañados.", ex);
        }

        if (input.Truncated)
        {
            throw new ArchiveTruncatedException($"RAR: faltan datos de '{hd.FileName}': el archivo está truncado.");
        }

        // Las partes de un archivo partido ya se leyeron todas: la cabecera actual es la de la última, que es
        // la que trae la suma del archivo completo.
        _pendingSplit = null;

        if (!hash.Matches(hd, hd.UseHashKey ? hashKey : null))
        {
            if (cantTellPassword)
            {
                throw new InvalidPasswordOrCorruptDataException($"RAR: la suma {Current.Checksum} de '{hd.FileName}' no coincide: contraseña incorrecta o datos dañados.");
            }

            throw new CorruptArchiveException($"RAR: la suma {Current.Checksum} de '{hd.FileName}' no coincide: datos dañados.");
        }

        if (!hd.UnknownUnpSize && output.Written != hd.UnpSize)
        {
            if (cantTellPassword)
            {
                throw new InvalidPasswordOrCorruptDataException($"RAR: '{hd.FileName}' dio {output.Written} bytes en lugar de {hd.UnpSize}: contraseña incorrecta o datos dañados.");
            }

            throw new CorruptArchiveException($"RAR: '{hd.FileName}' dio {output.Written} bytes en lugar de {hd.UnpSize}: datos dañados.");
        }
    }

    private void CheckUnpVer(RarFileHeader hd)
    {
        bool wrongVer = Format == RarFormat.Rar50
            ? hd.UnpVer > RarConst.VerUnpack7
            : hd.UnpVer < 13 || hd.UnpVer > RarConst.VerUnpack;

        if (hd.Method == 0)
        {
            wrongVer = false;
        }

        if (hd.CryptMethod == CryptMethod.Unknown)
        {
            wrongVer = true;
        }

        if (wrongVer)
        {
            throw new UnsupportedMethodException($"RAR: '{hd.FileName}' necesita una versión de descompresión desconocida ({hd.UnpVer}).");
        }
    }

    private static void Unstore(PackedDataReader input, HashingWriter output, long destUnpSize)
    {
        var buffer = new byte[1 << 16];
        while (true)
        {
            int readSize = input.Read(buffer);
            if (readSize <= 0)
            {
                break;
            }

            int writeSize = readSize < destUnpSize ? readSize : (int)destUnpSize;
            if (writeSize > 0)
            {
                output.Write(buffer.AsSpan(0, writeSize));
                destUnpSize -= writeSize;
            }
        }
    }

    private readonly RarKeyCache _keyCache = new();
    private RarCryptData? _headerCrypt;
    private RarCryptData? _fileCrypt;

    private string RequirePassword(string what) =>
        _password ?? throw new InvalidPasswordOrCorruptDataException($"RAR: {what}: hace falta la contraseña.");

    /// <summary>Cabeceras cifradas de RAR 3.x: cada una trae su sal de 8 bytes.</summary>
    private RarCryptData CreateHeaderDecryptor30(byte[] salt)
    {
        string pwd = RequirePassword("las cabeceras están cifradas");
        var c = _headerCrypt ??= new RarCryptData(_keyCache);
        c.SetCryptKeys(CryptMethod.Rar30, pwd, salt, null, 0, null, null);
        return c;
    }

    /// <summary>
    /// Cabeceras cifradas de RAR5: la clave sale de la cabecera de cifrado y cada cabecera trae su IV. Con el
    /// valor de chequeo de 64 bits, una contraseña incorrecta se distingue de un archivo dañado.
    /// </summary>
    private RarCryptData CreateHeaderDecryptor50(byte[] initV)
    {
        string pwd = RequirePassword("las cabeceras están cifradas");
        var c = _headerCrypt ??= new RarCryptData(_keyCache);
        var pswCheck = new byte[RarConst.SizePswCheck];
        bool set = c.SetCryptKeys(CryptMethod.Rar50, pwd, _cryptSalt, initV, _cryptLg2Count, null, pswCheck);
        if (set && _cryptUsePswCheck && !_brokenHeader && !pswCheck.AsSpan().SequenceEqual(_cryptPswCheck))
        {
            throw new PasswordIncorrectException("RAR: contraseña incorrecta (las cabeceras no se pueden descifrar).");
        }

        return c;
    }

    /// <summary>
    /// Archivos cifrados por RAR para DOS (RAR 1.5-2.x, sistema MS-DOS): la contraseña se usó en OEM, así que
    /// se pasa a OEM y se vuelve a leer como ANSI antes de derivar la clave (ConvertDosPassword de unrar).
    /// </summary>
    private string FilePassword(RarFileHeader hd, string pwd)
    {
        if (Format != RarFormat.Rar15 || hd.HostOS != RarConst.HostMsDos)
        {
            return pwd;
        }

        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var text = System.Globalization.CultureInfo.CurrentCulture.TextInfo;
        try
        {
            var oem = System.Text.Encoding.GetEncoding(text.OEMCodePage);
            var ansi = System.Text.Encoding.GetEncoding(text.ANSICodePage);
            return ansi.GetString(oem.GetBytes(pwd));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return pwd;
        }
    }

    /// <summary>
    /// Ventana a reservar. Fuera de un archivo sólido, ninguna distancia puede superar lo que ese archivo ya
    /// produjo, así que alcanza con su tamaño (redondeado a potencia de 2: RAR 1.5 a 4 usan la ventana como
    /// máscara). Antes se reservaba el diccionario declarado: un RAR de pocos bytes que declaraba 1 GB hacía
    /// reservar 1 GB por archivo. En un archivo sólido (también los RAR 1.5, sólidos por archivo completo) o
    /// con tamaño desconocido se respeta el declarado.
    /// </summary>
    /// <summary>Ventana de descompresión reservada hasta ahora (para tests).</summary>
    internal ulong AllocatedWindow => _unpack?.AllocWinSize ?? 0;

    private ulong EffectiveWindow(RarFileHeader hd)
    {
        if (IsSolid || hd.Solid || hd.UnknownUnpSize || hd.UnpSize < 0)
        {
            return hd.WinSize;
        }

        ulong needed = Math.Max((ulong)hd.UnpSize, 0x40000UL);
        ulong pow2 = needed >= (1UL << 62) ? needed : System.Numerics.BitOperations.RoundUpToPowerOf2(needed);
        return Math.Min(hd.WinSize, pow2);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _unpack?.Dispose();
        _headerCrypt?.Dispose();
        _fileCrypt?.Dispose();
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }

    /// <summary>
    /// Datos comprimidos de la entrada actual (ComprDataIO::UnpRead): al terminar una parte de un archivo
    /// partido sigue en el volumen siguiente, y con cifrado descifra lo leído de corrido entre volúmenes. Los
    /// datos de una cabecera de servicio (comentario) se leen sin seguir a otro volumen.
    /// </summary>
    internal sealed class PackedDataReader(RarArchive owner, long packedSize, IBlockDecryptor? decryptor = null, bool followVolumes = true)
    {
        private long _left = packedSize;

        public bool Truncated { get; private set; }

        public int Read(Span<byte> buffer)
        {
            // Con cifrado se lee de a bloques de 16 bytes, como ComprDataIO::UnpRead.
            int count = decryptor != null ? buffer.Length & ~(RawRead.CryptBlockSize - 1) : buffer.Length;
            int total = 0;
            while (count > 0)
            {
                if (_left <= 0)
                {
                    if (Truncated || !followVolumes || !owner._fileHead.SplitAfter)
                    {
                        break;
                    }

                    _left = owner.MergeArchive(forData: true);
                    continue;
                }

                int toRead = (int)Math.Min(count, _left);
                int n = owner._stream.ReadAtLeast(buffer.Slice(total, toRead), toRead, throwOnEndOfStream: false);
                total += n;
                count -= n;
                if (n < toRead)
                {
                    Truncated = true;
                    _left = 0;
                    break;
                }

                _left -= n;
                if (decryptor == null)
                {
                    break; // sin cifrado alcanza con lo que dé esta parte
                }
            }

            decryptor?.DecryptBlock(buffer[..total]);
            return total;
        }
    }

    /// <summary>Salida descomprimida con su suma (ComprDataIO::UnpWrite).</summary>
    internal sealed class HashingWriter(Stream destination, RarHash hash)
    {
        public long Written { get; private set; }

        public void Write(ReadOnlySpan<byte> data)
        {
            hash.Update(data);
            destination.Write(data);
            Written += data.Length;
        }
    }
}
