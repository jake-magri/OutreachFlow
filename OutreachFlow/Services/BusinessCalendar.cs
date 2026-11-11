namespace OutreachFlow.Services;

/// <summary>Weekday scheduling preview. Does not account for public holidays; no messages are sent.</summary>
public static class BusinessCalendar
{
    public static DateTime AddBusinessDays(DateTime fromUtc, int businessDays, string timeZoneId, int localHour)
    {
        if (fromUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Provide a UTC timestamp.", nameof(fromUtc));
        if (businessDays < 1 || businessDays > 60)
            throw new ArgumentOutOfRangeException(nameof(businessDays));
        if (localHour is < 8 or > 18)
            throw new ArgumentOutOfRangeException(nameof(localHour));

        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, zone);
        var day = DateOnly.FromDateTime(local);
        while (businessDays > 0)
        {
            day = day.AddDays(1);
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) businessDays--;
        }
        var dueLocal = day.ToDateTime(new TimeOnly(localHour, 0), DateTimeKind.Unspecified);
        // 8 AM–6 PM on a business day avoids DST spring-forward's missing hour.
        return TimeZoneInfo.ConvertTimeToUtc(dueLocal, zone);
    }
}
