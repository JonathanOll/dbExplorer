using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

/// <summary>A connection whose commands report a fixed rows-affected count; records what was executed and the outcome of the transaction.</summary>
internal sealed class FakeDbConnection(int affected, Func<DbParameter>? parameterFactory = null) : DbConnection
{
    private ConnectionState _state = ConnectionState.Open;

    private readonly List<FakeDbCommand> _commands = [];

    /// <summary>A snapshot of the commands created so far: a pooled-thread ROLLBACK may still be adding while a test reads.</summary>
    public List<FakeDbCommand> Commands { get { lock (_commands) return [.. _commands]; } }
    public bool Committed { get; set; }
    public bool RolledBack { get; set; }
    public int Affected { get; } = affected;
    public Func<DbParameter> ParameterFactory { get; } = parameterFactory ?? (() => new SqliteParameter());

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get; set; } = "";
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "0";
    public override ConnectionState State => _state;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() => _state = ConnectionState.Closed;
    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();   // like the real providers: disposing closes
        base.Dispose(disposing);
    }
    public override void Open() => _state = ConnectionState.Open;
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeDbTransaction(this);
    protected override DbCommand CreateDbCommand()
    {
        var cmd = new FakeDbCommand(this);
        lock (_commands) _commands.Add(cmd);
        return cmd;
    }
}

internal sealed class FakeDbTransaction(FakeDbConnection connection) : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
    protected override DbConnection DbConnection => connection;
    public override void Commit() => connection.Committed = true;
    public override void Rollback() => connection.RolledBack = true;
}

internal sealed class FakeDbCommand(FakeDbConnection connection) : DbCommand
{
    private readonly FakeParameterCollection _parameters = new();

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; } = connection;
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }
    public override void Cancel() { }
    public override int ExecuteNonQuery() => connection.Affected;
    public override object? ExecuteScalar() => null;
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => connection.ParameterFactory();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
}

internal sealed class FakeParameterCollection : DbParameterCollection
{
    public List<DbParameter> Items { get; } = [];
    public override int Count => Items.Count;
    public override object SyncRoot => Items;
    public override int Add(object value) { Items.Add((DbParameter)value); return Items.Count - 1; }
    public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
    public override void Clear() => Items.Clear();
    public override bool Contains(object value) => Items.Contains((DbParameter)value);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)Items).CopyTo(array, index);
    public override System.Collections.IEnumerator GetEnumerator() => Items.GetEnumerator();
    public override int IndexOf(object value) => Items.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) => Items.FindIndex(p => p.ParameterName == parameterName);
    public override void Insert(int index, object value) => Items.Insert(index, (DbParameter)value);
    public override void Remove(object value) => Items.Remove((DbParameter)value);
    public override void RemoveAt(int index) => Items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => Items.RemoveAt(IndexOf(parameterName));
    protected override DbParameter GetParameter(int index) => Items[index];
    protected override DbParameter GetParameter(string parameterName) => Items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => Items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value) => Items[IndexOf(parameterName)] = value;
}
