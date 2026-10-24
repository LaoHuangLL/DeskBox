using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public sealed partial class TodoWidgetViewModel
{
    private bool _isApplyingResets;

    /// <summary>
    /// Flips completed repeat items whose reset moment already passed back to
    /// the active state. Called when the widget loads (catching up while the
    /// app was closed) and by the widget's reset timer while it runs.
    /// </summary>
    public async Task<int> ApplyDueResetsAsync(DateTimeOffset now)
    {
        if (_isApplyingResets || _isDisposed)
        {
            return 0;
        }

        _isApplyingResets = true;
        try
        {
            int resetCount = 0;
            foreach (TodoItemViewModel item in Items.ToList())
            {
                if (!TodoRecurrenceService.TryApplyReset(item.Item, now))
                {
                    continue;
                }

                item.RefreshAfterReset();
                resetCount++;
            }

            if (resetCount == 0)
            {
                return 0;
            }

            RefreshVisibleItems();
            RefreshCountProperties();
            await SaveAsync();
            return resetCount;
        }
        finally
        {
            _isApplyingResets = false;
        }
    }
}
