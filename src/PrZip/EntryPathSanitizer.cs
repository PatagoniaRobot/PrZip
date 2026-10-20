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

using System.Security.Cryptography;
using System.Text;
using PrZip.Exceptions;

namespace PrZip;

/// <summary>
/// Convierte el nombre de una entrada en una ruta relativa segura para extraer.
///
/// - Un componente ".." es un intento de evasión de directorio (Zip-Slip) y aborta la extracción.
/// - Raíces, unidades ("C:") y separadores repetidos se descartan: la ruta queda relativa al destino,
///   como hace 7-Zip.
/// - En Windows, además: los caracteres que el sistema no admite o reinterpreta (':' abre un flujo
///   alternativo NTFS, '&lt;', '&gt;', '"', '|', '?', '*' y los de control) pasan a '_'; los nombres de
///   dispositivo (CON, NUL, COM1...) reciben el prefijo '_'; y un punto o espacio final se reemplaza,
///   porque Windows lo descarta en silencio y dos entradas distintas terminarían en el mismo archivo.
/// - Un nombre más largo de lo que admite el sistema de archivos (255 caracteres en NTFS, 255 bytes en ext4)
///   se acorta conservando la extensión y con una huella del original (ver FitLength).
///
/// Tanto '/' como '\' se tratan como separadores en cualquier sistema.
/// </summary>
public static class EntryPathSanitizer
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// Devuelve la ruta relativa segura (con el separador del sistema) y si difiere del nombre original.
    /// </summary>
    public static string ToSafeRelativePath(string entryName, out bool changed) =>
        ToSafeRelativePath(entryName, OperatingSystem.IsWindows(), out changed);

    public static string ToSafeRelativePath(string entryName, bool windowsRules, out bool changed) =>
        ToSafeRelativePath(entryName, windowsRules, neutralizeParentReferences: false, out changed);

    /// <summary>
    /// Con <paramref name="neutralizeParentReferences"/>, un componente ".." se reemplaza por "__" en lugar
    /// de abortar: para evidencia de terceros (un TAR con "../" es común y no siempre malicioso) conviene
    /// extraer y documentar el cambio. Sin la opción, ".." se trata como ataque y se lanza excepción.
    /// </summary>
    public static string ToSafeRelativePath(string entryName, bool windowsRules, bool neutralizeParentReferences, out bool changed)
    {
        ArgumentNullException.ThrowIfNull(entryName);

        var parts = new List<string>();
        foreach (string raw in entryName.Split('/', '\\'))
        {
            if (raw.Length == 0 || raw == ".")
            {
                continue;
            }

            if (raw == "..")
            {
                if (!neutralizeParentReferences)
                {
                    throw new CorruptArchiveException($"Intento de evasión de directorio detectado (Zip-Slip): {entryName}");
                }

                parts.Add("__");
                continue;
            }

            parts.Add(FitLength(windowsRules ? SanitizeWindowsComponent(raw) : SanitizeUnixComponent(raw), windowsRules));
        }

        if (parts.Count == 0)
        {
            parts.Add("_");
        }

        string result = string.Join(Path.DirectorySeparatorChar, parts);
        // Solo la barra final (de un directorio) no cuenta como cambio; una raíz inicial sí.
        string normalizedOriginal = entryName.Replace('\\', '/').TrimEnd('/');
        changed = !string.Equals(string.Join('/', parts), normalizedOriginal, StringComparison.Ordinal);
        return result;
    }

    /// <summary>Largo máximo de un nombre: 255 unidades UTF-16 en NTFS, 255 bytes UTF-8 en ext4 y la mayoría de Unix.</summary>
    private const int MaxComponentLength = 255;

    /// <summary>Largo al que se acorta un nombre que no entra: deja lugar para un "~2" si después choca con otro.</summary>
    private const int ShortenedComponentLength = 240;

    private static int ComponentLength(string component, bool windowsRules) =>
        windowsRules ? component.Length : Encoding.UTF8.GetByteCount(component);

    /// <summary>
    /// Un nombre más largo de lo que admite el sistema de archivos no se puede crear, y antes abortaba la
    /// extracción entera (los reportes de Autopsy con tags llegan a cientos de caracteres). Se acorta conservando
    /// la extensión y agregando una huella del nombre original ("~" y 8 hexadecimales de su SHA-256), para que dos
    /// nombres largos que empiezan igual no terminen en el mismo archivo. El cambio se informa como renombrado.
    /// </summary>
    private static string FitLength(string component, bool windowsRules)
    {
        if (ComponentLength(component, windowsRules) <= MaxComponentLength)
        {
            return component;
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(component)))[..8];
        string ext = Path.GetExtension(component);
        if (ext.Length > 16 || ext.Length == component.Length)
        {
            ext = string.Empty;
        }

        string stem = component[..^ext.Length];
        string suffix = "~" + hash + ext;
        int keep = stem.Length;
        while (keep > 0 && ComponentLength(stem[..keep] + suffix, windowsRules) > ShortenedComponentLength)
        {
            keep--;
        }

        // No partir un par sustituto (un emoji, por ejemplo) a la mitad.
        if (keep > 0 && char.IsHighSurrogate(stem[keep - 1]))
        {
            keep--;
        }

        return stem[..keep] + suffix;
    }

    private static string SanitizeUnixComponent(string component)
    {
        // En Unix solo el byte nulo es inválido en un nombre.
        return component.Replace('\0', '_');
    }

    private static string SanitizeWindowsComponent(string component)
    {
        var sb = new StringBuilder(component.Length + 1);
        foreach (char c in component)
        {
            sb.Append(c < 0x20 || c is ':' or '<' or '>' or '"' or '|' or '?' or '*' ? '_' : c);
        }

        // Windows elimina puntos y espacios finales: "a." y "a" serían el mismo archivo.
        int last = sb.Length - 1;
        if (sb[last] is '.' or ' ')
        {
            sb[last] = '_';
        }

        // Nombres de dispositivo, con o sin extensión ("NUL", "con.txt", "COM1 .log").
        string s = sb.ToString();
        int dot = s.IndexOf('.');
        string stem = (dot >= 0 ? s[..dot] : s).TrimEnd(' ');
        if (ReservedWindowsNames.Contains(stem))
        {
            s = "_" + s;
        }

        return s;
    }

    /// <summary>
    /// Ruta absoluta de destino para una entrada dentro de 'root': saneada, verificada contra la
    /// evasión de directorio y, para archivos, sin chocar con otra entrada ya extraída.
    /// Con 'flatten' (comando "e") se usa solo el último componente. 'renamed' indica si el destino
    /// no refleja el nombre original y conviene informarlo.
    /// </summary>
    public static string ResolveTarget(string root, string entryName, bool isDirectory, bool flatten,
        ISet<string> usedPaths, out bool renamed) =>
        ResolveTarget(root, entryName, isDirectory, flatten, usedPaths, neutralizeParentReferences: false, out renamed);

    /// <summary>Igual, con la opción de neutralizar ".." (ver <see cref="ToSafeRelativePath(string, bool, bool, out bool)"/>).</summary>
    public static string ResolveTarget(string root, string entryName, bool isDirectory, bool flatten,
        ISet<string> usedPaths, bool neutralizeParentReferences, out bool renamed)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(usedPaths);

        string fullRoot = Path.GetFullPath(root);
        string rootWithSep = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;

        string relative = ToSafeRelativePath(entryName, OperatingSystem.IsWindows(), neutralizeParentReferences, out renamed);
        if (flatten)
        {
            // Aplanar no es "renombrar": solo cuenta si cambió el último componente.
            relative = Path.GetFileName(relative);
            string originalLast = entryName.Replace('\\', '/').TrimEnd('/');
            originalLast = originalLast[(originalLast.LastIndexOf('/') + 1)..];
            renamed = !string.Equals(relative, originalLast, StringComparison.Ordinal);
        }

        string target = Path.GetFullPath(Path.Combine(fullRoot, relative));

        // Defensa en profundidad: aunque el saneado ya descartó "..", se verifica el resultado final.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(rootWithSep, comparison))
        {
            throw new CorruptArchiveException($"Intento de evasión de directorio detectado (Zip-Slip): {entryName}");
        }

        if (isDirectory)
        {
            usedPaths.Add(target);
            return target;
        }

        string unique = MakeUnique(target, usedPaths);
        renamed |= !string.Equals(unique, target, StringComparison.Ordinal);
        return unique;
    }

    /// <summary>
    /// Si la ruta ya fue usada en esta extracción (dos nombres que se sanean igual, o que solo difieren
    /// en mayúsculas en Windows), agrega un sufijo "~2", "~3"... antes de la extensión.
    /// </summary>
    public static string MakeUnique(string fullPath, ISet<string> usedPaths)
    {
        ArgumentNullException.ThrowIfNull(usedPaths);
        if (usedPaths.Add(fullPath))
        {
            return fullPath;
        }

        string dir = Path.GetDirectoryName(fullPath) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(fullPath);
        string ext = Path.GetExtension(fullPath);
        bool windows = OperatingSystem.IsWindows();
        for (int i = 2; ; i++)
        {
            // Un nombre ya cerca del máximo no puede crecer con el sufijo: se acorta lo necesario.
            string stem = name;
            while (stem.Length > 1 && ComponentLength($"{stem}~{i}{ext}", windows) > MaxComponentLength)
            {
                stem = char.IsLowSurrogate(stem[^1]) && stem.Length > 2 ? stem[..^2] : stem[..^1];
            }

            string candidate = Path.Combine(dir, $"{stem}~{i}{ext}");
            if (usedPaths.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Conjunto de rutas para <see cref="MakeUnique"/> con la sensibilidad a mayúsculas del sistema.
    /// </summary>
    public static HashSet<string> CreatePathSet() =>
        new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
