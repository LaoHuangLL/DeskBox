using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent
{
    private readonly DispatcherTimer _resetCheckTimer = new()
    {
        Interval = TimeSpan.FromSeconds(30)
    };
    private bool _isApplyingDueResets;

    private void InitializeResetSchedule()
    {
        _resetCheckTimer.Tick += ResetCheckTimer_Tick;
    }

    private void StartResetSchedule()
    {
        _resetCheckTimer.Start();
    }

    private void StopResetSchedule()
    {
        _resetCheckTimer.Stop();
    }

    private async void ResetCheckTimer_Tick(object? sender, object e)
    {
        if (_isApplyingDueResets || ViewModel is not { } viewModel || !viewModel.IsInitialized)
        {
            return;
        }

        _isApplyingDueResets = true;
        try
        {
            int resetCount = await viewModel.ApplyDueResetsAsync(DateTimeOffset.UtcNow);
            if (resetCount > 0)
            {
                App.Log($"[Todo] Reset {resetCount} completed item(s) back to active.");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Reset check failed: {ex}");
        }
        finally
        {
            _isApplyingDueResets = false;
        }
    }

    /// <summary>
    /// Bare time picker for a custom reset moment; committing it assigns the
    /// reset schedule to every selected item.
    /// </summary>
    private void ShowCustomResetTimePicker(
        IReadOnlyList<string> itemIds,
        int initialMinutes,
        FrameworkElement anchor)
    {
        int normalizedInitialMinutes =
            TodoRecurrence.NormalizeResetMinutes(initialMinutes) ??
            TodoRecurrenceService.DefaultResetMinutes;
        var timePicker = new TimePickerFlyout
        {
            ClockIdentifier = "24HourClock",
            MinuteIncrement = 5,
            Time = TimeSpan.FromMinutes(normalizedInitialMinutes)
        };
        timePicker.TimePicked += async (_, args) =>
        {
            if (ViewModel is not { } viewModel)
            {
                return;
            }

            int resetMinutes = (int)Math.Round(
                args.NewTime.TotalMinutes,
                MidpointRounding.AwayFromZero);
            await viewModel.SetResetTimeAsync(itemIds, resetMinutes);
        };
        timePicker.ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.Bottom });
    }
}
