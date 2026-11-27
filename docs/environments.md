# Environments and writes on production

Each connection can say what it is used for: **None**, **Development**, **Test**, **Pre-production** or **Production**.
Set it in the connection dialog, or for all the connections of a folder at once (folder menu, **Set environment of its
connections…**; a connection moved into the folder later keeps its own).

The environment is shown as a badge with a word (`DEV`, `TEST`, `PRE-PROD`, `PROD`), tinted green, blue, amber and red:
in the navigator, on tabs, in the script toolbar, the status bar, quick open, the command palette and the connection
pickers. Script and table tabs of a production connection also carry a banner.

## Confirmation before a write

On a **Production** connection, a confirmation names the connection, server and database and summarises what will run.
**Cancel** has the focus; `Enter` and `Esc` both cancel.

| Write path | When it asks |
| --- | --- |
| Script run (`Ctrl+Enter`, `F5`, "Raise limit", from the history or the palette) | when any statement is not provably a read |
| Actual plan | same rule: it runs the statements |
| Estimated plan | never: nothing runs (except a SQL Server script that mentions `SHOWPLAN`) |
| Table tab, Apply after the SQL preview | always |
| Database upgrade, run and retry | always, after the upgrade's own confirmation step |
| Run on group | never asks: it is read-only and refuses writes |

Plain reads, table browsing, search, diagrams and export never ask. A read-only connection does not ask either: it
refuses writes.

Scripts and grid edits offer **Do not ask again in this tab for 5 minutes**. It applies to that tab and that connection
only, and editing the connection cancels it. The upgrade always asks.

## What counts as a write

The [read-only guard](limitations.md) decides, and unknown means write: `SET`, `USE`, `DECLARE`, `BEGIN` or a procedure
call all ask. On production it also asks for `nextval`, `setval`, `set_config` and the large-object functions. It reads
the text only: a `SELECT` that calls **your own function with side effects is not detected**. Use a read-only connection
or restricted rights where that matters.

## Database upgrade scripts named "Production"

A script file ending in `- Production2` ([database upgrade](database-upgrade.md)) targets specific servers: it starts
unticked. That is a property of the script. Whether a database **is** production is the environment of its connection.

## DBeaver import

The connection type sets the environment: `dev`, `test`, `prod`, or a custom type whose name says so (`staging` and
`pre-prod` give Pre-production). DBeaver's default type is `dev`.
