using DeskBox.Models;

namespace DeskBox.Services;

public static class TodoRecurrenceService
{
    private const int MaxResetSearchDays = 400;

    public const int DefaultResetMinutes = 4 * 60;

    /// <summary>
    /// Reset times offered in the pickers. Game dailies cluster around these
    /// local wall-clock moments; anything else is reachable through the custom
    /// time picker.
    /// </summary>
    public static IReadOnlyList<int> SupportedResetTimes { get; } =
        [0, 4 * 60, 5 * 60, 6 * 60, 12 * 60, 20 * 60];


    public static bool IsRecurring(TodoItem item)
    {
        return item.DueDate is not null &&
               item.Recurrence is not null &&
               !string.Equals(
                   TodoRecurrenceMode.Normalize(item.Recurrence.Mode),
                   TodoRecurrenceMode.None,
                   StringComparison.Ordinal);
    }

    public static TodoRecurrence? CreateRecurrence(string? mode, DateTimeOffset? dueDate, int? resetMinutes = null)
    {
        string normalizedMode = TodoRecurrenceMode.Normalize(mode);
        if (string.Equals(normalizedMode, TodoRecurrenceMode.None, StringComparison.Ordinal))
        {
            return null;
        }

        int? normalizedResetMinutes = TodoRecurrence.NormalizeResetMinutes(resetMinutes);
        if (dueDate is null && normalizedResetMinutes is null)
        {
            return null;
        }

        return new TodoRecurrence
        {
            Mode = normalizedMode,
            AnchorDueDate = dueDate,
            ResetMinutes = normalizedResetMinutes
        };
    }

    /// <summary>
    /// True when the item repeats without spawning new occurrences: completion
    /// only marks the record done, and the reset schedule flips it back.
    /// </summary>
    public static bool HasResetTime(TodoItem item)
    {
        return item.Recurrence is not null &&
               item.Recurrence.ResetMinutes is not null &&
               !string.Equals(
                   TodoRecurrenceMode.Normalize(item.Recurrence.Mode),
                   TodoRecurrenceMode.None,
                   StringComparison.Ordinal);
    }

    public static string FormatResetTime(int resetMinutes)
    {
        int normalized = TodoRecurrence.NormalizeResetMinutes(resetMinutes) ?? 0;
        return $"{normalized / 60:00}:{normalized % 60:00}";
    }

