namespace DbExplorer.App.Tests.Settings;

/// <summary>
/// Tests that replace <c>SettingsService.Instance</c> change a global every view model reads: they run on their own, or a
/// test running beside them sees another default row limit.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalSettingsCollection
{
    public const string Name = "global settings";
}
