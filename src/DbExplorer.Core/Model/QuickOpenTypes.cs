namespace DbExplorer.Core.Model;

/// <summary>The kinds of object the quick open searches. Stored as the set of kinds switched off, so a new kind starts on.</summary>
[Flags]
public enum QuickOpenTypes
{
    None = 0,
    Tables = 1,
    Views = 2,
    Procedures = 4,
    Functions = 8,
    Columns = 16,
}
