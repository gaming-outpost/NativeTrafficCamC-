using CommunityToolkit.Mvvm.ComponentModel;
using CoastalCommandCenter.Models;

namespace CoastalCommandCenter.ViewModels;

public partial class LiveFeedSelectorItem : ObservableObject
{
    private bool _suppressSelectionChanged;

    public required LiveFeed Feed { get; init; }
    public required int SortOrder { get; init; }

    public string City => string.IsNullOrWhiteSpace(Feed.City) ? "Unknown" : Feed.City.Trim();
    public string State => string.IsNullOrWhiteSpace(Feed.State) ? "Texas" : Feed.State.Trim();
    public string FeedTypeLabel => Feed.IsYoutube ? "YouTube" : Feed.IsHls ? "HLS" : Feed.Type;
    public int SelectionSequence { get; private set; }

    [ObservableProperty] private bool _isSelected;

    public event EventHandler? SelectionChanged;

    public void SetSelectedSilently(bool isSelected, int? selectionSequence = null)
    {
        _suppressSelectionChanged = true;
        IsSelected = isSelected;
        _suppressSelectionChanged = false;

        if (selectionSequence.HasValue)
        {
            SelectionSequence = selectionSequence.Value;
        }
        else if (!isSelected)
        {
            SelectionSequence = 0;
        }
    }

    public void SetSelectionSequence(int selectionSequence)
    {
        SelectionSequence = selectionSequence;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_suppressSelectionChanged)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
