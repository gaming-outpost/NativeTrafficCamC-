using CommunityToolkit.Mvvm.ComponentModel;
using CoastalCommandCenter.Models;

namespace CoastalCommandCenter.ViewModels;

public partial class StaticCameraSelectorItem : ObservableObject
{
    private bool _suppressSelectionChanged;

    public required StaticCameraDefinition Camera { get; init; }
    public required int SortOrder { get; init; }

    public string Name => Camera.Name;
    public string Location => Camera.Location;
    public string Subgroup => Camera.Subgroup;

    [ObservableProperty] private bool _isSelected;

    public event EventHandler? SelectionChanged;

    public void SetSelectedSilently(bool isSelected)
    {
        _suppressSelectionChanged = true;
        IsSelected = isSelected;
        _suppressSelectionChanged = false;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_suppressSelectionChanged)
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
