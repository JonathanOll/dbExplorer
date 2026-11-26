namespace DbExplorer.Core.Model;

public enum RoutineKind { Procedure, Function }

/// <summary>A stored procedure or function.</summary>
/// <param name="Arguments">The argument list that tells overloads apart (PostgreSQL); null where names are unique.</param>
/// <param name="Id">The engine's identifier of exactly this overload (PostgreSQL oid); 0 where the name is enough.</param>
public record RoutineInfo(TableRef Ref, RoutineKind Kind, string? Arguments = null, long Id = 0);

/// <summary>The database gives no source text for an object (encrypted, no permission, dropped); the message says which.</summary>
public sealed class DefinitionUnavailableException(string message) : Exception(message);
