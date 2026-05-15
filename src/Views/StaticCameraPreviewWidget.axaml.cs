using Avalonia.Controls;
using Avalonia.Interactivity;
using CoastalCommandCenter.ViewModels;

namespace CoastalCommandCenter.Views;

public partial class StaticCameraPreviewWidget : UserControl
{
    public StaticCameraPreviewWidget()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is StaticCameraPreviewViewModel vm)
        {
            vm.StartRefresh();
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        if (DataContext is StaticCameraPreviewViewModel vm)
        {
            vm.StopRefresh();
            vm.Dispose();
        }
    }
}
