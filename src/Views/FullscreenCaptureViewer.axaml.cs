using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CoastalCommandCenter.ViewModels;

namespace CoastalCommandCenter.Views;

public partial class FullscreenCaptureViewer : UserControl
{
    private double _scale = 1.0;
    private double _tx, _ty;
    private bool _isPanning;
    private Point _panStart;
    private double _txAtPanStart, _tyAtPanStart;

    private ScaleTransform? _scaleT;
    private TranslateTransform? _translateT;
    private MainViewModel? _subscribedVm;

    public FullscreenCaptureViewer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _scaleT = new ScaleTransform(1, 1);
        _translateT = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(_scaleT);
        group.Children.Add(_translateT);
        CaptureImage.RenderTransform = group;
        CaptureImage.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);

        DataContextChanged += OnDataContextChanged;
        SubscribeToViewModel(DataContext);

        ViewportBorder.LayoutUpdated += OnViewportFirstLayout;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        DataContextChanged -= OnDataContextChanged;
        UnsubscribeFromViewModel();
    }

    private void OnViewportFirstLayout(object? sender, EventArgs e)
    {
        ViewportBorder.LayoutUpdated -= OnViewportFirstLayout;
        ResetView();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        SubscribeToViewModel(DataContext);
    }

    private void SubscribeToViewModel(object? dc)
    {
        UnsubscribeFromViewModel();
        _subscribedVm = dc as MainViewModel;
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedVm = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedCapture))
            ResetView();
    }

    private void ResetView()
    {
        _scale = 1.0;
        _isPanning = false;

        var bmp = CaptureImage.Source as Bitmap;
        var imgW = bmp?.PixelSize.Width ?? 800.0;
        var imgH = bmp?.PixelSize.Height ?? 600.0;

        var vpW = ViewportBorder.Bounds.Width;
        var vpH = ViewportBorder.Bounds.Height;

        if (vpW > 0 && vpH > 0)
        {
            _tx = (vpW - imgW) / 2.0;
            _ty = (vpH - imgH) / 2.0;
        }
        else
        {
            _tx = 0;
            _ty = 0;
        }

        ApplyTransform();
    }

    private void ApplyTransform()
    {
        if (_scaleT == null || _translateT == null) return;
        _scaleT.ScaleX = _scale;
        _scaleT.ScaleY = _scale;
        _translateT.X = _tx;
        _translateT.Y = _ty;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        ZoomAround(e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1, e.GetPosition(ViewportBorder));
        e.Handled = true;
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e)
        => ZoomAround(1.25, ViewportCenter());

    private void OnZoomOutClick(object? sender, RoutedEventArgs e)
        => ZoomAround(1.0 / 1.25, ViewportCenter());

    private Point ViewportCenter()
        => new(ViewportBorder.Bounds.Width / 2.0, ViewportBorder.Bounds.Height / 2.0);

    private void ZoomAround(double factor, Point anchor)
    {
        var newScale = Math.Clamp(_scale * factor, 0.05, 30.0);
        var f = newScale / _scale;
        _tx = anchor.X * (1 - f) + _tx * f;
        _ty = anchor.Y * (1 - f) + _ty * f;
        _scale = newScale;
        ApplyTransform();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ViewportBorder).Properties.IsLeftButtonPressed) return;
        _isPanning = true;
        _panStart = e.GetPosition(ViewportBorder);
        _txAtPanStart = _tx;
        _tyAtPanStart = _ty;
        e.Pointer.Capture(ViewportBorder);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(ViewportBorder);
        _tx = _txAtPanStart + (pos.X - _panStart.X);
        _ty = _tyAtPanStart + (pos.Y - _panStart.Y);
        ApplyTransform();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        e.Pointer.Capture(null);
    }
}
