using System.Text.Json;
using System.Text.Json.Nodes;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;
using DbExplorer.Core.Sessions;

namespace DbExplorer.Core.Claude;

public sealed record ClaudeReply(string Text, bool IsError = false);

/// <summary>
/// Runs the tool calls, whatever carries them. The config is read again for every call, so revoking access applies at
/// once. Every reply is scrubbed of secrets and logged; a call that cannot be logged returns nothing.
/// </summary>
public sealed class ClaudeTools(
    Func<AppConfig> loadConfig, ClaudeDatabase database, ISecretProtector protector, ClaudeActivityLog log, IClaudeApp app)
{
    private const int DefaultTimeoutSeconds = 30;
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(60);

    // A provider that ignores a cancel must not hang the call.
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _running = new(4, 4);

    private sealed class Call
    {
        public string? Connection { get; set; }
        public string? Detail { get; set; }
    }

    public async Task<ClaudeReply> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken ct)
    {
        var call = new Call();
        var outcome = "ok";
        string? failure = null;
        AppConfig? config = null;
        ClaudeReply reply;

        await _running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                var tool = ClaudeToolCatalog.All.FirstOrDefault(t => t.Name == name)
                    ?? throw new ClaudeRefusedException("Unknown tool.");
                config = Load();
                if (!config.Claude.Enabled) throw new ClaudeRefusedException(ClaudePolicy.Disabled);
                reply = new ClaudeReply(await DispatchAsync(config, tool, new ClaudeArguments(arguments, tool), call, ct).ConfigureAwait(false));
            }
            catch (ClaudeRefusedException e)
            {
                (reply, outcome, failure) = (new ClaudeReply(e.Message, true), "refused", e.Message);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                var message = e switch
                {
                    OperationCanceledException or TimeoutException => "The call took too long and was stopped.",
                    ConnectionStageException or DefinitionUnavailableException => e.Message,
                    _ => "Failed: " + e.Message,
                };
                (reply, outcome, failure) = (new ClaudeReply(message, true), "error", message);
            }
        }
        finally
        {
            _running.Release();
        }

        try
        {
            string Scrub(string text) => config is null ? text : SecretScrubber.Scrub(text, config, protector);
            reply = reply with { Text = Scrub(reply.Text) };
            log.Append(new ClaudeActivity(DateTimeOffset.Now, Scrub(name), call.Connection, call.Detail is { } d ? Scrub(d) : null,
                outcome, failure is null ? null : Scrub(failure)));
        }
        catch (Exception)
        {
            return new ClaudeReply("dbExplorer could not write its activity log, so nothing is returned.", true);
        }
        return reply;
    }

    private AppConfig Load()
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return loadConfig(); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(50); }   // being replaced by the app
            catch (Exception) { throw new ClaudeRefusedException("The dbExplorer configuration cannot be read."); }
        }
    }

    private Task<string> DispatchAsync(AppConfig config, ClaudeToolDefinition tool, ClaudeArguments args, Call call, CancellationToken ct) =>
        tool.Name switch
        {
            ClaudeToolCatalog.ListConnections => Task.FromResult(ListConnections(config)),
            ClaudeToolCatalog.ListObjects => ListObjectsAsync(config, args, call, ct),
            ClaudeToolCatalog.DescribeObject => DescribeAsync(config, args, call, ct),
            ClaudeToolCatalog.GetDefinition => DefinitionAsync(config, args, call, ct),
            ClaudeToolCatalog.Query => RunAsync(config, args, call, PlanMode.None, ct),
            ClaudeToolCatalog.Explain => RunAsync(config, args, call, PlanMode.Estimated, ct),
            ClaudeToolCatalog.OpenScript => OpenScriptAsync(config, args, call, ct),
            ClaudeToolCatalog.ActiveTab => ActiveTabAsync(config, call, ct),
            _ => throw new ClaudeRefusedException("Unknown tool."),
        };

    private static (ConnectionConfig Connection, ClaudeGrant Grant) Resolve(AppConfig config, ClaudeArguments args, Call call)
    {
        var wanted = args.Required("connection", 200);
        call.Connection = wanted;
        var allowed = ClaudePolicy.Allowed(config).ToList();
        var matches = Guid.TryParse(wanted, out var id)
            ? allowed.Where(a => a.Connection.Id == id).ToList()
            : allowed.Where(a => a.Connection.Name.Equals(wanted, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
            matches = allowed.Where(a => a.Connection.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        // The same answer for a connection that does not exist and one that is excluded.
        if (matches.Count == 0)
            throw new ClaudeRefusedException(
                "No allowed connection has this name or id (it may not be allowed, or be a production one). See list_connections.");
        if (matches.Count > 1) throw new ClaudeRefusedException("Several connections have this name: use the id from list_connections.");
        var connection = matches[0].Connection;
        call.Connection = connection.Name;
        return (connection, ClaudePolicy.For(config, connection));
    }

    private static string ListConnections(AppConfig config)
    {
        var list = new JsonArray();
        foreach (var (c, access) in ClaudePolicy.Allowed(config))
        {
            var item = new JsonObject
            {
                ["id"] = c.Id.ToString(),
                ["name"] = c.Name,
                ["engine"] = c.Kind.ToString(),
                ["environment"] = c.Environment.Label(),
                ["database"] = c.Kind == DbKind.Sqlite ? Path.GetFileName(c.Database) : c.Database,
                ["access"] = access == ClaudeAccess.SchemaAndData ? "schema_and_data" : "schema",
            };
            if (ClaudePolicy.CannotConnectUnattended(c) is { } why) item["unavailable"] = why;
            list.Add((JsonNode)item);
        }
        var result = new JsonObject { ["connections"] = list };
        if (list.Count == 0)
            result["note"] = "No connection is allowed. The user chooses them in dbExplorer, Settings, Claude Code.";
        return ClaudeText.Json(result);
    }

    private async Task<string> ListObjectsAsync(AppConfig config, ClaudeArguments args, Call call, CancellationToken ct)
    {
        var (connection, _) = Resolve(config, args, call);
        var type = args.Optional("type") ?? "all";
        if (type is not ("all" or "table" or "view" or "procedure" or "function"))
            throw new ClaudeRefusedException("The type is all, table, view, procedure or function.");
        var schema = args.Optional("schema");
        var search = args.Optional("search");
        var offset = args.Number("offset", 0, 0, int.MaxValue);
        var limit = args.Number("limit", 200, 1, 500);
        call.Detail = $"type={type} schema={schema} search={search}";

        var objects = new List<(TableRef Ref, string Type, string? Arguments)>();
        if (type is "all" or "table" or "view")
        {
            var tables = await Bounded(t => database.SchemaAsync(connection, t), MetadataTimeout, ct).ConfigureAwait(false);
            objects.AddRange(tables.Select(t => (t.Ref, t.IsView ? "view" : "table", (string?)null)));
        }
        if (type is "all" or "procedure" or "function")
        {
            var routines = await Bounded(t => database.RoutinesAsync(connection, t), MetadataTimeout, ct).ConfigureAwait(false);
            objects.AddRange(routines.Select(r => (r.Ref, r.Kind == RoutineKind.Procedure ? "procedure" : "function", r.Arguments)));
        }

        var found = objects
            .Where(o => type == "all" || o.Type == type)
            .Where(o => schema is null || string.Equals(o.Ref.Schema, schema, StringComparison.OrdinalIgnoreCase))
            .Where(o => search is null || o.Ref.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var list = new JsonArray();
        var size = 0;
        foreach (var o in found.Skip(offset).Take(limit))
        {
            var item = new JsonObject { ["name"] = o.Ref.Name, ["type"] = o.Type };
            if (o.Ref.Schema is { } s) item["schema"] = s;
            if (o.Arguments is { } a) item["arguments"] = a;
            size += ClaudeText.Json(item).Length + 1;
            if (size > ClaudeText.MaxOutput) break;
            list.Add((JsonNode)item);
        }
        return ClaudeText.Json(new JsonObject
        {
            ["total"] = found.Count,
            ["offset"] = offset,
            ["returned"] = list.Count,
            ["objects"] = list,
        });
    }

    // Names are only ever compared with what the database listed: nothing from the call reaches a statement.
    private static T Find<T>(IReadOnlyList<T> all, Func<T, TableRef> reference, string? schema, string name) where T : class
    {
        List<T> Matching(StringComparison how) => all
            .Where(o => reference(o).Name.Equals(name, how) && (schema is null || string.Equals(reference(o).Schema, schema, how)))
            .ToList();

        var matches = Matching(StringComparison.Ordinal);
        if (matches.Count == 0) matches = Matching(StringComparison.OrdinalIgnoreCase);
        if (matches.Count == 1) return matches[0];
        if (matches.Count == 0) throw new ClaudeRefusedException($"No such object: {name}. See list_objects.");
        throw new ClaudeRefusedException("Several objects match, give the schema or arguments: "
            + string.Join(", ", matches.Take(20).Select(m => reference(m) + (m is RoutineInfo { Arguments: { } a } ? $"({a})" : ""))));
    }

    private async Task<string> DescribeAsync(AppConfig config, ClaudeArguments args, Call call, CancellationToken ct)
    {
        var (connection, _) = Resolve(config, args, call);
        var name = args.Required("name");
        var schema = args.Optional("schema");
        call.Detail = schema is null ? name : $"{schema}.{name}";

        var tables = await Bounded(t => database.SchemaAsync(connection, t), MetadataTimeout, ct).ConfigureAwait(false);
        var table = Find(tables, t => t.Ref, schema, name);
        static JsonArray Names(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

        return ClaudeText.Cut(ClaudeText.Json(new JsonObject
        {
            ["schema"] = table.Ref.Schema,
            ["name"] = table.Ref.Name,
            ["type"] = table.IsView ? "view" : "table",
            ["columns"] = new JsonArray(table.Columns.Select(c => (JsonNode?)new JsonObject
            {
                ["name"] = c.Name, ["type"] = c.DataType, ["nullable"] = c.Nullable, ["primary_key"] = c.IsPrimaryKey,
            }).ToArray()),
            ["foreign_keys"] = new JsonArray(table.ForeignKeys.Select(f => (JsonNode?)new JsonObject
            {
                ["name"] = f.Name, ["columns"] = Names(f.Columns), ["references"] = f.RefTable.ToString(), ["referenced_columns"] = Names(f.RefColumns),
            }).ToArray()),
            ["indexes"] = new JsonArray(table.Indexes.Select(i => (JsonNode?)new JsonObject
            {
                ["name"] = i.Name, ["unique"] = i.Unique, ["columns"] = Names(i.Columns),
            }).ToArray()),
            ["referenced_by"] = Names(tables.Where(t => t.ForeignKeys.Any(f => f.RefTable.Name == table.Ref.Name
                    && (f.RefTable.Schema ?? t.Ref.Schema) == table.Ref.Schema))
                .Select(t => t.Ref.ToString())),
        }));
    }

    private async Task<string> DefinitionAsync(AppConfig config, ClaudeArguments args, Call call, CancellationToken ct)
    {
        var (connection, _) = Resolve(config, args, call);
        var name = args.Required("name");
        var schema = args.Optional("schema");
        var signature = args.Optional("arguments", 4_000);
        var offset = args.Number("offset", 0, 0, int.MaxValue);
        call.Detail = schema is null ? name : $"{schema}.{name}";

        var text = await Bounded(async t =>
        {
            var tables = await database.SchemaAsync(connection, t).ConfigureAwait(false);
            var views = tables.Where(v => v.IsView).ToList();
            var routines = (await database.RoutinesAsync(connection, t).ConfigureAwait(false))
                .Where(r => signature is null || r.Arguments == signature).ToList();
            var candidates = views.Select(v => new Definable(v.Ref, null))
                .Concat(routines.Select(r => new Definable(r.Ref, r))).ToList();
            if (signature is not null) candidates.RemoveAll(c => c.Routine is null);
            if (!candidates.Any(c => c.Ref.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                && tables.Any(x => !x.IsView && x.Ref.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ClaudeRefusedException($"{name} is a table: use describe_object.");
            var found = FindDefinable(candidates, schema, name);
            return found.Routine is { } routine
                ? await database.RoutineDefinitionAsync(connection, routine, t).ConfigureAwait(false)
                : await database.ViewDefinitionAsync(connection, found.Ref, t).ConfigureAwait(false);
        }, MetadataTimeout, ct).ConfigureAwait(false);

        // Scrubbed before it is cut, so no part of a secret survives at an edge.
        text = SecretScrubber.Scrub(text, config, protector);
        if (offset >= text.Length && text.Length > 0) throw new ClaudeRefusedException($"The definition has {text.Length} characters.");
        var length = Math.Min(text.Length - offset, ClaudeText.MaxOutput);
        var part = text.Substring(offset, length);
        return offset + length < text.Length ? $"{part}\n… ({text.Length} characters in all; continue with offset {offset + length})" : part;
    }

    private sealed record Definable(TableRef Ref, RoutineInfo? Routine);

    private static Definable FindDefinable(IReadOnlyList<Definable> all, string? schema, string name)
    {
        List<Definable> Matching(StringComparison how) => all
            .Where(o => o.Ref.Name.Equals(name, how) && (schema is null || string.Equals(o.Ref.Schema, schema, how))).ToList();

        var matches = Matching(StringComparison.Ordinal);
        if (matches.Count == 0) matches = Matching(StringComparison.OrdinalIgnoreCase);
        if (matches.Count == 1) return matches[0];
        if (matches.Count == 0) throw new ClaudeRefusedException($"No view or routine named {name}. See list_objects.");
        throw new ClaudeRefusedException("Several objects match, give the schema or arguments: "
            + string.Join(", ", matches.Take(20).Select(m => m.Ref + (m.Routine?.Arguments is { } a ? $"({a})" : ""))));
    }

    private async Task<string> RunAsync(AppConfig config, ClaudeArguments args, Call call, PlanMode plan, CancellationToken ct)
    {
        var (connection, grant) = Resolve(config, args, call);
        var sql = args.Required("sql", ClaudeSqlPolicy.MaxLength);
        call.Detail = sql;
        if (plan == PlanMode.None && !grant.CanReadData)
            throw new ClaudeRefusedException("This connection is schema only: reading data was not allowed by the user.");
        // These two engines may run parts of a statement while they plan it, and show values in the plan or its errors.
        if (plan != PlanMode.None && !grant.CanReadData && connection.Kind is DbKind.Postgres or DbKind.MySql)
            throw new ClaudeRefusedException($"A {connection.Kind} plan needs the data access, which the user did not allow on this connection.");
        if (ClaudeSqlPolicy.Refuse(sql, connection.Kind) is { } why) throw new ClaudeRefusedException(why);
        var closed = config.Connections
            .Where(c => c.Kind == connection.Kind && !ClaudePolicy.For(config, c).CanReadData
                        && !string.IsNullOrEmpty(c.Database) && !c.Database.Equals(connection.Database, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Database).ToList();
        if (ClaudeSqlPolicy.RefuseOtherDatabase(sql, connection.Kind, closed) is { } elsewhere) throw new ClaudeRefusedException(elsewhere);

        var maxRows = plan == PlanMode.None ? args.Number("max_rows", 100, 1, 1000) : 1000;
        var timeout = TimeSpan.FromSeconds(args.Number("timeout_seconds", DefaultTimeoutSeconds, 1, 120));
        var result = await Bounded(t => database.RunAsync(connection, sql, maxRows, plan, timeout, t), timeout, ct).ConfigureAwait(false);
        if (result.Error is { } error) throw new InvalidOperationException(error);

        if (plan == PlanMode.None) return ClaudeText.Rows(result.Sets.Count > 0 ? result.Sets[0] : null);
        var (_, parsed) = PlanCapture.Extract(result, connection.Kind, plan);
        return parsed is null ? throw new InvalidOperationException("The server sent no readable plan.") : ClaudeText.Plan(parsed);
    }

    private async Task<string> OpenScriptAsync(AppConfig config, ClaudeArguments args, Call call, CancellationToken ct)
    {
        var (connection, _) = Resolve(config, args, call);
        var sql = args.Required("sql", 100_000);
        var title = args.Optional("title", 60);
        call.Detail = sql;
        var reply = await app.SendAsync(new ClaudeAppRequest(ClaudeAppRequest.OpenScript, connection.Id, title, sql), ct).ConfigureAwait(false);
        return reply.Ok
            ? "The SQL is open in a new dbExplorer tab. It was not executed; the user decides whether to run it."
            : throw new ClaudeRefusedException(reply.Message ?? "dbExplorer refused.");
    }

    private async Task<string> ActiveTabAsync(AppConfig config, Call call, CancellationToken ct)
    {
        var reply = await app.SendAsync(new ClaudeAppRequest(ClaudeAppRequest.ActiveTab), ct).ConfigureAwait(false);
        if (!reply.Ok || reply.Tab is not { } tab) throw new ClaudeRefusedException(reply.Message ?? "There is no script tab to read.");

        // Checked here too, against the stored settings.
        var connection = config.Connections.FirstOrDefault(c => c.Id == tab.ConnectionId);
        var grant = connection is null ? default : ClaudePolicy.For(config, connection);
        if (!grant.Allowed) throw new ClaudeRefusedException("The connection of the active tab is not allowed for Claude Code.");
        call.Connection = connection!.Name;

        var result = new JsonObject
        {
            ["connection"] = connection.Name,
            ["title"] = tab.Title,
            ["sql"] = ClaudeText.Cut(SecretScrubber.Scrub(tab.Sql, config, protector)),
        };
        if (grant.CanReadData && tab.LastError is { } error) result["last_error"] = ClaudeText.Cut(error);
        if (tab.Note is { } note) result["note"] = note;
        return ClaudeText.Json(result);
    }

    private static async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> work, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        var task = Task.Run(() => work(linked.Token), linked.Token);
        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return await task.WaitAsync(timeout + CancelGrace, ct).ConfigureAwait(false);
    }
}
