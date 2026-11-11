namespace FxOs.Api.Rates;

/// <summary>
/// Days Mexican banks are closed, so Banxico publishes no FIX. Deterministic rules (LFT art. 74 and CNBV bank
/// holidays), including the 2006 change to Monday holidays, the bank holidays of the 1990s that were later dropped,
/// and one-off closures. A bank holiday is never an open issue; a missing FIX on any other weekday is a gap to explain.
/// </summary>
public static class MexicanBankCalendar
{
    /// <summary>One-off national closures (no FIX published).</summary>
    private static readonly HashSet<DateOnly> SpecialClosures =
    [
        new(1994, 3, 24),  // national mourning after the Colosio assassination
        new(2006, 3, 21),  // bicentennial of Benito Juárez's birth (in addition to the Monday holiday)
        new(2010, 9, 17),  // national Bicentennial of Independence
    ];

    public static bool IsBusinessDay(DateOnly day) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsHoliday(day);

    public static bool IsHoliday(DateOnly day)
    {
        var (y, m, d) = (day.Year, day.Month, day.Day);
        if ((m, d) is (1, 1) or (5, 1) or (9, 16) or (11, 2) or (12, 12) or (12, 25) || SpecialClosures.Contains(day))
        {
            return true;
        }

        // Bank holidays of the 1990s that were later removed from the bank calendar.
        if (((m, d) is (12, 31) && (y <= 1993 || y == 1999))
            || ((m, d) is (10, 12) && y <= 1993)
            || ((m, d) is (5, 5) && y <= 2000)
            || ((m, d) is (11, 1) && y is 1993 or 1994)
            || ((m, d) is (9, 1) && y is >= 1995 and <= 1999))
        {
            return true;
        }

        if (y >= 2006)
        {
            if (day == NthMonday(y, 2, 1) || day == NthMonday(y, 3, 3) || day == NthMonday(y, 11, 3))
            {
                return true;
            }
        }
        else if ((m, d) is (2, 5) or (3, 21) or (11, 20))
        {
            return true;
        }

        var easter = EasterSunday(y);
        if (day == easter.AddDays(-3) || day == easter.AddDays(-2))
        {
            return true; // Holy Thursday and Good Friday
        }

        // Change of federal government: 1 Dec every six years until 2018, 1 Oct from 2024.
        return (y <= 2018 && (y - 1994) % 6 == 0 && (m, d) is (12, 1))
            || (y >= 2024 && (y - 2024) % 6 == 0 && (m, d) is (10, 1));
    }

    /// <summary>Business days (weekdays that are not bank holidays) from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static IEnumerable<DateOnly> BusinessDays(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (IsBusinessDay(d))
            {
                yield return d;
            }
        }
    }

    private static DateOnly NthMonday(int year, int month, int n)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)DayOfWeek.Monday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + (7 * (n - 1)));
    }

    /// <summary>Gregorian Easter Sunday (anonymous Gregorian algorithm).</summary>
    public static DateOnly EasterSunday(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = ((19 * a) + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        int m = (a + (11 * h) + (22 * l)) / 451, month = (h + l - (7 * m) + 114) / 31, day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(y, month, day);
    }
}
