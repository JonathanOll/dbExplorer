using DbExplorer.Core.Upgrade;

namespace DbExplorer.Core.Tests.Upgrade;

public class ScriptAnalyzerTests
{
    private static IReadOnlyList<ScriptWarning> A(string sql) => ScriptAnalyzer.Analyze(sql);

    private static void NoWarning(string sql) => Assert.Empty(A(sql));

    private static ScriptWarning Single(string sql, ScriptWarningKind kind)
    {
        var w = Assert.Single(A(sql));
        Assert.Equal(kind, w.Kind);
        return w;
    }

    // ---- real shapes ----

    [Fact]
    public void GuardedCreateIndex_IsFlagged_WithNameAndLine_CrlfSafe()
    {
        var sql = "-- adj - 003\r\nIF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Invoices_PublicId')\r\nBEGIN\r\n"
                  + "    CREATE NONCLUSTERED INDEX IX_Invoices_PublicId ON [dbo].[Invoices] ([PublicId])\r\nEND\r\n";
        var w = Single(sql, ScriptWarningKind.Index);
        Assert.Equal("Creates index IX_Invoices_PublicId", w.Reason);
        Assert.Equal(4, w.Line);
    }

    [Fact]
    public void DropThenTwoCreates_GivesExactlyTwoIndexWarnings()
    {
        var sql = "DROP INDEX IF EXISTS [IX_a] ON [dbo].[T];\nCREATE NONCLUSTERED INDEX [IX_a] ON [dbo].[T] ([c]);\n"
                  + "DROP INDEX IF EXISTS [IX_b] ON [dbo].[T];\nCREATE NONCLUSTERED INDEX [IX_b] ON [dbo].[T] ([d]);\n";
        var ws = A(sql);
        Assert.Equal(2, ws.Count);
        Assert.All(ws, w => Assert.Equal(ScriptWarningKind.Index, w.Kind));
        Assert.Equal(new[] { 2, 4 }, ws.Select(w => w.Line).ToArray());
        Assert.Equal("Creates index IX_a", ws[0].Reason);
        Assert.Equal("Creates index IX_b", ws[1].Reason);
    }

    [Theory]
    [InlineData("CREATE UNIQUE CLUSTERED INDEX IX_u ON t(a)", "Creates index IX_u")]
    [InlineData("CREATE COLUMNSTORE INDEX IX_c ON t(a)", "Creates index IX_c")]
    [InlineData("create nonclustered columnstore index [IX c] on t(a)", "Creates index IX c")]
    [InlineData("CREATE XML INDEX IX_x ON t(a)", "Creates index IX_x")]
    [InlineData("CREATE INDEX ON t(a)", "Creates an index")]
    public void CreateIndexVariants(string sql, string reason)
    {
        Assert.Equal(reason, Single(sql, ScriptWarningKind.Index).Reason);
    }

    [Theory]
    [InlineData("CREATE VIEW dbo.v AS SELECT 1 AS a")]
    [InlineData("CREATE TABLE IndexLog (id int)")]
    [InlineData("CREATE TABLE dbo.T (id int, name nvarchar(10))\nGO\nALTER TABLE t ADD c int")]
    [InlineData("ALTER TABLE t ADD c int")]
    [InlineData("DROP INDEX IX_a ON t")]
    [InlineData("SELECT * FROM sys.indexes WHERE name = 'x'")]
    public void PlainSchemaScripts_HaveNoWarning(string sql) => NoWarning(sql);

    // ---- ignored text ----

    [Theory]
    [InlineData("-- CREATE INDEX x ON t(a)")]
    [InlineData("/* ALTER INDEX a ON t REBUILD */")]
    [InlineData("PRINT 'CREATE INDEX x ON t(a)'")]
    [InlineData("SELECT [CREATE INDEX]")]
    [InlineData("PRINT 'UPDATE t SET a = 1 WHILE'")]
    public void CommentsStringsAndBrackets_AreIgnored(string sql) => NoWarning(sql);

    [Theory]
    [InlineData("SELECT 'unterminated")]
    [InlineData("/* unterminated CREATE INDEX x")]
    [InlineData("SELECT [unterminated")]
    [InlineData("CREATE")]
    [InlineData("ALTER INDEX")]
    [InlineData("UPDATE")]
    [InlineData("DELETE FROM")]
    [InlineData("ALTER TABLE")]
    public void Malformed_NeverThrows(string sql)
    {
        var ex = Record.Exception(() => A(sql));
        Assert.Null(ex);
    }

    // ---- index extras ----

    [Fact]
    public void AlterIndexAllRebuild_IsIndex()
    {
        var w = Single("ALTER INDEX ALL ON t REBUILD", ScriptWarningKind.Index);
        Assert.Equal("Rebuilds index ALL", w.Reason);
    }

    [Fact]
    public void AlterIndexRebuildWithOptions_IsIndex()
    {
        var w = Single("ALTER INDEX IX_a ON t REBUILD WITH (ONLINE = ON)", ScriptWarningKind.Index);
        Assert.Equal("Rebuilds index IX_a", w.Reason);
    }

