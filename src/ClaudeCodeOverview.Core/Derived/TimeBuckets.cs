namespace ClaudeCodeOverview.Core.Derived;

/// <summary>
/// Day bucketing is fixed to Europe/Amsterdam (v1 decision): computed once at ingest and
/// stored as day_local, so queries stay index-friendly and DST is handled in one place.
/// </summary>
public static class TimeBuckets
{
    public static readonly TimeZoneInfo Amsterdam = Resolve();

    private static TimeZoneInfo Resolve()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"); }
        catch (TimeZoneNotFoundException)
        {
            // Windows dev machines without ICU-based IANA support.
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
    }

    public static string DayLocal(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, Amsterdam).ToString("yyyy-MM-dd");
}