    /// <summary>
    /// First reset moment strictly after <paramref name="after"/> for the
    /// item's schedule. Every supported mode fires at the same local
    /// wall-clock time; weekly/monthly/weekday modes additionally restrict the
    /// days that fire.
    /// </summary>
    public static bool TryGetNextResetAt(TodoItem item, DateTimeOffset after, out DateTimeOffset resetAt)
    {
        resetAt = default;
        if (!HasResetTime(item) || item.Recurrence is not { } recurrence)
        {
            return false;
        }

        int resetMinutes = recurrence.ResetMinutes!.Value;
        string mode = TodoRecurrenceMode.Normalize(recurrence.Mode);
        DateTime anchorDate = (recurrence.AnchorDueDate ?? item.DueDate ?? item.CreatedAt).ToLocalTime().Date;
        DateTime firstDay = after.ToLocalTime().Date;
        for (int dayOffset = 0; dayOffset <= MaxResetSearchDays; dayOffset++)
        {
            DateTime day = firstDay.AddDays(dayOffset);
            if (!IsResetDay(mode, day, anchorDate))
            {
                continue;
            }

            DateTime localInstant = day.AddMinutes(resetMinutes);
            var candidate = new DateTimeOffset(localInstant);
            if (candidate <= after)
            {
                continue;
            }

            resetAt = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Applies the reset schedule to a completed item: when the next reset
    /// moment after its completion already passed, the item returns to the
    /// active state so the same record tracks the next day instead of spawning
    /// a new item. Returns true when the item changed.
    /// </summary>
    public static bool TryApplyReset(TodoItem item, DateTimeOffset now)
    {
        if (!item.IsCompleted || !HasResetTime(item))
        {
            return false;
        }

        DateTimeOffset completedAt = item.CompletedAt ?? item.UpdatedAt;
        if (completedAt == default)
        {
            completedAt = now;
        }

        if (!TryGetNextResetAt(item, completedAt, out DateTimeOffset resetAt) || resetAt > now)
        {
            return false;
        }

        ApplyReset(item, resetAt, now);
        return true;
    }

    private static void ApplyReset(TodoItem item, DateTimeOffset resetAt, DateTimeOffset now)
    {
        if (IsRecurring(item) &&
            item.DueDate is { } dueDate &&
            dueDate < resetAt &&
            TryGetNextDueDate(item, resetAt, out DateTimeOffset nextDueDate))
        {
            item.DueDate = nextDueDate;
            if (item.Recurrence is not null)
            {
                item.Recurrence.AnchorDueDate = nextDueDate;
            }
        }

        item.IsCompleted = false;
        item.CompletedAt = null;
        item.GeneratedNextItemId = null;
        item.ReminderLastNotifiedAt = null;
        item.ReminderDismissedForDueDate = null;
        item.SnoozedUntil = null;
        item.SnoozeLastNotifiedAt = null;
        foreach (TodoStep step in item.Steps)
        {
            step.IsCompleted = false;
        }

        item.UpdatedAt = now;
    }

    private static bool IsResetDay(string mode, DateTime day, DateTime anchorDate)
    {
        return mode switch
        {
            TodoRecurrenceMode.Weekdays => !IsWeekend(day.DayOfWeek),
            TodoRecurrenceMode.Weekly => day.DayOfWeek == anchorDate.DayOfWeek,
            TodoRecurrenceMode.Monthly => day.Day == Math.Min(anchorDate.Day, DateTime.DaysInMonth(day.Year, day.Month)),
            _ => true
        };
    }

    public static string GetLocalizationKey(string? mode)
    {
        return TodoRecurrenceMode.Normalize(mode) switch
        {
            TodoRecurrenceMode.Daily => "Todo.Recurrence.Daily",
            TodoRecurrenceMode.Weekly => "Todo.Recurrence.Weekly",
            TodoRecurrenceMode.Monthly => "Todo.Recurrence.Monthly",
            TodoRecurrenceMode.Weekdays => "Todo.Recurrence.Weekdays",
            _ => "Todo.Recurrence.None"
        };
    }

    public static bool TryCreateNextOccurrence(TodoItem sourceItem, DateTimeOffset completedAt, out TodoItem? nextItem)
    {
        nextItem = null;
        if (HasResetTime(sourceItem))
        {
            // Items with a reset schedule reuse the same record: completing
            // them only marks the current period done, and the reset schedule
            // flips the item back to the active state.
            return false;
        }

        if (!TryGetNextDueDate(sourceItem, completedAt, out DateTimeOffset nextDueDate))
        {
            return false;
        }

        string recurrenceSeriesId = NormalizeSeriesId(sourceItem.RecurrenceSeriesId) ?? Guid.NewGuid().ToString("N");

        nextItem = new TodoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = sourceItem.Text,
            IsCompleted = false,
            IsImportant = sourceItem.IsImportant,
            ColorMarker = sourceItem.ColorMarker,
            DueDate = nextDueDate,
            Recurrence = sourceItem.Recurrence?.Clone(),
            Steps = sourceItem.Steps
                .OrderBy(step => step.SortOrder)
                .Select(step => new TodoStep
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Text = step.Text,
                    IsCompleted = false,
                    SortOrder = step.SortOrder
                })
                .ToList(),
            Notes = sourceItem.Notes,
            Attachments = sourceItem.Attachments
                .Select(attachment => new TodoAttachment
                {
                    Id = Guid.NewGuid().ToString("N"),
                    FilePath = attachment.FilePath,
                    DisplayName = attachment.DisplayName,
                    Type = attachment.Type,
                    AddedAt = completedAt
                })
                .ToList(),
            CompletedAt = null,
            ReminderLastNotifiedAt = null,
            ReminderDismissedForDueDate = null,
            ReminderOffsetMinutes = TodoReminderOptions.NormalizeOffsetMinutes(sourceItem.ReminderOffsetMinutes),
            SnoozedUntil = null,
            SnoozeLastNotifiedAt = null,
            RecurrenceSeriesId = recurrenceSeriesId,
            GeneratedNextItemId = null,
            SortOrder = sourceItem.SortOrder + 1,
            CreatedAt = completedAt,
            UpdatedAt = completedAt
        };

        return true;
    }

    public static bool TryGetNextDueDate(TodoItem item, DateTimeOffset completedAt, out DateTimeOffset nextDueDate)
    {
        nextDueDate = default;
        if (!IsRecurring(item) || item.DueDate is not { } dueDate)
        {
            return false;
        }

        string mode = TodoRecurrenceMode.Normalize(item.Recurrence!.Mode);
        DateTimeOffset anchorDueDate = item.Recurrence.AnchorDueDate?.ToLocalTime() ?? dueDate.ToLocalTime();
        DateTimeOffset localDueDate = dueDate.ToLocalTime();
        DateTimeOffset localCompletedAt = completedAt.ToLocalTime();

        nextDueDate = mode switch
        {
            TodoRecurrenceMode.Daily => AdvanceByFixedDays(localDueDate, localCompletedAt, 1),
            TodoRecurrenceMode.Weekly => AdvanceByFixedDays(localDueDate, localCompletedAt, 7),
            TodoRecurrenceMode.Monthly => AdvanceMonthly(localDueDate, anchorDueDate, localCompletedAt),
            TodoRecurrenceMode.Weekdays => AdvanceWeekdays(localDueDate, localCompletedAt),
            _ => default
        };

        return nextDueDate != default;
    }

