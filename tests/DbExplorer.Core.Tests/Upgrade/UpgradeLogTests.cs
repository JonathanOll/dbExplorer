using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Upgrade;

namespace DbExplorer.Core.Tests.Upgrade;

public class UpgradeLogTests
{
    private static ConnectionConfig Conn(string name) =>
        new(Guid.NewGuid(), name, DbKind.SqlServer, null, "h", null, "d", null, null, false, false, false);

    private static ScriptOutcome O(string name, ScriptOutcomeState state, string? error = null) =>
        new(name, state, 0, TimeSpan.Zero, error);

    [Fact]
    public void Format_DoneFailedAndSkipped_ExactText()
    {
        var results = new List<UpgradeResult>
        {
            new(Conn("db1"), MemberState.Done,
                [O("v1/a.sql", ScriptOutcomeState.Done), O("v1/b.sql", ScriptOutcomeState.Done)], TimeSpan.FromSeconds(1.234), null),
            new(Conn("db2"), MemberState.Failed,
                [O("v1/a.sql", ScriptOutcomeState.Done), O("v1/b.sql", ScriptOutcomeState.Failed, "batch 2 of 3: Invalid object name 'x'."), O("v1/c.sql", ScriptOutcomeState.Skipped)],
                TimeSpan.FromSeconds(12), "v1/b.sql: batch 2 of 3: Invalid object name 'x'."),
            new(Conn("db3"), MemberState.Cancelled,
                [O("v1/a.sql", ScriptOutcomeState.Cancelled), O("v1/b.sql", ScriptOutcomeState.Skipped)], TimeSpan.Zero, null),
        };

        var text = UpgradeLog.Format("Sprint 143 on group prod", results);

        Assert.Equal(
            "Sprint 143 on group prod\n" +
            "== db1: Done (2 of 2 scripts, 1.2 s)\n" +
            "  [OK] v1/a.sql\n" +
            "  [OK] v1/b.sql\n" +
            "== db2: Failed (1 of 3 scripts, 12.0 s)\n" +
            "  [OK] v1/a.sql\n" +
            "  [FAILED] v1/b.sql : batch 2 of 3: Invalid object name 'x'.\n" +
            "  [SKIPPED] v1/c.sql\n" +
            "== db3: Cancelled (0 of 2 scripts, 0.0 s)\n" +
            "  [CANCELLED] v1/a.sql\n" +
            "  [SKIPPED] v1/b.sql",
            text);
    }

    [Fact]
    public void Format_ADatabaseThatFailedBeforeAnyScript_KeepsItsReason()
    {
        var results = new List<UpgradeResult>
        {
            new(Conn("db1"), MemberState.Failed, [O("a.sql", ScriptOutcomeState.Skipped)], TimeSpan.FromSeconds(0.04), "Login failed."),
        };

        Assert.Equal(
            "L\n== db1: Failed (0 of 1 scripts, 0.0 s)\n  [SKIPPED] a.sql\n  ERROR: Login failed.",
            UpgradeLog.Format("L", results));
    }

    [Fact]
    public void Format_NoResult_IsTheLabelOnly()
    {
        Assert.Equal("L", UpgradeLog.Format("L", []));
    }
}
