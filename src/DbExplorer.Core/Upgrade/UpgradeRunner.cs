using System.Data.Common;
using System.Diagnostics;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Upgrade;

/// <param name="Name">What the user sees (and the log shows) for the script, e.g. <c>v0860-sprint 143/adj - 003 - x.sql</c>.</param>
public sealed record UpgradeScript(string Name, string Text);

/// <param name="MaxParallel">Databases upgraded at the same time (at least 1), like "Run on group".</param>
/// <param name="CommandTimeoutSeconds">Per batch; 0 = no timeout (an index build can take long). The run stays cancellable.</param>
public sealed record UpgradeRunOptions(int MaxParallel = 4, int CommandTimeoutSeconds = 0);

public enum ScriptOutcomeState { Done, Failed, Cancelled, Skipped }

/// <param name="BatchesRun">Batches that completed (the failed or cancelled one excluded).</param>
/// <param name="Error">For a failed script: <c>batch k of n: message</c>.</param>
public sealed record ScriptOutcome(string Name, ScriptOutcomeState State, int BatchesRun, TimeSpan Duration, string? Error);

/// <param name="Scripts">One outcome per script of the run, in order; scripts never reached are <see cref="ScriptOutcomeState.Skipped"/>.</param>
/// <param name="Error">For a failed database: <c>script: batch k of n: message</c>, or the connection failure.</param>
/// <param name="Note">Something the user should know about this database that is not an error, e.g. it was listed more than once and ran once.</param>
public sealed record UpgradeResult(
    ConnectionConfig Connection,
    MemberState State,
    IReadOnlyList<ScriptOutcome> Scripts,
    TimeSpan Duration,
    string? Error,
    string? Note = null);

/// <param name="ScriptIndex">1-based index of the script being played (the last one reached once the database ended); 0 while pending.</param>
public sealed record UpgradeProgress(Guid ConnectionId, MemberState State, int ScriptIndex, int ScriptCount, string? ScriptName, string? Error);

/// <summary>
/// The run was refused before any database was contacted: read-only, mixed or non SQL Server targets, no script, a <c>GO n</c> or other
/// unsupported GO line, a <c>USE</c> statement, or another run in progress on the same runner.
/// </summary>
public sealed class UpgradeRefusedException(string message) : Exception(message);

/// <summary>
/// Plays SQL scripts on databases, in parallel. This is the write-capable runner of the n2f upgrade and it is deliberately
/// separate from <see cref="GroupRunner"/> (which stays read-only and is never reached from here, nor reaches this): it shares no
/// code with it, uses plain <see cref="DbCommand.ExecuteNonQueryAsync(CancellationToken)"/> on a connection of its own, and the
/// read-only guard of the group run is not involved. A connection flagged read-only is refused before anything is opened.
/// </summary>
/// <remarks>
/// Per database: scripts in order, each split into GO batches (<see cref="ScriptSplitter.SplitBatches"/>), no transaction, command
/// timeout <see cref="UpgradeRunOptions.CommandTimeoutSeconds"/>. The first failing batch stops that database only. Cancelling (all,
/// or one database) never throws. The connection returned by the delegate belongs to the run: it is disposed when its database has
/// ended. A result set a script returns is discarded by the server round trip, never truncated, and never cancels the batch.
/// One run at a time per instance (a second concurrent <see cref="RunAsync"/> is refused). Only SQL Server targets are accepted: the
/// scripts are T-SQL and are split with the SQL Server lexer. A database listed twice runs once (the first entry).
/// </remarks>
public sealed class UpgradeRunner
{
    private readonly Func<ConnectionConfig, CancellationToken, Task<DbConnection>> openConnection;
    private readonly bool _requireSqlServer;
    private volatile MemberSlot[] _slots = [];
    private int _running;

    public UpgradeRunner(Func<ConnectionConfig, CancellationToken, Task<DbConnection>> openConnection)
        : this(openConnection, requireSqlServer: true)
    {
    }

    /// <summary>Test seam only: lets the unit tests play the scripts on a SQLite file. Production code uses the public constructor.</summary>
    internal UpgradeRunner(Func<ConnectionConfig, CancellationToken, Task<DbConnection>> openConnection, bool requireSqlServer)
    {
        this.openConnection = openConnection;
        _requireSqlServer = requireSqlServer;
    }