    public static bool ShouldRemoveGeneratedOccurrence(TodoItem sourceItem, TodoItem generatedItem)
    {
        if (string.IsNullOrWhiteSpace(sourceItem.GeneratedNextItemId) ||
            !string.Equals(sourceItem.GeneratedNextItemId, generatedItem.Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (generatedItem.IsCompleted ||
            generatedItem.CreatedAt != generatedItem.UpdatedAt)
        {
            return false;
        }

        return string.Equals(sourceItem.Text, generatedItem.Text, StringComparison.Ordinal) &&
               sourceItem.IsImportant == generatedItem.IsImportant &&
               string.Equals(sourceItem.ColorMarker, generatedItem.ColorMarker, StringComparison.Ordinal) &&
               Nullable.Equals(
                   TodoReminderOptions.NormalizeOffsetMinutes(sourceItem.ReminderOffsetMinutes),
                   TodoReminderOptions.NormalizeOffsetMinutes(generatedItem.ReminderOffsetMinutes)) &&
               string.Equals(
                   NormalizeSeriesId(sourceItem.RecurrenceSeriesId),
                   NormalizeSeriesId(generatedItem.RecurrenceSeriesId),
                   StringComparison.Ordinal) &&
               AreEquivalent(sourceItem.Recurrence, generatedItem.Recurrence);
    }

    public static string? NormalizeSeriesId(string? seriesId)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return null;
        }

        return seriesId.Trim();
    }

    private static bool AreEquivalent(TodoRecurrence? left, TodoRecurrence? right)
    {
        string leftMode = TodoRecurrenceMode.Normalize(left?.Mode);
        string rightMode = TodoRecurrenceMode.Normalize(right?.Mode);
        if (!string.Equals(leftMode, rightMode, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(leftMode, TodoRecurrenceMode.None, StringComparison.Ordinal))
        {
            return true;
        }

        return Nullable.Equals(left?.AnchorDueDate, right?.AnchorDueDate) &&
               Nullable.Equals(
                   TodoRecurrence.NormalizeResetMinutes(left?.ResetMinutes),
                   TodoRecurrence.NormalizeResetMinutes(right?.ResetMinutes));
    }

    private static DateTimeOffset AdvanceByFixedDays(
        DateTimeOffset currentDueDate,
        DateTimeOffset completedAt,
        int days)
    {
        DateTimeOffset candidate = AddLocalDays(currentDueDate, days);
        while (candidate <= completedAt)
        {
            candidate = AddLocalDays(candidate, days);
        }

        return candidate;
    }

    private static DateTimeOffset AdvanceWeekdays(
        DateTimeOffset currentDueDate,
        DateTimeOffset completedAt)
    {
        DateTimeOffset candidate = currentDueDate;
        do
        {
            candidate = AddLocalDays(candidate, 1);
        }
        while (candidate <= completedAt || IsWeekend(candidate.DayOfWeek));

        return candidate;
    }

    private static DateTimeOffset AdvanceMonthly(
        DateTimeOffset currentDueDate,
        DateTimeOffset anchorDueDate,
        DateTimeOffset completedAt)
    {
        int monthOffset = MonthsBetween(anchorDueDate, currentDueDate) + 1;
        DateTimeOffset candidate = CreateMonthlyCandidate(anchorDueDate, monthOffset);
        while (candidate <= completedAt)
        {
            monthOffset++;
            candidate = CreateMonthlyCandidate(anchorDueDate, monthOffset);
        }

        return candidate;
    }

    private static int MonthsBetween(DateTimeOffset anchorDueDate, DateTimeOffset targetDueDate)
    {
        return ((targetDueDate.Year - anchorDueDate.Year) * 12) +
               (targetDueDate.Month - anchorDueDate.Month);
    }

    private static DateTimeOffset CreateMonthlyCandidate(DateTimeOffset anchorDueDate, int monthOffset)
    {
        DateTime anchorLocal = anchorDueDate.LocalDateTime;
        DateTime targetMonth = new(anchorLocal.Year, anchorLocal.Month, 1, anchorLocal.Hour, anchorLocal.Minute, anchorLocal.Second, anchorLocal.Kind);
        targetMonth = targetMonth.AddMonths(monthOffset);
        int targetDay = Math.Min(anchorLocal.Day, DateTime.DaysInMonth(targetMonth.Year, targetMonth.Month));
        DateTime localCandidate = new(
            targetMonth.Year,
            targetMonth.Month,
            targetDay,
            anchorLocal.Hour,
            anchorLocal.Minute,
            anchorLocal.Second,
            DateTimeKind.Local);
        return new DateTimeOffset(localCandidate);
    }

    private static bool IsWeekend(DayOfWeek dayOfWeek)
    {
        return dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }

    private static DateTimeOffset AddLocalDays(DateTimeOffset value, int days)
    {
        DateTime localDateTime = value.LocalDateTime.AddDays(days);
        return new DateTimeOffset(localDateTime);
    }
}
