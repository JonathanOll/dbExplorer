using System.Diagnostics;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Groups;

public enum MemberState { Pending, Running, Done, Failed, Cancelled }

/// <param name="Rows">Total rows returned by the member, once it ended.</param>
/// <param name="Duration">Time spent on the member (connecting included), once it ended.</param>
/// <param name="Error">The failure message of a failed member.</param>
public sealed record MemberProgress(Guid ConnectionId, MemberState State, int? Rows, TimeSpan? Duration, string? Error);

/// <param name="Statements">The statement results the member produced, also for a failed or cancelled member (what ran before).</param>
public sealed record MemberResult(
    ConnectionConfig Connection,
    MemberState State,
    IReadOnlyList<StatementResult> Statements,
    TimeSpan Duration,
    string? Error);

/// <param name="MaxRows">Row limit per result set and per member (the existing <see cref="ScriptOptions.MaxRows"/>, at most 100000).</param>
/// <param name="MaxParallel">Members running at the same time (at least 1).</param>
/// <param name="Statistics">SQL Server: every member sets STATISTICS IO / TIME on its connection (a session setting applied by the
/// connection, not script text, so the read-only guard is unchanged) and returns the server messages with its results.</param>
public sealed record GroupRunOptions(int MaxRows = 1000, int MaxParallel = 4, bool Statistics = false);

/// <summary>The run was refused before any member was contacted: a write statement, mixed kinds, or nothing to run.</summary>
public sealed class GroupRunRefusedException(string message) : Exception(message);

/// <summary>
/// Runs read-only statements on every member of a group, in parallel. Safety first: a group run is read and compare only,
/// so before anything is opened every statement goes through <see cref="ReadOnlyGuard"/> for the group's kind and a single
/// write statement refuses the whole run (<see cref="GroupRunRefusedException"/>), whatever the members' own read-only
/// flags say. The run then forces <see cref="ScriptOptions.ReadOnly"/> on every member (the member's own lexical check and
/// read-only database context as defence in depth). A failing member never stops the others and is reported as
/// <see cref="MemberState.Failed"/>; cancelling (all, or one member) never throws.
/// </summary>
/// <remarks>
/// The <see cref="ScriptConnection"/> returned by <c>connectionFor</c> belongs to this run: it is disposed when its member
/// has ended, so the delegate must hand out a connection of its own for each call. A connection whose database kind is not
/// the group's kind is never used. One run at a time per instance.
/// </remarks>
public sealed class GroupRunner(Func<ConnectionConfig, CancellationToken, Task<ScriptConnection>> connectionFor)
{
    private const int MaxRowsCeiling = 100_000;

    private volatile MemberSlot[] _slots = [];

    /// <summary>Cancels one member of the current run: a running one is interrupted, a pending one never starts. No-op otherwise.</summary>
    public void CancelMember(Guid connectionId)
    {
        foreach (var slot in _slots)
            if (slot.Config.Id == connectionId) slot.Cancel();
    }

    public async Task<IReadOnlyList<MemberResult>> RunAsync(
        IReadOnlyList<ConnectionConfig> members,
        IReadOnlyList<ScriptStatement> statements,
        GroupRunOptions options,
        IProgress<MemberProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(options);

        if (members.Count == 0) return [];
        var kind = members[0].Kind;
        Refuse(members, statements, kind);

        var memberOptions = new ScriptOptions(Math.Clamp(options.MaxRows, 1, MaxRowsCeiling), ReadOnly: true, Kind: kind, Statistics: options.Statistics);
        using var gate = new SemaphoreSlim(Math.Max(1, options.MaxParallel));
        var slots = members.Select(m => new MemberSlot(m, ct)).ToArray();
        _slots = slots;
        try
        {
            foreach (var slot in slots)
                progress?.Report(new MemberProgress(slot.Config.Id, MemberState.Pending, null, null, null));

            var tasks = slots.Select(slot => RunMemberAsync(slot, kind, statements, memberOptions, gate, progress)).ToArray();
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            _slots = [];
            foreach (var slot in slots) slot.Dispose();
        }
    }