    [Theory]
    [InlineData("ALTER INDEX IX_a ON t DISABLE")]
    [InlineData("ALTER INDEX IX_a ON t REORGANIZE")]
    [InlineData("ALTER INDEX IX_a ON t DISABLE; SELECT 1 AS REBUILD")]
    [InlineData("ALTER INDEX IX_a ON t DISABLE\nALTER INDEX IX_b ON t DISABLE\nALTER TABLE t ADD REBUILD int")]
    public void AlterIndexWithoutRebuild_HasNoWarning(string sql) => NoWarning(sql);

    // ---- unbounded DML flagged ----

    [Theory]
    [InlineData("UPDATE t SET a = 1", "UPDATE without WHERE: touches every row")]
    [InlineData("DELETE FROM t", "DELETE without WHERE: touches every row")]
    [InlineData("DELETE t", "DELETE without WHERE: touches every row")]
    [InlineData("IF EXISTS (SELECT 1 FROM x) UPDATE t SET a = 1", "UPDATE without WHERE: touches every row")]
    [InlineData("UPDATE t SET a = (SELECT 1 FROM x WHERE x.id = 1)", "UPDATE without WHERE: touches every row")]
    [InlineData("UPDATE t SET a = 1 FROM t INNER JOIN u ON u.id = t.id", "UPDATE without WHERE: touches every row")]
    [InlineData("UPDATE t SET a = 1;\nSELECT 1 WHERE 1 = 1", "UPDATE without WHERE: touches every row")]
    [InlineData("UPDATE t SET a = 1\nGO\nPRINT 'x' WHERE 1 = 1", "UPDATE without WHERE: touches every row")]
    public void UnboundedDml_IsFlagged(string sql, string reason)
    {
        var w = Single(sql, ScriptWarningKind.LongRunning);
        Assert.Equal(reason, w.Reason);
        Assert.Equal(1, w.Line);
    }

    [Fact]
    public void TwoUpdatesWithoutSemicolon_OnlyTheUnboundedOneIsFlagged()
    {
        var w = Single("UPDATE a SET x = 1\nUPDATE b SET y = 2 WHERE z = 1", ScriptWarningKind.LongRunning);
        Assert.Equal(1, w.Line);
    }

    [Fact]
    public void CaseEndAndNextStatementWhere_DoNotRescueTheUpdate()
    {
        var w = Single("UPDATE t SET a = CASE WHEN b = 1 THEN 2 ELSE 3 END\nSELECT 1 WHERE 1 = 1", ScriptWarningKind.LongRunning);
        Assert.Equal("UPDATE without WHERE: touches every row", w.Reason);
    }

    [Fact]
    public void CaseElse_DoesNotEndTheStatementEarly()
    {
        NoWarning("UPDATE t SET a = CASE WHEN b = 1 THEN 2 ELSE 3 END WHERE c = 1");
    }

    [Fact]
    public void UpdateInsideBeginEnd_EndClosesTheStatement()
    {
        var sql = "IF 1 = 1 BEGIN\n  UPDATE t SET a = 1\nEND\nSELECT 1 WHERE 1 = 1";
        var w = Single(sql, ScriptWarningKind.LongRunning);
        Assert.Equal(2, w.Line);
    }

    [Fact]
    public void UpdateWithWhereInsideBeginEnd_IsNotFlagged()
    {
        NoWarning("IF 1 = 1 BEGIN\n  UPDATE t SET a = CASE WHEN b = 1 THEN 2 END WHERE id = 3\nEND");
    }

    // ---- DML not flagged ----

    [Theory]
    [InlineData("UPDATE t SET a = 1 WHERE id = 1")]
    [InlineData("DELETE FROM t WHERE id = 1")]
    [InlineData("UPDATE TOP (1000) t SET a = 1")]
    [InlineData("DELETE TOP (1000) FROM t")]
    [InlineData("UPDATE #tmp SET a = 1")]
    [InlineData("DELETE FROM @rows")]
    [InlineData("DELETE FROM #tmp")]
    [InlineData("CREATE TRIGGER trg ON t AFTER INSERT, UPDATE, DELETE AS BEGIN SELECT 1 END")]
    [InlineData("CREATE TRIGGER trg ON t FOR UPDATE AS SELECT 1")]
    [InlineData("CREATE TRIGGER trg ON t INSTEAD OF DELETE AS SELECT 1")]
    [InlineData("ALTER TABLE a ADD CONSTRAINT fk FOREIGN KEY (b) REFERENCES c(id) ON DELETE CASCADE")]
    [InlineData("ALTER TABLE a ADD CONSTRAINT fk FOREIGN KEY (b) REFERENCES c(id) ON UPDATE NO ACTION")]
    [InlineData("GRANT DELETE ON t TO r")]
    [InlineData("GRANT SELECT, UPDATE, DELETE ON t TO r")]
    [InlineData("DENY DELETE ON t TO r")]
    [InlineData("UPDATE STATISTICS t")]
    [InlineData("IF UPDATE(col) PRINT 'x'")]
    [InlineData("IF UPDATE (col) PRINT 'x'")]
    [InlineData("MERGE t USING s ON s.id = t.id WHEN MATCHED THEN UPDATE SET a = 1;")]
    [InlineData("MERGE t USING s ON s.id = t.id WHEN NOT MATCHED BY SOURCE THEN DELETE;")]
    [InlineData("DELETE FROM t OUTPUT deleted.* INTO #x WHERE id = 1")]
    public void BoundedOrNonDml_IsNotFlagged(string sql) => NoWarning(sql);

