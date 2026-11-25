using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;

namespace DbExplorer.App.Appearance;

/// <summary>
/// UI scale: the content presenter of every window sits in a <see cref="LayoutTransformControl"/> bound to the <c>DbxUiScale</c>
/// resource. A layout transform (not a render transform) re-measures the content for the scaled space, so nothing clips and the
/// window keeps its size. It scales text, icons, row heights and hard-coded sizes alike, which scaling the font tokens alone cannot
/// do. The wrapper goes around the presenter inside the window's template, before the content exists: the content itself is never
/// re-parented, so its bindings are untouched. Popups (menus, combo lists, tooltips) are separate top-levels and stay at 100 %.
/// </summary>
public static class UiScaleHost
{
    private const string Marker = "dbx-scale";
    private static bool s_installed;

    /// <summary>Wraps the content presenter of every window the moment its template is applied (before the first layout).</summary>
    public static void Install()
    {
        if (s_installed) return;
        s_installed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<Window>((window, e) => Wrap(e.NameScope.Find<ContentPresenter>("PART_ContentPresenter")));
    }

    private static void Wrap(ContentPresenter? presenter)
    {
        if (presenter?.Parent is not Decorator parent || parent is LayoutTransformControl { Tag: Marker }) return;
        var host = new LayoutTransformControl { Tag = Marker };
        host.Bind(LayoutTransformControl.LayoutTransformProperty, host.GetResourceObservable("DbxUiScale"));
        parent.Child = null;
        host.Child = presenter;
        parent.Child = host;
    }
}