    /// <summary>The authoritative guard: nothing is opened unless every statement is a plain read for the group's kind.</summary>
    private static void Refuse(IReadOnlyList<ConnectionConfig> members, IReadOnlyList<ScriptStatement> statements, DbKind kind)
    {
        if (members.Any(m => m.Kind != kind))
            throw new GroupRunRefusedException("All connections of a group must be of the same database kind.");
        if (statements.Count == 0)
            throw new GroupRunRefusedException("There is no statement to run.");
        foreach (var statement in statements)
            if (ReadOnlyGuard.IsWrite(statement.Text, kind))
                throw new GroupRunRefusedException(
                    "A group run is read-only: write statements are not allowed, whatever each connection's own setting is.");
    }

    private async Task<MemberResult> RunMemberAsync(
        MemberSlot slot,
        DbKind kind,
        IReadOnlyList<ScriptStatement> statements,
        ScriptOptions memberOptions,
        SemaphoreSlim gate,
        IProgress<MemberProgress>? progress)
    {
        var token = slot.Token;
        var config = slot.Config;
        var watch = Stopwatch.StartNew();

        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Finish(MemberState.Cancelled, [], null);
        }

        var results = new List<StatementResult>();
        try
        {
            progress?.Report(new MemberProgress(config.Id, MemberState.Running, null, null, null));
            ScriptConnection? connection = null;
            try
            {
                connection = await ConnectAsync(config, token).ConfigureAwait(false);
                if (connection.Session.Driver.Kind != kind)
                    return Finish(MemberState.Failed, results,
                        $"The connection is a {connection.Session.Driver.Kind} database, the group is {kind}: not run.");

                await foreach (var result in connection.RunAsync(statements, memberOptions, token).ConfigureAwait(false))
                    results.Add(result);

                // A cancelled statement yields no result, so a run that has all its results ended normally even if the
                // cancellation arrived right after.
                var error = results.Select(r => r.Error).FirstOrDefault(e => e is not null);
                if (error is not null) return Finish(MemberState.Failed, results, error);
                if (results.Count == statements.Count) return Finish(MemberState.Done, results, null);
                return token.IsCancellationRequested
                    ? Finish(MemberState.Cancelled, results, null)
                    : Finish(MemberState.Failed, results, "The run ended before its last statement.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return Finish(MemberState.Cancelled, results, null);
            }
            catch (Exception ex)
            {
                return Finish(MemberState.Failed, results, ex.Message);
            }
            finally
            {
                DisposeQuietly(connection);
            }
        }
        finally
        {
            gate.Release();
        }

        MemberResult Finish(MemberState state, IReadOnlyList<StatementResult> statementResults, string? error)
        {
            watch.Stop();
            var rows = statementResults.Sum(r => r.Sets.Sum(s => s.Rows.Count));
            progress?.Report(new MemberProgress(config.Id, state, rows, watch.Elapsed, error));
            return new MemberResult(config, state, statementResults, watch.Elapsed, error);
        }
    }

    /// <summary>
    /// Opens the member's connection, giving up when <paramref name="token"/> is cancelled even if the delegate ignores it
    /// (an unreachable server must not hold the run); a connection that arrives after that is disposed.
    /// </summary>
    private async Task<ScriptConnection> ConnectAsync(ConnectionConfig config, CancellationToken token)
    {
        var connecting = connectionFor(config, token);
        try
        {
            return await connecting.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = connecting.ContinueWith(
                t => { if (t.IsCompletedSuccessfully) DisposeQuietly(t.Result); else _ = t.Exception; },
                TaskScheduler.Default);
            throw;
        }
    }

    private static void DisposeQuietly(ScriptConnection? connection)
    {
        try { connection?.Dispose(); }
        catch (Exception) { /* nothing more to do for a connection being released */ }
    }

    /// <summary>One member of the current run: its cancellation (linked to the run's) and its place in the result order.</summary>
    private sealed class MemberSlot : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private int _disposed;

        public MemberSlot(ConnectionConfig config, CancellationToken runToken)
        {
            Config = config;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            Token = _cts.Token;
        }

        public ConnectionConfig Config { get; }
        public CancellationToken Token { get; }

        public void Cancel()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { /* the run has ended */ }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _cts.Dispose();
        }
    }
}
