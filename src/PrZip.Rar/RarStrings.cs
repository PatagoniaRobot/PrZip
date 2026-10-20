// Port a C# de unrar 7.3.1 (RARLAB), de encname.cpp, unicode.cpp (UtfToWide) y strfn.cpp
// (ArcCharToWide). Copyright (c) Alexander L. Roshal.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

using System.Globalization;
using System.Text;

namespace PrZip.Rar;

internal static class RarStrings
{
    /// <summary>Bytes hasta el primer cero (los nombres de RAR son cadenas de C).</summary>
    public static ReadOnlySpan<byte> UntilZero(ReadOnlySpan<byte> src)
    {
        int zero = src.IndexOf((byte)0);
        return zero < 0 ? src : src[..zero];
    }

    /// <summary>UTF-8 a UTF-16 como unrar: se corta en la primera secuencia inválida.</summary>
    public static string UtfToWide(ReadOnlySpan<byte> src)
    {
        src = UntilZero(src);
        var sb = new StringBuilder(src.Length);
        int i = 0;
        while (i < src.Length)
        {
            uint c = src[i++], d;
            if (c < 0x80)
            {
                d = c;
            }
            else if ((c >> 5) == 6)
            {
                if (i >= src.Length || (src[i] & 0xc0) != 0x80)
                {
                    break;
                }

                d = ((c & 0x1f) << 6) | (uint)(src[i] & 0x3f);
                i++;
            }
            else if ((c >> 4) == 14)
            {
                if (i + 1 >= src.Length || (src[i] & 0xc0) != 0x80 || (src[i + 1] & 0xc0) != 0x80)
                {
                    break;
                }

                d = ((c & 0xf) << 12) | ((uint)(src[i] & 0x3f) << 6) | (uint)(src[i + 1] & 0x3f);
                i += 2;
            }
            else if ((c >> 3) == 30)
            {
                if (i + 2 >= src.Length || (src[i] & 0xc0) != 0x80 || (src[i + 1] & 0xc0) != 0x80 || (src[i + 2] & 0xc0) != 0x80)
                {
                    break;
                }

                d = ((c & 7) << 18) | ((uint)(src[i] & 0x3f) << 12) | ((uint)(src[i + 1] & 0x3f) << 6) | (uint)(src[i + 2] & 0x3f);
                i += 3;
            }
            else
            {
                break;
            }

            if (d > 0xffff)
            {
                if (d > 0x10ffff)
                {
                    continue; // UTF-8 termina en 0x10FFFF (RFC 3629)
                }

                sb.Append((char)(((d - 0x10000) >> 10) + 0xd800));
                sb.Append((char)((d & 0x3ff) + 0xdc00));
            }
            else
            {
                sb.Append((char)d);
            }
        }

        return sb.ToString();
    }

    private static Encoding? s_oem;
    private static Encoding? s_ansi;

    /// <summary>
    /// Nombres de RAR 1.x–4.x sin Unicode: en la página de códigos OEM de la máquina, como unrar en Windows
    /// (ACTW_OEM). unrar no convierte de OEM a Unicode directamente: pasa por OemToCharBuffA (OEM a ANSI, con
    /// el mapeo "best fit" de Windows, que por ejemplo lleva '╣' a '¦') y después de ANSI a Unicode. Se
    /// reproduce igual para obtener los mismos nombres que UnRAR y WinRAR. Un nombre solo ASCII no depende de
    /// nada de esto.
    /// </summary>
    public static string OemToWide(ReadOnlySpan<byte> src)
    {
        src = UntilZero(src);
        bool lowAscii = true;
        foreach (byte b in src)
        {
            if (b > 127)
            {
                lowAscii = false;
                break;
            }
        }

        if (lowAscii)
        {
            return Encoding.ASCII.GetString(src);
        }

        if (s_oem == null || s_ansi == null)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var text = CultureInfo.CurrentCulture.TextInfo;
            s_oem = GetCodePage(text.OEMCodePage, 437);
            s_ansi = GetCodePage(text.ANSICodePage, 1252);
        }

        string oem = s_oem.GetString(src);
        string ansi = s_ansi.GetString(s_ansi.GetBytes(oem)); // best fit, como OemToCharBuffA
        return TruncateAtZero(ansi);
    }

    private static Encoding GetCodePage(int codePage, int fallback)
    {
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.GetEncoding(fallback);
        }
    }

    public static string TruncateAtZero(string s)
    {
        int zero = s.IndexOf('\0', StringComparison.Ordinal);
        return zero < 0 ? s : s[..zero];
    }

    /// <summary>Texto en la página ANSI de la máquina (CharToWide de unrar en Windows), hasta el primer cero.</summary>
    public static string AnsiToWide(ReadOnlySpan<byte> src)
    {
        src = UntilZero(src);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        s_ansi ??= GetCodePage(CultureInfo.CurrentCulture.TextInfo.ANSICodePage, 1252);
        return s_ansi.GetString(src);
    }

    /// <summary>UTF-16 little endian (RawToWide de unrar), hasta el primer carácter cero.</summary>
    public static string RawToWide(ReadOnlySpan<byte> src) =>
        TruncateAtZero(Encoding.Unicode.GetString(src[..(src.Length & ~1)]));

    /// <summary>
    /// Nombre Unicode de RAR 1.5–4.x (EncodeFileName::Decode): un nombre en bytes, un cero, y a continuación
    /// el Unicode codificado contra ese nombre.
    /// </summary>
    public static string DecodeUnicodeName(ReadOnlySpan<byte> name, ReadOnlySpan<byte> encName)
    {
        var dest = new List<char>(name.Length);
        int encPos = 0, decPos = 0;
        int flags = 0, flagBits = 0;
        int encSize = encName.Length;
        int nameSize = name.Length;
        byte highByte = encPos < encSize ? encName[encPos++] : (byte)0;

        void Put(int pos, char c)
        {
            while (dest.Count <= pos)
            {
                dest.Add('\0');
            }

            dest[pos] = c;
        }

        while (encPos < encSize)
        {
            if (flagBits == 0)
            {
                flags = encName[encPos++];
                flagBits = 8;
            }

            switch (flags >> 6)
            {
                case 0:
                    if (encPos >= encSize)
                    {
                        break;
                    }

                    Put(decPos++, (char)encName[encPos++]);
                    break;
                case 1:
                    if (encPos >= encSize)
                    {
                        break;
                    }

                    Put(decPos++, (char)(encName[encPos++] + (highByte << 8)));
                    break;
                case 2:
                    if (encPos + 1 >= encSize)
                    {
                        break;
                    }

                    Put(decPos++, (char)(encName[encPos] + (encName[encPos + 1] << 8)));
                    encPos += 2;
                    break;
                case 3:
                    {
                        if (encPos >= encSize)
                        {
                            break;
                        }

                        int length = encName[encPos++];
                        if ((length & 0x80) != 0)
                        {
                            if (encPos >= encSize)
                            {
                                break;
                            }

                            byte correction = encName[encPos++];
                            for (length = (length & 0x7f) + 2; length > 0 && decPos < nameSize; length--, decPos++)
                            {
                                Put(decPos, (char)(((name[decPos] + correction) & 0xff) + (highByte << 8)));
                            }
                        }
                        else
                        {
                            for (length += 2; length > 0 && decPos < nameSize; length--, decPos++)
                            {
                                Put(decPos, (char)name[decPos]);
                            }
                        }
                    }

                    break;
            }

            flags = (flags << 2) & 0xff;
            flagBits -= 2;
        }

        return new string(dest.ToArray());
    }
}
