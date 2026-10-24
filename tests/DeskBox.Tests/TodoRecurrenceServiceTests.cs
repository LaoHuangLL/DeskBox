using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class TodoRecurrenceServiceTests
{
    [Fact]
    public void TryGetNextDueDate_DailyRecurrenceAdvancesPastCompletionTime()
    {
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 5, 9, 0, 0, DateTimeKind.Local));
        var completedAt = new DateTimeOffset(new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Local));
        var item = CreateRecurringItem(TodoRecurrenceMode.Daily, dueDate);

        bool advanced = TodoRecurrenceService.TryGetNextDueDate(item, completedAt, out DateTimeOffset nextDueDate);

        Assert.True(advanced);
        Assert.Equal(new DateTime(2026, 7, 8, 9, 0, 0), nextDueDate.LocalDateTime);
    }

    [Fact]
    public void TryGetNextDueDate_WeekdaysRecurrenceSkipsWeekend()
    {
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Local));
        var completedAt = new DateTimeOffset(new DateTime(2026, 7, 10, 18, 30, 0, DateTimeKind.Local));
        var item = CreateRecurringItem(TodoRecurrenceMode.Weekdays, dueDate);

        bool advanced = TodoRecurrenceService.TryGetNextDueDate(item, completedAt, out DateTimeOffset nextDueDate);

        Assert.True(advanced);
        Assert.Equal(DayOfWeek.Monday, nextDueDate.DayOfWeek);
        Assert.Equal(new DateTime(2026, 7, 13, 18, 0, 0), nextDueDate.LocalDateTime);
    }

    [Fact]
    public void TryGetNextDueDate_MonthlyRecurrencePreservesAnchorDayAcrossShortMonth()
    {
        var anchorDueDate = new DateTimeOffset(new DateTime(2026, 1, 31, 18, 0, 0, DateTimeKind.Local));
        var currentDueDate = new DateTimeOffset(new DateTime(2026, 2, 28, 18, 0, 0, DateTimeKind.Local));
        var completedAt = new DateTimeOffset(new DateTime(2026, 2, 28, 19, 0, 0, DateTimeKind.Local));
        var item = CreateRecurringItem(TodoRecurrenceMode.Monthly, currentDueDate, anchorDueDate);

        bool advanced = TodoRecurrenceService.TryGetNextDueDate(item, completedAt, out DateTimeOffset nextDueDate);

        Assert.True(advanced);
        Assert.Equal(new DateTime(2026, 3, 31, 18, 0, 0), nextDueDate.LocalDateTime);
    }

    [Fact]
    public void TryCreateNextOccurrence_PreservesRecurrenceAndClearsReminderState()
    {
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Local));
        var completedAt = new DateTimeOffset(new DateTime(2026, 7, 10, 9, 30, 0, DateTimeKind.Local));
        var item = CreateRecurringItem(TodoRecurrenceMode.Weekly, dueDate);
        item.IsImportant = true;
        item.ColorMarker = TodoItem.BlueColorMarker;
        item.ReminderLastNotifiedAt = completedAt.AddMinutes(-10);
        item.ReminderDismissedForDueDate = dueDate;
        item.ReminderOffsetMinutes = 30;
        item.SnoozedUntil = completedAt.AddMinutes(10);
        item.SnoozeLastNotifiedAt = completedAt.AddMinutes(-5);
        item.Notes = "repeat this context";
        item.Steps =
        [
            new TodoStep { Text = "first", IsCompleted = true, SortOrder = 0 },
            new TodoStep { Text = "second", IsCompleted = false, SortOrder = 1 }
        ];

        bool created = TodoRecurrenceService.TryCreateNextOccurrence(item, completedAt, out TodoItem? nextItem);

        Assert.True(created);
        Assert.NotNull(nextItem);
        Assert.False(nextItem.IsCompleted);
        Assert.Equal(item.Text, nextItem.Text);
        Assert.Equal(TodoRecurrenceMode.Weekly, nextItem.Recurrence?.Mode);
        Assert.Equal(item.Recurrence?.AnchorDueDate, nextItem.Recurrence?.AnchorDueDate);
        Assert.Null(nextItem.ReminderLastNotifiedAt);
        Assert.Null(nextItem.ReminderDismissedForDueDate);
        Assert.Equal(30, nextItem.ReminderOffsetMinutes);
        Assert.Null(nextItem.SnoozedUntil);
        Assert.Null(nextItem.SnoozeLastNotifiedAt);
        Assert.Equal(item.Notes, nextItem.Notes);
        Assert.Collection(
            nextItem.Steps,
            step =>
            {
                Assert.Equal("first", step.Text);
                Assert.False(step.IsCompleted);
            },
            step =>
            {
                Assert.Equal("second", step.Text);
                Assert.False(step.IsCompleted);
            });
    }

    [Fact]
    public void TryGetNextResetAt_DailyResetUsesLocalWallClockTime()
    {
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 5, 9, 0, 0, DateTimeKind.Local));
        TodoItem item = CreateRecurringItem(TodoRecurrenceMode.Daily, dueDate);
        item.Recurrence!.ResetMinutes = 4 * 60;
        var after = new DateTimeOffset(new DateTime(2026, 7, 5, 5, 0, 0, DateTimeKind.Local));

        bool found = TodoRecurrenceService.TryGetNextResetAt(item, after, out DateTimeOffset resetAt);

        Assert.True(found);
        Assert.Equal(new DateTime(2026, 7, 6, 4, 0, 0), resetAt.LocalDateTime);
    }

    [Fact]
    public void TryGetNextResetAt_WeeklyResetFiresOnlyOnAnchorWeekday()
    {
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 6, 9, 0, 0, DateTimeKind.Local));
        TodoItem item = CreateRecurringItem(TodoRecurrenceMode.Weekly, dueDate);
        item.Recurrence!.ResetMinutes = 0;
        var after = new DateTimeOffset(new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Local));

        bool found = TodoRecurrenceService.TryGetNextResetAt(item, after, out DateTimeOffset resetAt);

        Assert.True(found);
        Assert.Equal(DayOfWeek.Monday, resetAt.DayOfWeek);
        Assert.Equal(new DateTime(2026, 7, 13, 0, 0, 0), resetAt.LocalDateTime);
    }

    [Fact]
    public void TryGetNextResetAt_MonthlyResetClampsAnchorDay()
    {
        var anchorDueDate = new DateTimeOffset(new DateTime(2026, 1, 31, 9, 0, 0, DateTimeKind.Local));
        TodoItem item = CreateRecurringItem(TodoRecurrenceMode.Monthly, anchorDueDate, anchorDueDate);
        item.Recurrence!.ResetMinutes = 4 * 60;
        var after = new DateTimeOffset(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Local));

        bool found = TodoRecurrenceService.TryGetNextResetAt(item, after, out DateTimeOffset resetAt);

        Assert.True(found);
        Assert.Equal(new DateTime(2026, 2, 28, 4, 0, 0), resetAt.LocalDateTime);
    }

    [Fact]
    public void TryApplyReset_ReturnsCompletedItemToActiveAndAdvancesDueDate()
    {
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 5, 9, 0, 0, DateTimeKind.Local));
        TodoItem item = CreateRecurringItem(TodoRecurrenceMode.Daily, dueDate);
        item.Recurrence!.ResetMinutes = 4 * 60;
        item.IsCompleted = true;
        item.CompletedAt = new DateTimeOffset(new DateTime(2026, 7, 5, 9, 30, 0, DateTimeKind.Local));
        item.Steps =
        [
            new TodoStep { Text = "first", IsCompleted = true, SortOrder = 0 },
            new TodoStep { Text = "second", IsCompleted = true, SortOrder = 1 }
        ];
        var now = new DateTimeOffset(new DateTime(2026, 7, 6, 4, 5, 0, DateTimeKind.Local));

        bool reset = TodoRecurrenceService.TryApplyReset(item, now);

        Assert.True(reset);
        Assert.False(item.IsCompleted);
        Assert.Null(item.CompletedAt);
        Assert.Equal(new DateTime(2026, 7, 6, 9, 0, 0), item.DueDate!.Value.LocalDateTime);
        Assert.All(item.Steps, step => Assert.False(step.IsCompleted));
    }

    [Fact]
    public void TryApplyReset_KeepsItemCompletedBeforeResetMoment()
    {
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 5, 9, 0, 0, DateTimeKind.Local));
        TodoItem item = CreateRecurringItem(TodoRecurrenceMode.Daily, dueDate);
        item.Recurrence!.ResetMinutes = 4 * 60;
        item.IsCompleted = true;
        item.CompletedAt = new DateTimeOffset(new DateTime(2026, 7, 5, 9, 30, 0, DateTimeKind.Local));
        var justBeforeReset = new DateTimeOffset(new DateTime(2026, 7, 6, 3, 59, 0, DateTimeKind.Local));

        bool reset = TodoRecurrenceService.TryApplyReset(item, justBeforeReset);

        Assert.False(reset);
        Assert.True(item.IsCompleted);
    }

    [Fact]
    public void TryCreateNextOccurrence_SkipsItemsWithResetTime()
    {
        var dueDate = new DateTimeOffset(new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Local));
        TodoItem item = CreateRecurringItem(TodoRecurrenceMode.Daily, dueDate);
        item.Recurrence!.ResetMinutes = 4 * 60;
        var completedAt = new DateTimeOffset(new DateTime(2026, 7, 10, 9, 30, 0, DateTimeKind.Local));

        bool created = TodoRecurrenceService.TryCreateNextOccurrence(item, completedAt, out TodoItem? nextItem);

        Assert.False(created);
        Assert.Null(nextItem);
    }

    private static TodoItem CreateRecurringItem(string recurrenceMode, DateTimeOffset dueDate, DateTimeOffset? anchorDueDate = null)
    {
        return new TodoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = "task",
            DueDate = dueDate,
            Recurrence = new TodoRecurrence
            {
                Mode = recurrenceMode,
                AnchorDueDate = anchorDueDate ?? dueDate
            }
        };
    }
}
