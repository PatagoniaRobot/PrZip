// Port a C# de unrar 7.3.1 (RARLAB), de pathfn.cpp (NextVolumeName, GetVolNumPos, VolNameToFirstName y
// ayudantes de nombres). Copyright (c) Alexander L. Roshal.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

using System.Text;

namespace PrZip.Rar;

/// <summary>Nombres de los volúmenes de un RAR: nombre.part1.rar, nombre.part2.rar… o nombre.rar, .r00, .r01…</summary>
internal static class RarVolumes
{
    private static bool IsPathDiv(char c) => c is '\\' or '/';

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static int GetNamePos(string path)
    {
        for (int i = path.Length - 1; i >= 0; i--)
        {
            if (IsPathDiv(path[i]))
            {
                return i + 1;
            }
        }

        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' ? 2 : 0;
    }

    private static int GetExtPos(string name)
    {
        int namePos = GetNamePos(name);
        int dotPos = name.LastIndexOf('.');
        return dotPos < namePos ? -1 : dotPos;
    }

    private static string SetExt(string name, string newExt)
    {
        int dotPos = GetExtPos(name);
        if (dotPos >= 0)
        {
            name = name[..dotPos];
        }

        return name + "." + newExt;
    }

    private static bool CmpExt(string name, string ext)
    {
        int extPos = GetExtPos(name);
        return extPos < 0 ? ext.Length == 0 : string.Equals(name[(extPos + 1)..], ext, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetVolNumPos(string arcName)
    {
        int namePos = GetNamePos(arcName);
        if (namePos == arcName.Length)
        {
            return namePos;
        }

        int pos = arcName.Length - 1;
        while (!IsDigit(arcName[pos]) && pos > namePos)
        {
            pos--;
        }

        int numPos = pos;
        while (IsDigit(arcName[numPos]) && numPos > namePos)
        {
            numPos--;
        }

        // Primer número en nombres como nombre.part##of##.rar: se busca hasta el primer punto.
        while (numPos > namePos && arcName[numPos] != '.')
        {
            if (IsDigit(arcName[numPos]))
            {
                int dotPos = arcName.IndexOf('.', namePos);
                if (dotPos >= 0 && dotPos < numPos)
                {
                    pos = numPos;
                }

                break;
            }

            numPos--;
        }

        return pos;
    }

    /// <summary>Nombre del volumen siguiente (NextVolumeName).</summary>
    public static string NextVolumeName(string arcName, bool oldNumbering)
    {
        var name = new StringBuilder(arcName);
        int dotPos = GetExtPos(arcName);
        if (dotPos < 0)
        {
            name.Append(".rar");
            dotPos = GetExtPos(name.ToString());
        }
        else if (dotPos + 1 == arcName.Length || CmpExt(arcName, "exe") || CmpExt(arcName, "sfx"))
        {
            name = new StringBuilder(SetExt(arcName, "rar"));
        }

        if (!oldNumbering)
        {
            int numPos = GetVolNumPos(name.ToString());
            while (++name[numPos] == '9' + 1)
            {
                name[numPos] = '0';
                if (numPos == 0)
                {
                    break;
                }

                numPos--;
                if (!IsDigit(name[numPos]))
                {
                    name.Insert(numPos + 1, '1');
                    break;
                }
            }
        }
        else
        {
            if (name.Length - dotPos < 3)
            {
                name.Length = dotPos + 1;
                name.Append("rar");
            }

            if (!IsDigit(name[dotPos + 2]) || !IsDigit(name[dotPos + 3]))
            {
                name.Length = dotPos + 2;
                name.Append("00");
            }
            else
            {
                int numPos = name.Length - 1;
                while (++name[numPos] == '9' + 1)
                {
                    if (numPos == 0 || name[numPos - 1] == '.')
                    {
                        name[numPos] = 'a';
                        break;
                    }

                    name[numPos--] = '0';
                }
            }
        }

        return name.ToString();
    }

    /// <summary>
    /// Nombre del primer volumen a partir de cualquiera (VolNameToFirstName). Si no existe, se busca uno con el
    /// mismo nombre y otra extensión que sea primer volumen (un .exe autoextraíble, por ejemplo).
    /// </summary>
    public static string VolNameToFirstName(string volName, bool newNumbering)
    {
        var name = new StringBuilder(volName);
        if (newNumbering)
        {
            char n = '1';
            for (int pos = GetVolNumPos(volName); pos > 0; pos--)
            {
                if (IsDigit(name[pos]))
                {
                    name[pos] = n;
                    n = '0';
                }
                else if (n == '0')
                {
                    break;
                }
            }
        }
        else
        {
            name = new StringBuilder(SetExt(volName, "rar"));
        }

        string result = name.ToString();
        if (!File.Exists(result))
        {
            string dir = Path.GetDirectoryName(result) is { Length: > 0 } d ? d : ".";
            string mask = Path.GetFileName(SetExt(result, "*"));
            try
            {
                foreach (var candidate in Directory.EnumerateFiles(dir, mask).Order(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var arc = RarArchive.Open(candidate, null, exactVolume: true);
                        if (arc.IsFirstVolume)
                        {
                            return candidate;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PrZip.Exceptions.PrZipException)
                    {
                        // No es un RAR o no se puede abrir: se sigue buscando.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Carpeta inaccesible: queda el nombre calculado.
            }
        }

        return result;
    }
}
