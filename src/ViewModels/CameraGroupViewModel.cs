using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoastalCommandCenter.Models;

namespace CoastalCommandCenter.ViewModels;

/// <summary>
/// Represents a collapsible group of cameras in the left-panel list,
/// keyed by a city/road code (e.g. "HOU", "IH-45 GULF").
/// </summary>
public partial class CameraGroupViewModel : ObservableObject
{
    /// <summary>Display label shown in the group header.</summary>
    public string GroupKey { get; }

    /// <summary>Cameras belonging to this group.</summary>
    public ObservableCollection<CameraItem> Cameras { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    public CameraGroupViewModel(string groupKey)
    {
        GroupKey = groupKey;
    }

    /// <summary>Toggles the expanded/collapsed state of the group.</summary>
    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
