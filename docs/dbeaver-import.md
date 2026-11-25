# Importing from DBeaver

Click **Import** in the navigator toolbar. It reads `%APPDATA%\DBeaverData\workspace6\General\.dbeaver\data-sources.json`
and `credentials-config.json` (or the `--dbeaver-dir` folder): folders, connections, credentials (re-stored DPAPI-encrypted)
and virtual keys. It is a one-time, re-runnable action: connections whose name already exists are skipped, and the live
config is replaced only when the whole import succeeded. Warnings (unsupported drivers, missing credentials) are listed
in the status bar. Only user/password authentication is imported.

Connections that DBeaver marks `read-only`, or whose connection type is `prod` / `production` (or a custom type with
"prod" in its name or one that forbids data edits), are imported **read-only**, on purpose: an imported production database
never silently becomes editable. The status bar names them after the import; untick **Read-only** in the connection's
context menu to edit one. Virtual keys that use "all columns" skip the columns SQL Server / PostgreSQL cannot compare
with `=` (text, ntext, image, xml, json...).
