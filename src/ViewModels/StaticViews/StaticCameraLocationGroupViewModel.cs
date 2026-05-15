using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CoastalCommandCenter.ViewModels;

public partial class StaticCameraLocationGroupViewModel : ObservableObject
{
    public required string Location { get; init; }
    public ObservableCollection<StaticCameraSelectorItem> Cameras { get; } = [];

    [ObservableProperty] private bool _isExpanded = true;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