    // ---- other long-running ----

    [Fact]
    public void WhileLoop_IsFlagged()
    {
        var w = Single("DECLARE @i int = 0\nWHILE @i < 10\nBEGIN SET @i += 1 END", ScriptWarningKind.LongRunning);
        Assert.Equal("WHILE loop", w.Reason);
        Assert.Equal(2, w.Line);
    }

    [Fact]
    public void WaitFor_IsFlagged()
    {
        var w = Single("WAITFOR DELAY '00:00:05'", ScriptWarningKind.LongRunning);
        Assert.Equal("WAITFOR delays the script", w.Reason);
    }

    [Fact]
    public void AlterColumn_IsFlagged()
    {
        var w = Single("ALTER TABLE [dbo].[T] ALTER COLUMN c nvarchar(max)", ScriptWarningKind.LongRunning);
        Assert.Equal("ALTER COLUMN can rewrite the whole table", w.Reason);
    }

    [Fact]
    public void AlterTableAdd_IsNotFlagged() => NoWarning("ALTER TABLE t ADD c int");

    [Fact]
    public void AlterColumnInALaterStatement_IsNotAttributedToAnEarlierAlterTable()
    {
        NoWarning("ALTER TABLE t ADD c int;\nALTER TABLE t2 DROP COLUMN d");
    }

    // ---- large script ----

    [Fact]
    public void LargeScript_FlaggedAtThreshold_NotBelow()
    {
        const string pad = "-- x\n";
        string Make(int len) => new string('x', 0) + string.Concat(Enumerable.Repeat(pad, len / pad.Length)).PadRight(len, ' ');

        Assert.Empty(A(Make(ScriptAnalyzer.LargeScriptChars - 1)));

        var text = Make(ScriptAnalyzer.LargeScriptChars);
        Assert.Equal(ScriptAnalyzer.LargeScriptChars, text.Length);
        var w = Assert.Single(A(text));
        Assert.Equal(ScriptWarningKind.LongRunning, w.Kind);
        Assert.Equal(1, w.Line);
        Assert.StartsWith("Large script (", w.Reason);
        Assert.EndsWith(" KB)", w.Reason);
    }

    // ---- ordering and empties ----

