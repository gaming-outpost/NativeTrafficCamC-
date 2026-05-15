using CommunityToolkit.Mvvm.ComponentModel;

namespace CoastalCommandCenter.ViewModels;

public partial class FeedStateTabViewModel : ObservableObject
{
    public required string State { get; init; }

    [ObservableProperty]
    private bool _isActive;
}
