using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CoastalCommandCenter.ViewModels;

public partial class LiveFeedCityGroupViewModel : ObservableObject
{
    public required string City { get; init; }
    public ObservableCollection<LiveFeedSelectorItem> Feeds { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