    [Fact]
    public void SeveralWarnings_AreOrderedByLine_LargeScriptFirst()
    {
        var sql = "WAITFOR DELAY '00:00:01'\nCREATE INDEX IX_a ON t(a)\nUPDATE t SET a = 1\n" + new string(' ', ScriptAnalyzer.LargeScriptChars);
        var ws = A(sql);
        Assert.Equal(4, ws.Count);
        Assert.Equal(new[] { 1, 1, 2, 3 }, ws.Select(w => w.Line).ToArray());
        Assert.StartsWith("Large script", ws[0].Reason);
        Assert.Equal(ScriptWarningKind.Index, ws[2].Kind);
        Assert.Equal(ScriptWarningKind.LongRunning, ws[3].Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void EmptyOrWhitespace_ReturnsEmpty(string sql) => NoWarning(sql);

    // ---- dynamic SQL ----

    [Theory]
    [InlineData("EXEC('CREATE INDEX IX_a ON t(a)')", ScriptWarningKind.Index, "Creates index IX_a")]
    [InlineData("EXECUTE ('CREATE NONCLUSTERED INDEX IX_a ON t(a)')", ScriptWarningKind.Index, "Creates index IX_a")]
    [InlineData("EXEC(N'ALTER INDEX IX_a ON t REBUILD')", ScriptWarningKind.Index, "Rebuilds index IX_a")]
    [InlineData("EXEC sp_executesql N'ALTER INDEX ALL ON t REBUILD'", ScriptWarningKind.Index, "Rebuilds index ALL")]
    [InlineData("EXECUTE sp_executesql @stmt = N'CREATE INDEX IX_a ON t(a)', @params = N''", ScriptWarningKind.Index, "Creates index IX_a")]
    [InlineData("EXEC sp_executesql N'UPDATE t SET a = 1'", ScriptWarningKind.LongRunning, "UPDATE without WHERE: touches every row")]
    [InlineData("EXEC('DELETE FROM t')", ScriptWarningKind.LongRunning, "DELETE without WHERE: touches every row")]
    [InlineData("EXEC('UPDATE t SET a = ''x''')", ScriptWarningKind.LongRunning, "UPDATE without WHERE: touches every row")]
    [InlineData("EXEC('WAITFOR DELAY ''00:00:05''')", ScriptWarningKind.LongRunning, "WAITFOR delays the script")]
    [InlineData("EXEC('ALTER TABLE t ALTER COLUMN c nvarchar(max)')", ScriptWarningKind.LongRunning, "ALTER COLUMN can rewrite the whole table")]
    [InlineData("EXEC('EXEC(''CREATE INDEX IX_n ON t(a)'')')", ScriptWarningKind.Index, "Creates index IX_n")]
    [InlineData("DECLARE @s nvarchar(max) = N'x'\nEXEC('UPDATE t SET a = 1 ' + @s)", ScriptWarningKind.LongRunning, "UPDATE without WHERE: touches every row")]
    public void DynamicSql_IsScannedWithTheSameRules(string sql, ScriptWarningKind kind, string reason)
    {
        var w = Single(sql, kind);
        Assert.Equal(reason, w.Reason);
    }

    [Fact]
    public void DynamicSql_LineIsTheLineInsideTheString()
    {
        var w = Single("SELECT 1\nEXEC('\nCREATE INDEX IX_a ON t(a)')", ScriptWarningKind.Index);
        Assert.Equal(3, w.Line);
    }

    [Theory]
    [InlineData("EXEC('SELECT 1')")]
    [InlineData("EXEC sp_executesql N'SELECT 1'")]
    [InlineData("EXEC('UPDATE t SET a = 1 WHERE id = 1')")]
    [InlineData("EXEC('UPDATE t SET a = 1 ' + 'WHERE id = 1')")]
    [InlineData("EXEC sp_who2")]
    [InlineData("EXEC my_proc 'CREATE INDEX x ON t(a)'")]
    [InlineData("EXEC my_proc @sql = N'UPDATE t SET a = 1'")]
    [InlineData("PRINT 'EXEC(''CREATE INDEX x ON t(a)'')'")]
    [InlineData("-- EXEC('CREATE INDEX x ON t(a)')")]
    [InlineData("EXEC('PRINT ''CREATE INDEX x ON t(a)''')")]
    public void DynamicSql_FalsePositives_StayQuiet(string sql) => NoWarning(sql);

    // ---- ALTER TABLE ... ADD PRIMARY KEY / UNIQUE, XML indexes ----

    [Theory]
    [InlineData("ALTER TABLE t ADD CONSTRAINT pk_t PRIMARY KEY CLUSTERED (id)")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT pk_t PRIMARY KEY NONCLUSTERED (id)")]
    [InlineData("ALTER TABLE t ADD PRIMARY KEY (id)")]
    [InlineData("alter table [dbo].[t] add constraint [uq_t] unique (a, b)")]
    [InlineData("ALTER TABLE t ADD UNIQUE NONCLUSTERED (a)")]
    [InlineData("ALTER TABLE t WITH CHECK ADD CONSTRAINT uq UNIQUE CLUSTERED (a)")]
    [InlineData("ALTER TABLE t\nADD CONSTRAINT pk_t\n  PRIMARY KEY (id)")]
    [InlineData("ALTER TABLE srv.db.dbo.t ADD CONSTRAINT pk_t PRIMARY KEY (id)")]
    [InlineData("ALTER TABLE t ADD c int NOT NULL, CONSTRAINT pk_t PRIMARY KEY (c)")]
    public void AlterTableAddKeyConstraint_IsIndex(string sql)
    {
        var w = Single(sql, ScriptWarningKind.Index);
        Assert.Equal(1, w.Line);
        Assert.Contains("index", w.Reason);
    }

    [Theory]
    [InlineData("CREATE PRIMARY XML INDEX PX_t ON t(x)", "Creates index PX_t")]
    [InlineData("CREATE SELECTIVE XML INDEX SX_t ON t(x)", "Creates index SX_t")]
    [InlineData("CREATE XML INDEX X_t ON t(x) USING XML INDEX PX_t FOR PATH", "Creates index X_t")]
    public void XmlIndexes_AreIndex(string sql, string reason) => Assert.Equal(reason, Single(sql, ScriptWarningKind.Index).Reason);

    [Theory]
    [InlineData("ALTER TABLE a ADD CONSTRAINT fk FOREIGN KEY (b) REFERENCES c(id)")]
    [InlineData("ALTER TABLE t DROP CONSTRAINT pk_t")]
    [InlineData("ALTER TABLE t DROP CONSTRAINT IF EXISTS uq_t")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT df DEFAULT 0 FOR c")]
    [InlineData("ALTER TABLE t NOCHECK CONSTRAINT ALL")]
    [InlineData("ALTER TABLE t ADD c int\nSELECT 1 AS [PRIMARY KEY]")]
    [InlineData("ALTER TABLE t ADD c int\nCREATE TABLE z (id int PRIMARY KEY, n int UNIQUE)")]
    [InlineData("ALTER TABLE t ADD c int;\nPRINT 'PRIMARY KEY'")]
    [InlineData("ALTER TABLE t DROP COLUMN c\nDROP TABLE z\nGO\nSELECT 'UNIQUE'")]
    public void AlterTableWithoutKeyConstraint_HasNoWarning(string sql) => NoWarning(sql);

    // ---- ALTER COLUMN on long names ----

    [Theory]
    [InlineData("ALTER TABLE srv.db.dbo.t ALTER COLUMN c int NOT NULL")]
    [InlineData("ALTER TABLE [srv].[db].[dbo].[t] ALTER COLUMN [c] nvarchar(max)")]
    [InlineData("ALTER TABLE [a very].[long name with ] bracket].[and].[a long name too] ALTER COLUMN c int")]
    [InlineData("ALTER TABLE t\nALTER COLUMN c int")]
    public void AlterColumn_IsFlagged_WhateverTheNameLooksLike(string sql)
    {
        var w = Single(sql, ScriptWarningKind.LongRunning);
        Assert.Equal("ALTER COLUMN can rewrite the whole table", w.Reason);
        Assert.Equal(1, w.Line);
    }

    // ---- SELECT INTO / INSERT ... SELECT ----

    [Theory]
    [InlineData("SELECT * INTO dbo.Copy FROM dbo.T")]
    [InlineData("SELECT a, b INTO archive.T2 FROM t JOIN u ON u.id = t.id")]
    [InlineData("select (SELECT 1) AS x INTO dbo.C FROM t")]
    [InlineData("INSERT INTO dbo.T (a) SELECT a FROM dbo.S")]
    [InlineData("INSERT dbo.T SELECT * FROM S")]
    [InlineData("INSERT INTO T (a)\nSELECT a FROM S")]
    [InlineData("INSERT INTO T WITH (TABLOCK) (a) SELECT a FROM S")]
    [InlineData("INSERT INTO [dbo].[T] ([a]) SELECT [a] FROM [dbo].[S]")]
    [InlineData("EXEC('SELECT * INTO dbo.c FROM t')")]
    public void SelectIntoAndInsertSelect_AreLongRunning(string sql)
    {
        var w = Single(sql, ScriptWarningKind.LongRunning);
        Assert.Equal(1, w.Line);
    }

    [Theory]
    [InlineData("SELECT * INTO #t FROM x")]
    [InlineData("SELECT * INTO ##g FROM x")]
    [InlineData("SELECT * INTO [#t] FROM x")]
    [InlineData("INSERT INTO #t SELECT * FROM x")]
    [InlineData("INSERT INTO ##g (a) SELECT a FROM x")]
    [InlineData("INSERT INTO [#t] SELECT * FROM x")]
    [InlineData("INSERT INTO @t (a) SELECT a FROM x")]
    [InlineData("INSERT @t SELECT a FROM x")]
    [InlineData("INSERT INTO t VALUES (1)\nSELECT * FROM t")]
    [InlineData("INSERT INTO t (a) VALUES (1);\nSELECT * FROM t")]
    [InlineData("INSERT INTO t DEFAULT VALUES\nSELECT * FROM t")]
    [InlineData("INSERT INTO t (a) VALUES ((SELECT 1))")]
    [InlineData("INSERT INTO t EXEC some_proc")]
    [InlineData("SELECT * FROM t")]
    [InlineData("SELECT a INTO_x FROM t")]
    [InlineData("DECLARE @a int\nFETCH NEXT FROM c INTO @a")]
    [InlineData("SELECT @a = x FROM t")]
    [InlineData("GRANT INSERT ON t TO r")]
    [InlineData("MERGE t USING s ON s.id = t.id WHEN NOT MATCHED THEN INSERT (a) VALUES (1);")]
    [InlineData("CREATE TRIGGER trg ON t AFTER INSERT AS SELECT 1 FROM inserted")]
    [InlineData("INSERT INTO t (a) VALUES (1) -- SELECT * FROM x")]
    [InlineData("PRINT 'INSERT INTO t SELECT * FROM x'")]
    public void SelectIntoAndInsertSelect_FalsePositives_StayQuiet(string sql) => NoWarning(sql);

    // ---- unbounded DML: bounded by trigger pseudo-tables, TOP in a derived table, or a trigger body ----

    [Theory]
    [InlineData("CREATE TRIGGER trg ON Invoices AFTER UPDATE AS BEGIN\n UPDATE inv SET a = 1 FROM Invoices inv JOIN inserted i ON i.Id = inv.Id\nEND")]
    [InlineData("UPDATE inv SET a = 1 FROM Invoices inv JOIN inserted i ON i.Id = inv.Id")]
    [InlineData("UPDATE inv SET a = 1 FROM Invoices inv INNER JOIN deleted AS d ON d.Id = inv.Id")]
    [InlineData("DELETE x FROM X x JOIN deleted d ON d.Id = x.Id")]
    [InlineData("update t set a = 1 from t, INSERTED i")]
    [InlineData("UPDATE t SET a = 1 FROM inserted")]
    [InlineData("delete t from (select top 1000 id from t order by id) x join t on t.id = x.id")]
    [InlineData("DELETE t FROM (SELECT TOP (1000) * FROM t ORDER BY Id) t")]
    public void DmlBoundedByPseudoTableOrDerivedTop_IsNotFlagged(string sql) => NoWarning(sql);

    // A #temp table or @table variable in the FROM / JOIN is the working set of the statement (the keys to touch).
    [Theory]
    [InlineData("DELETE line FROM #Lines line JOIN #Removed r ON r.ID = line.ID;")]
    [InlineData("delete line\nfrom #Lines line\njoin #RemovedLines removed on removed.ID = line.ID;")]
    [InlineData("UPDATE r SET a = 0 FROM [Roles] r INNER JOIN @ids t ON t.ID = r.ID")]
    [InlineData("UPDATE iv SET a = 1 FROM dbo.Invoices iv JOIN @batch b ON b.Id = iv.Id;")]
    [InlineData("UPDATE l SET Deleted = getdate() FROM Expenses l JOIN #R removed ON removed.ID = l.ID")]
    [InlineData("UPDATE l SET a = 1 FROM #src s JOIN Expenses l ON l.ID = s.ID")]
    [InlineData("UPDATE l SET a = 1 FROM Expenses l JOIN ##g ON ##g.ID = l.ID")]
    public void DmlJoinedToATempTableOrTableVariable_IsNotFlagged(string sql) => NoWarning(sql);

    [Theory]
    [InlineData("CREATE TRIGGER trg ON t AFTER INSERT AS BEGIN\n UPDATE other SET a = 1\n DELETE FROM log\nEND")]
    [InlineData("CREATE TRIGGER dbo.trg ON t AFTER INSERT AS UPDATE other SET a = 1")]
    [InlineData("CREATE OR ALTER TRIGGER trg ON t AFTER INSERT AS UPDATE other SET a = 1")]
    [InlineData("ALTER TRIGGER trg ON t AFTER INSERT AS UPDATE other SET a = 1")]
    [InlineData("SELECT 1\nGO\nCREATE TRIGGER trg ON t AFTER INSERT AS\nBEGIN\n UPDATE other SET a = 1\nEND\nGO")]
    public void DmlInsideTriggerBody_IsNotFlagged(string sql) => NoWarning(sql);

    [Fact]
    public void TriggerBodyEndsAtGo_TheNextBatchIsScannedAgain()
    {
        var w = Single("CREATE TRIGGER trg ON t AFTER INSERT AS UPDATE other SET a = 1 WHERE id = 1\nGO\nUPDATE t SET a = 1", ScriptWarningKind.LongRunning);
        Assert.Equal(3, w.Line);
        Assert.Empty(A("CREATE TRIGGER trg ON t AFTER INSERT AS UPDATE other SET a = 1\nGO\nUPDATE t SET a = 1 WHERE id = 1"));
    }

    [Theory]
    [InlineData("DISABLE TRIGGER trg ON t;\nUPDATE t SET a = 1")]
    [InlineData("ALTER TABLE t DISABLE TRIGGER ALL;\nUPDATE t SET a = 1")]
    [InlineData("DROP TRIGGER trg;\nUPDATE t SET a = 1")]
    [InlineData("UPDATE t SET a = (SELECT TOP 1 b FROM u)")]
    [InlineData("UPDATE t SET a = 1 FROM t JOIN (SELECT id FROM u WHERE x = 1) q ON q.id = t.id")]
    [InlineData("UPDATE t SET a = 1 OUTPUT deleted.a, inserted.a INTO #x")]
    [InlineData("DELETE t OUTPUT deleted.* INTO #x FROM t JOIN u ON u.id = t.id")]
    [InlineData("UPDATE t SET a = 1 FROM t JOIN dbo.inserted i ON i.id = t.id")]
    [InlineData("UPDATE t SET a = 1 FROM t JOIN other ON other.id = t.id")]
    [InlineData("UPDATE t SET a = 1 FROM t LEFT JOIN #x ON #x.id = t.id")]
    [InlineData("UPDATE t SET a = 1 FROM t LEFT OUTER JOIN @x x ON x.id = t.id")]
    [InlineData("UPDATE t SET a = 1 FROM t CROSS JOIN #x")]
    [InlineData("UPDATE t SET a = 1 FROM t JOIN other o ON o.k = @k")]
    public void DmlStillUnbounded_IsFlagged(string sql)
    {
        var ws = A(sql);
        Assert.NotEmpty(ws);
        Assert.Contains(ws, w => w.Kind == ScriptWarningKind.LongRunning && w.Reason.Contains("without WHERE"));
    }

    [Fact]
    public void UnboundedUpdateInsideExec_KeepsItsLine_NextToAnIndexInExec()
    {
        var sql = "IF 1 = 1\nBEGIN\n  EXEC sp_rename 'a.b', 'c', 'COLUMN';\n\n  EXEC(N'CREATE INDEX [IX_a]\n         ON [dbo].[A] ([c]);');\n\n"
                  + "  EXEC(N'UPDATE [dbo].[A]\n         SET [E] =\n           CASE [E]\n             WHEN ''None'' THEN ''0''\n           END;');\nEND";
        var ws = A(sql);
        Assert.Equal(2, ws.Count);
        Assert.Equal(ScriptWarningKind.Index, ws[0].Kind);
        Assert.Equal(5, ws[0].Line);
        Assert.Equal(ScriptWarningKind.LongRunning, ws[1].Kind);
        Assert.Equal("UPDATE without WHERE: touches every row", ws[1].Reason);
        Assert.Equal(8, ws[1].Line);
    }

    [Fact]
    public void ExecUpdateJoiningInserted_IsBounded() =>
        NoWarning("EXEC('UPDATE t SET a = 1 FROM t JOIN inserted i ON i.id = t.id')");

    // ---- full-text indexes ----

    [Theory]
    [InlineData("CREATE FULLTEXT INDEX ON dbo.T (Name) KEY INDEX PK_T ON ftCat", "Creates a full-text index")]
    [InlineData("create fulltext index on [dbo].[T] ([Name]) key index PK_T", "Creates a full-text index")]
    [InlineData("ALTER FULLTEXT INDEX ON [dbo].[Suppliers] ADD ([Name])", "Alters a full-text index")]
    [InlineData("ALTER FULLTEXT INDEX ON [dbo].[Suppliers] DROP ([Name]);", "Alters a full-text index")]
    [InlineData("ALTER FULLTEXT INDEX ON t START FULL POPULATION", "Alters a full-text index")]
    [InlineData("DROP FULLTEXT INDEX ON dbo.T", "Drops a full-text index")]
    [InlineData("IF @x = 1\n    ALTER FULLTEXT INDEX ON [dbo].[Suppliers] DROP ([Name]);", "Alters a full-text index")]
    [InlineData("EXEC('DROP FULLTEXT INDEX ON t')", "Drops a full-text index")]
    public void FullTextIndex_IsIndex(string sql, string reason) => Assert.Equal(reason, Single(sql, ScriptWarningKind.Index).Reason);

    [Theory]
    [InlineData("CREATE FULLTEXT CATALOG ftc")]
    [InlineData("DROP FULLTEXT CATALOG ftc")]
    [InlineData("SELECT * FROM sys.fulltext_index_columns")]
    [InlineData("PRINT 'CREATE FULLTEXT INDEX ON t'")]
    [InlineData("-- DROP FULLTEXT INDEX ON t")]
    public void FullTextNonIndex_HasNoWarning(string sql) => NoWarning(sql);

    // ---- INSERT ... SELECT / SELECT INTO bounded or not ----

    [Theory]
    [InlineData("INSERT INTO T (a) SELECT a FROM S WHERE ID = @x")]
    [InlineData("INSERT INTO T (a)\nSELECT a FROM S WHERE a > 1")]
    [InlineData("INSERT INTO T (a) SELECT a FROM S JOIN U ON U.id = S.id WHERE U.k = 1 ORDER BY a")]
    [InlineData("INSERT INTO T SELECT TOP 10 a FROM S")]
    [InlineData("INSERT INTO T SELECT TOP (10) PERCENT a FROM S")]
    [InlineData("INSERT INTO T (a) SELECT 1 UNION ALL SELECT 2")]
    [InlineData("INSERT INTO T (a) SELECT 'x'")]
    [InlineData("INSERT INTO T (a) SELECT (SELECT MAX(id) FROM S)")]
    [InlineData("INSERT INTO T (a) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM T)")]
    [InlineData("INSERT INTO T (a) SELECT a FROM S WHERE a = 1 UNION ALL SELECT a FROM U WHERE a = 2")]
    [InlineData("INSERT INTO T (a) SELECT a FROM S WHERE a = 1 UNION SELECT TOP 5 a FROM U")]
    [InlineData("INSERT INTO T (a) SELECT CASE WHEN x = 1 THEN 1 ELSE 2 END FROM S WHERE id = 3")]
    [InlineData("INSERT INTO T (a) SELECT a FROM inserted")]
    [InlineData("INSERT INTO T (a) SELECT a FROM deleted d")]
    [InlineData("SELECT a, b INTO archive.T2 FROM t WHERE x = 1")]
    [InlineData("SELECT TOP 10 a INTO [dbo].[Copy] FROM t")]
    [InlineData("SELECT * INTO dbo.Copy FROM t WHERE id = @x")]
    [InlineData("SELECT 1 AS a INTO dbo.Copy")]
    [InlineData("EXEC('INSERT INTO T (a) SELECT a FROM S WHERE id = 1')")]
    public void BoundedInsertSelectAndSelectInto_AreNotFlagged(string sql) => NoWarning(sql);

    [Theory]
    [InlineData("INSERT INTO T (a) SELECT a FROM S")]
    [InlineData("INSERT INTO T (a) SELECT a, (SELECT 1 FROM x WHERE x.id = 1) FROM S")]
    [InlineData("INSERT INTO T (a) SELECT a FROM S JOIN (SELECT id FROM x WHERE y = 1) q ON q.id = S.id")]
    [InlineData("INSERT INTO T (a) SELECT a FROM S WHERE a = 1 UNION ALL SELECT a FROM U")]
    [InlineData("INSERT INTO T (a) SELECT a FROM S UNION SELECT a FROM U WHERE a = 1")]
    [InlineData("INSERT INTO T (a) SELECT CASE WHEN x = 1 THEN 1 ELSE 2 END FROM S")]
    [InlineData("INSERT INTO T (a) SELECT a FROM S\nSELECT 1 WHERE 1 = 1")]
    [InlineData("INSERT INTO T (a) SELECT a FROM S;\nUPDATE t SET a = 1 WHERE id = 1")]
    [InlineData("IF 1 = 1 BEGIN INSERT INTO T (a) SELECT a FROM S END\nSELECT 1 WHERE 1 = 1")]
    public void UnboundedInsertSelect_IsFlagged(string sql)
    {
        var w = Single(sql, ScriptWarningKind.LongRunning);
        Assert.Equal("INSERT ... SELECT without WHERE can write many rows", w.Reason);
        Assert.Equal(1, w.Line);
    }

    [Theory]
    [InlineData("SELECT * INTO dbo.Copy FROM dbo.T")]
    [InlineData("SELECT a INTO dbo.Copy FROM T JOIN (SELECT TOP 5 id FROM U) q ON q.id = T.id")]
    [InlineData("EXEC('SELECT * INTO dbo.c FROM t')")]
    public void UnboundedSelectInto_IsFlagged(string sql)
    {
        var w = Single(sql, ScriptWarningKind.LongRunning);
        Assert.Equal("SELECT INTO without WHERE creates and fills a table", w.Reason);
        Assert.Equal(1, w.Line);
    }

    // ---- USE ----

    [Theory]
    [InlineData("USE other", 1)]
    [InlineData("use [other db];", 1)]
    [InlineData("SELECT 1\nGO\nUSE master", 3)]
    [InlineData("SELECT 1\n  USE other", 2)]
    [InlineData("BEGIN\n USE other\nEND", 2)]
    [InlineData("IF 1 = 1\n USE other", 2)]
    [InlineData("SELECT 1;\nUSE \"other\"", 2)]
    [InlineData("PRINT 'x'\nUSE other", 2)]
    [InlineData("EXEC('USE other; DELETE FROM t WHERE id = 1')", 1)]
    [InlineData("SELECT 1\nEXEC sp_executesql N'USE other'", 2)]
    [InlineData("SELECT 1\nEXEC('\nUSE other')", 3)]
    public void FindUse_FlagsTheStatement(string sql, int line)
    {
        Assert.Equal(line, ScriptAnalyzer.FindUse(sql));
        Assert.Equal($"USE <db> would redirect the run to a database that was not confirmed (line {line}).", ScriptAnalyzer.UseMessage(line));
    }

    [Theory]
    [InlineData("SELECT use FROM t")]
    [InlineData("SELECT a AS use FROM t")]
    [InlineData("SELECT [USE] FROM t")]
    [InlineData("SELECT t.use FROM t")]
    [InlineData("UPDATE t SET use = 1 WHERE id = 1")]
    [InlineData("INSERT INTO t (use, x) VALUES (1, 2)")]
    [InlineData("SELECT a,\n use\nFROM t")]
    [InlineData("SELECT a,\n use, b\nFROM t")]
    [InlineData("SELECT\n use\nFROM t")]
    [InlineData("SELECT 1 AS\nuse")]
    [InlineData("SELECT * FROM t WHERE\n use = 1")]
    [InlineData("SELECT * FROM t ORDER BY\n use")]
    [InlineData("SELECT x\n use\nFROM t")]
    [InlineData("PRINT 'USE other'")]
    [InlineData("-- USE other")]
    [InlineData("/* USE other */")]
    [InlineData("SELECT 'a\nUSE other\nb'")]
    [InlineData("EXEC('SELECT use FROM t')")]
    [InlineData("EXEC my_proc 'USE other'")]
    [InlineData("USE")]
    [InlineData("SELECT 1\nUSE")]
    public void FindUse_IgnoresIdentifiersStringsAndComments(string sql)
    {
        Assert.Null(ScriptAnalyzer.FindUse(sql));
        Assert.DoesNotContain(A(sql), w => w.Reason.Contains("USE"));
    }

    [Fact]
    public void Use_IsNeverAWarning_OnlyALoadError()
    {
        Assert.Empty(A("USE other"));
        Assert.Empty(A("SELECT 1\nGO\nUSE master\nGO"));
    }
}
