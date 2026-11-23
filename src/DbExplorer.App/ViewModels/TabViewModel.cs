using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// Base class of everything shown in the main tab area (table views, script editors). Subclasses provide the
/// title; the connection drives the colour chip on the tab header.
/// </summary>
public abstract class TabViewModel(ConnectionConfig connection) : ObservableObject
{
    public ConnectionConfig Connection { get; } = connection;

    public abstract string Title { get; }

    /// <summary>
    /// Asked before the tab is closed; false vetoes the close. Tabs with unsaved work override it to prompt.
    /// </summary>
    public virtual Task<bool> ConfirmCloseAsync() => Task.FromResult(true);

    /// <summary>
    /// True when <see cref="ConfirmCloseAsync"/> is going to ask the user something. The shell activates such a tab
    /// before asking, so the prompt is about the tab on screen. Tabs with nothing to ask are left alone.
    /// </summary>
    public virtual bool NeedsCloseConfirmation => false;

    /// <summary>
    /// <see cref="ConfirmCloseAsync"/> for the window being closed (not a single tab): a tab whose unsaved work is kept
    /// by other means (a script's autosave) can answer yes without asking. Defaults to the tab-close prompt.
    /// </summary>
    public virtual Task<bool> ConfirmExitAsync() => ConfirmCloseAsync();

    /// <summary>True when <see cref="ConfirmExitAsync"/> is going to ask (the shell shows the tab first).</summary>
    public virtual bool NeedsExitConfirmation => NeedsCloseConfirmation;

    /// <summary>
    /// Called on a tab whose <see cref="ConfirmCloseAsync"/> said yes when the close it was part of was abandoned
    /// afterwards (another tab answered Cancel): the tab stays open and must go back to normal.
    /// </summary>
    public virtual void CloseAborted() { }

    /// <summary>Short status shown next to the title (e.g. "read-only"), or null.</summary>
    public virtual string? Badge => null;

    public string ChipColor => ConnectionPalette.ColorFor(Connection.Id);

    /// <summary>Resource key of the vector icon (Styles/Icons.axaml) drawn on the tab header.</summary>
    public virtual string IconKey => "IconTable";

    /// <summary>True while the tab holds changes that are not saved yet: its header shows a dirty marker.</summary>
    public virtual bool HasUnsavedWork => false;
}
