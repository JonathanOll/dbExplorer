using DbExplorer.Core.Updates;

namespace DbExplorer.Core.Tests.Updates;

/// <summary>Scripted gh: records every call and answers with a canned result (or runs a hook, e.g. to write the downloaded file).</summary>
internal sealed class FakeGhRunner : IGhRunner
{
    private readonly Func<IReadOnlyList<string>, GhResult> _handler;

    public List<string[]> Calls { get; } = [];

    public FakeGhRunner(GhResult result) => _handler = _ => result;
    public FakeGhRunner(Func<IReadOnlyList<string>, GhResult> handler) => _handler = handler;

    public Task<GhResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add(args.ToArray());
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_handler(args));
    }
}