    /// <summary>Cancels one database of the current run: a running one is interrupted, a pending one never starts. No-op otherwise.</summary>
    public void CancelMember(Guid connectionId)
    {
        foreach (var slot in _slots)
            if (slot.Config.Id == connectionId) slot.Cancel();
    }

    public async Task<IReadOnlyList<UpgradeResult>> RunAsync(
        IReadOnlyList<ConnectionConfig> targets,
        IReadOnlyList<UpgradeScript> scripts,
        UpgradeRunOptions options,
        IProgress<UpgradeProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(options);

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new UpgradeRefusedException("An upgrade is already running on this runner; wait for it to end. Nothing was run.");

        try
        {
            var batches = Refuse(targets, scripts);
            var (distinct, notes) = Dedupe(targets);

            using var gate = new SemaphoreSlim(Math.Max(1, options.MaxParallel));
            var slots = distinct.Select(t => new MemberSlot(t, ct)).ToArray();
            _slots = slots;
            try
            {
                foreach (var slot in slots)
                    Report(progress, new UpgradeProgress(slot.Config.Id, MemberState.Pending, 0, scripts.Count, null, null));

                var tasks = slots.Select(slot => RunMemberAsync(slot, scripts, batches, options, gate, progress, notes.GetValueOrDefault(slot.Config.Id))).ToArray();
                return await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                _slots = [];
                foreach (var slot in slots) slot.Dispose();
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>A throwing reporter (a UI bug) must never lose the results of a run that writes to databases.</summary>
    private static void Report(IProgress<UpgradeProgress>? progress, UpgradeProgress value)
    {
        try { progress?.Report(value); }
        catch (Exception) { /* the run goes on; the results are returned anyway */ }
    }

    /// <summary>One entry per connection id, the first one kept; the note says how many times a repeated database was listed.</summary>
    private static (List<ConnectionConfig> Distinct, Dictionary<Guid, string> Notes) Dedupe(IReadOnlyList<ConnectionConfig> targets)
    {
        var distinct = new List<ConnectionConfig>();
        var counts = new Dictionary<Guid, int>();
        foreach (var t in targets)
        {
            if (counts.TryGetValue(t.Id, out var n)) counts[t.Id] = n + 1;
            else { counts[t.Id] = 1; distinct.Add(t); }
        }
        var notes = counts.Where(kv => kv.Value > 1)
            .ToDictionary(kv => kv.Key, kv => $"Listed {kv.Value} times; the scripts were run once.");
        return (distinct, notes);
    }

    /// <summary>The authoritative check: nothing is opened unless the targets are writable and the scripts can be played as written.</summary>
    private IReadOnlyList<ScriptStatement>[] Refuse(IReadOnlyList<ConnectionConfig> targets, IReadOnlyList<UpgradeScript> scripts)
    {
        var refusal = UpgradeRules.Refusal(targets, _requireSqlServer);
        if (refusal is not null) throw new UpgradeRefusedException(refusal);
        if (scripts.Count == 0) throw new UpgradeRefusedException("There is no script to run.");

        foreach (var script in scripts)
        {
            if (ScriptSplitter.FindUnsupportedGo(script.Text) is { } go)
                throw new UpgradeRefusedException($"{script.Name}: line {go.Line}: {go.Reason}. Nothing was run.");
            if (ScriptAnalyzer.FindUse(script.Text) is { } use)
                throw new UpgradeRefusedException($"{script.Name}: {ScriptAnalyzer.UseMessage(use)} Nothing was run.");
        }

        return scripts.Select(s => ScriptSplitter.SplitBatches(s.Text)).ToArray();
    }

    private async Task<UpgradeResult> RunMemberAsync(
        MemberSlot slot,
        IReadOnlyList<UpgradeScript> scripts,
        IReadOnlyList<ScriptStatement>[] batches,
        UpgradeRunOptions options,
        SemaphoreSlim gate,
        IProgress<UpgradeProgress>? progress,
        string? note)
    {
        var token = slot.Token;
        var config = slot.Config;
        var watch = Stopwatch.StartNew();
        var outcomes = scripts.Select(s => new ScriptOutcome(s.Name, ScriptOutcomeState.Skipped, 0, TimeSpan.Zero, null)).ToArray();
        var reached = 0;   // 1-based index of the last script started

        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Finish(MemberState.Cancelled, null);
        }

        try
        {
            DbConnection? connection = null;
            try
            {
                token.ThrowIfCancellationRequested();   // cancelled while waiting for a free slot: never connect
                connection = await ConnectAsync(config, token).ConfigureAwait(false);

                for (var i = 0; i < scripts.Count; i++)
                {
                    reached = i + 1;
                    Report(progress, new UpgradeProgress(config.Id, MemberState.Running, reached, scripts.Count, scripts[i].Name, null));

                    var failure = await PlayScriptAsync(connection, scripts[i], batches[i], i, options, token).ConfigureAwait(false);
                    if (failure is not null) return Finish(MemberState.Failed, $"{scripts[i].Name}: {failure}");
                }
                return Finish(MemberState.Done, null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return Finish(MemberState.Cancelled, null);
            }
            catch (Exception ex)
            {
                return Finish(MemberState.Failed, ex.Message);
            }
            finally
            {
                await DisposeQuietlyAsync(connection).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }

        UpgradeResult Finish(MemberState state, string? error)
        {
            watch.Stop();
            Report(progress, new UpgradeProgress(
                config.Id, state, reached, scripts.Count, reached > 0 ? scripts[reached - 1].Name : null, error));
            return new UpgradeResult(config, state, outcomes, watch.Elapsed, error, note);
        }

        // Plays one script; returns the failure message (without the script name) or null. Cancellation propagates as an
        // exception after the outcome has been recorded.
        async Task<string?> PlayScriptAsync(
            DbConnection connection, UpgradeScript script, IReadOnlyList<ScriptStatement> scriptBatches, int index, UpgradeRunOptions opts, CancellationToken tk)
        {
            var scriptWatch = Stopwatch.StartNew();
            var run = 0;
            try
            {
                tk.ThrowIfCancellationRequested();   // a cancel seen between two scripts cancels the script that was about to start
                foreach (var batch in scriptBatches)
                {
                    tk.ThrowIfCancellationRequested();
                    await using var command = connection.CreateCommand();
                    command.CommandText = batch.Text;
                    command.CommandTimeout = opts.CommandTimeoutSeconds;
                    await command.ExecuteNonQueryAsync(tk).ConfigureAwait(false);
                    run++;
                }
                outcomes[index] = new ScriptOutcome(script.Name, ScriptOutcomeState.Done, run, scriptWatch.Elapsed, null);
                return null;
            }
            catch (Exception) when (tk.IsCancellationRequested)
            {
                outcomes[index] = new ScriptOutcome(script.Name, ScriptOutcomeState.Cancelled, run, scriptWatch.Elapsed, null);
                throw new OperationCanceledException(tk);
            }
            catch (Exception ex)
            {
                var message = $"batch {run + 1} of {scriptBatches.Count}: {ex.Message}";
                outcomes[index] = new ScriptOutcome(script.Name, ScriptOutcomeState.Failed, run, scriptWatch.Elapsed, message);
                return message;
            }
        }
    }

    /// <summary>
    /// Opens the database's connection, giving up when <paramref name="token"/> is cancelled even if the delegate ignores it
    /// (an unreachable server must not hold the run); a connection that arrives after that is disposed.
    /// </summary>
    private async Task<DbConnection> ConnectAsync(ConnectionConfig config, CancellationToken token)
    {
        var connecting = openConnection(config, token);
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

    private static void DisposeQuietly(DbConnection? connection)
    {
        try { connection?.Dispose(); }
        catch (Exception) { /* nothing more to do for a connection being released */ }
    }

    private static async ValueTask DisposeQuietlyAsync(DbConnection? connection)
    {
        try { if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* nothing more to do for a connection being released */ }
    }

    /// <summary>One database of the current run: its cancellation (linked to the run's).</summary>
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
