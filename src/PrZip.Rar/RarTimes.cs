// Port a C# de unrar 7.3.1 (RARLAB), de timefn.cpp. Copyright (c) Alexander L. Roshal.
//
// UnRAR source code may be used in any software to handle RAR archives without limitations free of
// charge, but cannot be used to develop RAR (WinRAR) compatible archiver and to re-create RAR
// compression algorithm, which is proprietary. Distribution of modified UnRAR source code in separate
// form or as a part of other software is permitted, provided that full text of this paragraph, starting
// from "UnRAR source code" words, is included in license, or in documentation if license is not
// available, and in source code comments of resulting package.

namespace PrZip.Rar;

/// <summary>
/// Fechas de RAR. Las de RAR 1.x–4.x son DOS en hora local de quien creó el archivo (sin zona): como unrar,
/// se interpretan en la zona de esta máquina. Las de RAR5 son FILETIME o Unix, en UTC.
/// </summary>
internal static class RarTimes
{
    /// <summary>Fecha DOS. Devuelve null si los campos no forman una fecha válida.</summary>
    public static DateTimeOffset? FromDos(uint dosTime, out DateTime? local)
    {
        local = null;
        int second = (int)(dosTime & 0x1f) * 2;
        int minute = (int)(dosTime >> 5) & 0x3f;
        int hour = (int)(dosTime >> 11) & 0x1f;
        int day = (int)(dosTime >> 16) & 0x1f;
        int month = (int)(dosTime >> 21) & 0x0f;
        int year = (int)(dosTime >> 25) + 1980;
        if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
        return ToOffset(local.Value);
    }

    /// <summary>
    /// Tiempo extendido de RAR 3.x–4.x: una fecha DOS más un segundo opcional y una fracción en unidades de
    /// 100 ns (el "Reminder" de unrar).
    /// </summary>
    public static DateTimeOffset? ExtendLocal(DateTime? local, bool addSecond, uint reminder100ns)
    {
        if (local == null)
        {
            return null;
        }

        var t = local.Value;
        if (addSecond)
        {
            t = t.AddSeconds(1);
        }

        t = t.AddTicks(reminder100ns);
        return ToOffset(t);
    }

    private static DateTimeOffset? ToOffset(DateTime local)
    {
        try
        {
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>FILETIME de Windows (100 ns desde 1601-01-01 UTC).</summary>
    public static DateTimeOffset? FromWin(ulong winTime)
    {
        if (winTime > (ulong)DateTime.MaxValue.ToFileTimeUtc())
        {
            return null;
        }

        return new DateTimeOffset(DateTime.FromFileTimeUtc((long)winTime));
    }

    /// <summary>time_t de 32 bits sin signo, como lo lee unrar.</summary>
    public static DateTimeOffset? FromUnix(uint unixTime) => DateTimeOffset.FromUnixTimeSeconds(unixTime);

    public static DateTimeOffset? AddNs(DateTimeOffset? t, uint ns) => t?.AddTicks(ns / 100);
}
