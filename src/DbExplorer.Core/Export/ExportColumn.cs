namespace DbExplorer.Core.Export;

/// <summary>A column of exported rows; the type information is optional (a script result has only names).</summary>
public sealed record ExportColumn(string Name, string? TypeName = null, Type? ClrType = null);

/// <summary>The export cannot be written as asked; the message is meant for the user.</summary>
public sealed class ExportException(string message) : Exception(message);
