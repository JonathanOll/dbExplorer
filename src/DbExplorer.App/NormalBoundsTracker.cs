namespace DbExplorer.App;

/// <summary>
/// Remembers the bounds of the NORMAL (not maximised) window while it is used, so they can be saved when it closes
/// maximised. Resize and move events can arrive just before the state changes to Maximized, so the previous distinct
/// value of each is kept and put back when the window turns out to have been maximised by the very bounds just seen.
/// </summary>
public sealed class NormalBoundsTracker
{
    /// <summary>Win32 reports a minimised window at (-32000, -32000): never a real position.</summary>
    private const int MinimizedCoordinate = -32000;

    private (int X, int Y)? _position, _previousPosition;
    private (double Width, double Height)? _size, _previousSize;

    public NormalBoundsTracker(WindowPlacement? initial = null)
    {
        if (initial is null) return;
        if (initial.X is { } x && initial.Y is { } y) _position = (x, y);
        _size = (initial.Width, initial.Height);
    }

    public void PositionChanged(int x, int y, bool isNormal)
    {
        if (!isNormal || x <= MinimizedCoordinate || y <= MinimizedCoordinate) return;
        if (_position is { } old && old != (x, y)) _previousPosition = old;
        _position = (x, y);
    }

    public void SizeChanged(double width, double height, bool isNormal)
    {
        if (!isNormal) return;
        if (_size is { } old && (Math.Abs(old.Width - width) > 0.5 || Math.Abs(old.Height - height) > 0.5)) _previousSize = old;
        _size = (width, height);
    }

    /// <summary>The window just became maximised and currently has these bounds.</summary>
    public void BecameMaximized(int x, int y, double width, double height)
    {
        if (_position == (x, y) && _previousPosition is { } position) _position = position;
        if (_size is { } size && Math.Abs(size.Width - width) <= 0.5 && Math.Abs(size.Height - height) <= 0.5 && _previousSize is { } previous)
            _size = previous;
    }

    /// <summary>What to save, or null while no size is known yet.</summary>
    public WindowPlacement? Placement(bool maximized) =>
        _size is { } size ? new WindowPlacement(_position?.X, _position?.Y, size.Width, size.Height, maximized) : null;
}
