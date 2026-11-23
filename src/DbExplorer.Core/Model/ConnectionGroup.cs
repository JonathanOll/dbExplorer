namespace DbExplorer.Core.Model;

/// <summary>A named set of connections of the same <see cref="DbKind"/>, used for read/compare group runs.</summary>
public sealed record ConnectionGroup(Guid Id, string Name, List<Guid> ConnectionIds);
