namespace DeskBox.Models;

public static class TodoRecurrenceMode
{
    public const string None = "none";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string Weekdays = "weekdays";

    public static readonly string[] SupportedModes =
    [
        None,
        Daily,
        Weekly,
        Monthly,
        Weekdays
    ];

    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return None;
        }

        string normalized = mode.Trim().ToLowerInvariant();
        return SupportedModes.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : None;
    }
}

public sealed class TodoRecurrence
{
    public const int MinutesPerDay = 24 * 60;

    public string Mode { get; set; } = TodoRecurrenceMode.None;

    public DateTimeOffset? AnchorDueDate { get; set; }

    /// <summary>
    /// Local wall-clock time of day, in minutes after midnight, at which a
    /// completed repeating item switches back to the active state. Null keeps
    /// the classic behavior: completing the item generates the next
    /// occurrence instead of resetting the same record.
    /// </summary>
    public int? ResetMinutes { get; set; }

    public static TodoRecurrence? Normalize(TodoRecurrence? recurrence, DateTimeOffset? dueDate)
    {
        if (recurrence is null)
        {
            return null;
        }

        string normalizedMode = TodoRecurrenceMode.Normalize(recurrence.Mode);
        if (string.Equals(normalizedMode, TodoRecurrenceMode.None, StringComparison.Ordinal))
        {
            return null;
        }

        int? resetMinutes = NormalizeResetMinutes(recurrence.ResetMinutes);
        if (dueDate is null && resetMinutes is null)
        {
            // A repeating item needs at least one schedule signal: a due date
            // or a daily reset time.
            return null;
        }

        recurrence.Mode = normalizedMode;
        recurrence.ResetMinutes = resetMinutes;
        recurrence.AnchorDueDate = NormalizeDueDate(recurrence.AnchorDueDate) ?? dueDate;
        return recurrence;
    }

    public TodoRecurrence Clone()
    {
        return new TodoRecurrence
        {
            Mode = TodoRecurrenceMode.Normalize(Mode),
            AnchorDueDate = NormalizeDueDate(AnchorDueDate),
            ResetMinutes = NormalizeResetMinutes(ResetMinutes)
        };
    }

    public static int? NormalizeResetMinutes(int? resetMinutes)
    {
        if (resetMinutes is not { } value)
        {
            return null;
        }

        return Math.Clamp(value, 0, MinutesPerDay - 1);
    }

    private static DateTimeOffset? NormalizeDueDate(DateTimeOffset? dueDate)
    {
        if (dueDate is not { } value)
        {
            return null;
        }

        return new DateTimeOffset(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            value.Second,
            value.Offset);
    }
}
