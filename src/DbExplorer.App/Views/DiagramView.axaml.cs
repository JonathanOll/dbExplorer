using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Diagrams;

namespace DbExplorer.App.Views;

/// <summary>
/// The ER diagram tab. All state lives in <see cref="DiagramTabViewModel"/>; this class only does what needs a visual: the
/// free canvas (zoom, pan, centring a table), fitting the zoom to the window, the context menu, keys and the PNG export.
/// </summary>
public partial class DiagramView : UserControl
{
    /// <summary>A diagram opens fitted, but never so small that the table names cannot be read.</summary>
    private const double OpeningZoomFloor = 0.5;

    private const double ArrowPan = 60;

    private readonly FreeCanvas _canvas;
    private DiagramTabViewModel? _tab;
    private DiagramScene? _framed;
    private Point? _zoomAnchor;

    public DiagramView()
    {
        InitializeComponent();
        _canvas = new FreeCanvas(Scroller, Zoomer, OnWheelZoom);
        DataContextChanged += (_, _) => Attach();
        // The surface only draws what is in view: it repaints when the view moves.
        Scroller.ScrollChanged += (_, _) => Surface.InvalidateVisual();
        Scroller.SizeChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            Surface.InvalidateVisual();
            Frame();
        }, DispatcherPriority.Loaded);
        Scroller.KeyDown += OnKeyDown;
        Scroller.ContextRequested += OnContextRequested;
        SearchBox.KeyDown += OnSearchKeyDown;
        Surface.TableActivated += async table => await OpenAsync(table);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();   // the tab outlives the view
    }

    private void Detach()
    {
        if (_tab is null) return;
        _tab.PropertyChanged -= OnTabChanged;
        _tab.CentreRequested -= OnCentreRequested;
        _tab = null;
    }

    private void Attach()
    {
        if (ReferenceEquals(_tab, DataContext)) return;
        Detach();
        _tab = DataContext as DiagramTabViewModel;
        _framed = null;
        if (_tab is null) return;
        _tab.PropertyChanged += OnTabChanged;
        _tab.CentreRequested += OnCentreRequested;
        Dispatcher.UIThread.Post(() =>
        {
            ApplyZoom();
            Frame();
        }, DispatcherPriority.Loaded);
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiagramTabViewModel.Zoom)) ApplyZoom();
        else if (e.PropertyName == nameof(DiagramTabViewModel.Scene)) Dispatcher.UIThread.Post(Frame, DispatcherPriority.Loaded);
    }

    private void ApplyZoom()
    {
        _canvas.SetZoom(_tab?.Zoom ?? 1, _zoomAnchor);
        Surface.InvalidateVisual();
    }

    // A new layout is shown fitted, or around the table it was opened on; once per layout.
    private void Frame()
    {
        if (_tab?.Scene is not { Model.Tables.Count: > 0 } scene || ReferenceEquals(scene, _framed)) return;
        if (Scroller.Bounds.Width < 50 || Scroller.Bounds.Height < 50) return;   // not laid out yet: the size change calls again
        _framed = scene;
        _tab.SetZoom(_tab.FitZoom(Scroller.Bounds.Width, Scroller.Bounds.Height, OpeningZoomFloor));
        ApplyZoom();
        if (_tab.IsFocused && _tab.SelectedTable >= 0) Centre(_tab.SelectedTable);
        else _canvas.Home();
        Surface.InvalidateVisual();
    }

    private void Fit()
    {
        if (_tab?.Scene is null) return;
        _tab.SetZoom(_tab.FitZoom(Scroller.Bounds.Width, Scroller.Bounds.Height));
        _canvas.Home();
    }

    private void Centre(int table)
    {
        if (_tab?.Scene is not { } scene || table < 0) return;
        var r = scene.Rect(table);
        // A card taller than the view is shown from its title.
        var half = Scroller.Bounds.Height / 2 / _tab.Zoom;
        _canvas.Centre(new Point(r.CenterX, Math.Min(r.CenterY, r.Y + Math.Max(0, half - 20))));
    }

    private void OnCentreRequested(int table)
    {
        // A match found while zoomed far out is shown readable.
        if (_tab is { Zoom: < DiagramPainter.DetailZoom }) _tab.SetZoom(1);
        Centre(table);
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => _tab?.ZoomBy(0.1);

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => _tab?.ZoomBy(-0.1);

    private void OnFitClick(object? sender, RoutedEventArgs e) => Fit();

    // The zoom changes through the tab: the anchor is remembered while it does, so the canvas zooms around the cursor.
    private void OnWheelZoom(double step, Point cursor)
    {
        _zoomAnchor = cursor;
        _tab?.ZoomBy(step);
        _zoomAnchor = null;
    }

    // A segment cannot be switched off by clicking it again.
    private void OnSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { IsChecked: not true } button) button.IsChecked = true;
    }

    // ---- keyboard ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.KeyModifiers != KeyModifiers.Control || e.Key != Key.F) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_tab is null) return;
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add: _tab.ZoomBy(0.1); break;
                case Key.OemMinus or Key.Subtract: _tab.ZoomBy(-0.1); break;
                case Key.D0 or Key.NumPad0: Fit(); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Enter && _tab.SelectedTable >= 0)
        {
            e.Handled = true;
            await OpenAsync(_tab.SelectedTable);
            return;
        }
        Vector? move = e.Key switch
        {
            Key.Left => new Vector(-ArrowPan, 0),
            Key.Right => new Vector(ArrowPan, 0),
            Key.Up => new Vector(0, -ArrowPan),
            Key.Down => new Vector(0, ArrowPan),
            _ => null,
        };
        if (move is not { } d) return;
        Scroller.Offset = new Vector(Math.Max(0, Scroller.Offset.X + d.X), Math.Max(0, Scroller.Offset.Y + d.Y));
        e.Handled = true;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _tab?.Search(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        else if (e.Key == Key.Escape)
        {
            if (_tab is not null) _tab.SearchText = "";
            Scroller.Focus();
        }
        else return;
        e.Handled = true;
    }

    private async Task OpenAsync(int table)
    {
        try
        {
            if (_tab is not null) await _tab.OpenTableAsync(table);
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine("Opening a table from the diagram failed: " + e.Message);
        }
    }

    // ---- context menu of a table ----

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_tab?.Scene is not { } scene) return;
        // The right press selected the table under the pointer (the menu key keeps the selection).
        var table = _tab.SelectedTable;
        if (table < 0 || table >= scene.Model.Tables.Count) return;
        var name = scene.Model.Tables[table].Ref;
        var tab = _tab;

        var open = new MenuItem { Header = "Open data", FontWeight = Avalonia.Media.FontWeight.SemiBold, InputGesture = new KeyGesture(Key.Enter) };
        open.Click += async (_, _) => await OpenAsync(table);
        var neighbours = new MenuItem { Header = "Show this table and its neighbours" };
        neighbours.Click += (_, _) => tab.FocusOn(name);
        var copy = new MenuItem { Header = "Copy name" };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(name.ToString());
            }
            catch (Exception) { /* busy clipboard */ }
        };
        var menu = new ContextMenu { ItemsSource = new[] { open, neighbours, copy } };
        menu.Open(Surface);
        e.Handled = true;
    }

    // ---- export ----

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_tab?.Scene is not { } scene || TopLevel.GetTopLevel(this) is not { } top) return;
        var tab = _tab;
        try
        {
            var name = string.Concat(tab.Title.Replace("ER · ", "").Split(Path.GetInvalidFileNameChars()));
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export the diagram",
                SuggestedFileName = $"{name}-diagram.png",
                DefaultExtension = "png",
                FileTypeChoices = [new FilePickerFileType("PNG picture") { Patterns = ["*.png"] }],
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            Surface.Painter.SavePng(scene, stream);
        }
        catch (Exception ex)
        {
            tab.Error = $"Cannot export the diagram: {ErrorText.Describe(ex)}";
        }
    }
}
