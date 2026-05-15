using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using CoastalCommandCenter.Models.Health;
using CoastalCommandCenter.Services.Health;
using CoastalCommandCenter.ViewModels.Health;
using CoastalCommandCenter.Views.Controls;

namespace CoastalCommandCenter.Views;

public partial class StreamHealthPopup : Window
{
    private StreamHealthPopupViewModel? _viewModel;
    private TextBlock? _titleText;
    private TextBlock? _classificationText;
    private Border? _classificationBadge;
    private TextBlock? _timeInStateText;
    private TextBlock? _reasonText;
    private StreamHealthChart? _chart;
    private ItemsControl? _eventList;

    private Point _dragStart;
    private bool _isDragging;

    public StreamHealthPopup() : this(null)
    {
    }

    public StreamHealthPopup(CameraHealthState? state)
    {
        InitializeComponent();

        _titleText = this.FindControl<TextBlock>("TitleText");
        _classificationText = this.FindControl<TextBlock>("ClassificationText");
        _classificationBadge = this.FindControl<Border>("ClassificationBadge");
        _timeInStateText = this.FindControl<TextBlock>("TimeInStateText");
        _reasonText = this.FindControl<TextBlock>("ReasonText");
        _chart = this.FindControl<StreamHealthChart>("Chart");
        _eventList = this.FindControl<ItemsControl>("EventList");

        if (state is not null)
            Bind(state);
    }

    public void Bind(CameraHealthState state)
    {
        _viewModel?.Dispose();
        _viewModel = new StreamHealthPopupViewModel(state);
        DataContext = _viewModel;

        if (_titleText is not null) _titleText.Text = state.CameraName;
        if (_chart is not null) _chart.State = state;
        if (_eventList is not null) _eventList.ItemsSource = _viewModel.Events;

        ApplyClassification();
        ApplyTimeInState();
        ApplyReason();

        _viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnViewModelChanged(sender, e));
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(StreamHealthPopupViewModel.Classification):
                ApplyClassification();
                break;
            case nameof(StreamHealthPopupViewModel.TimeInStateLabel):
                ApplyTimeInState();
                break;
            case nameof(StreamHealthPopupViewModel.ClassificationReason):
                ApplyReason();
                break;
        }
    }

    private void ApplyClassification()
    {
        if (_viewModel is null || _classificationText is null || _classificationBadge is null) return;
        _classificationText.Text = _viewModel.Classification.ToString().ToUpperInvariant();
        _classificationBadge.Background = HealthBrushes.ForClassification(_viewModel.Classification);
    }

    private void ApplyTimeInState()
    {
        if (_viewModel is null || _timeInStateText is null) return;
        _timeInStateText.Text = $"{_viewModel.Classification} for {_viewModel.TimeInStateLabel}";
    }

    private void ApplyReason()
    {
        if (_viewModel is null || _reasonText is null) return;
        _reasonText.Text = _viewModel.ClassificationReason;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.Dispose();
            _viewModel = null;
        }
        if (_chart is not null) _chart.State = null;
        base.OnClosed(e);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _isDragging = true;
    }

    private void TitleBar_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging) return;
        var currentPos = e.GetPosition(null);
        var delta = currentPos - _dragStart;
        Position = new Avalonia.PixelPoint((int)(Position.X + delta.X), (int)(Position.Y + delta.Y));
        _dragStart = currentPos;
    }

    private void TitleBar_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
    }

    private void CloseButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
