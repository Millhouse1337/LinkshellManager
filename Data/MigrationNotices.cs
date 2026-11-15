using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LinkshellManagerDiscordApp.Data;

// Sends Postgres notices raised while migrating (RAISE NOTICE / RAISE WARNING in a migration's SQL)
// to the app log. Npgsql logs them at Debug only, and the app logs at Information, so before this a
// guarded migration that skipped an index (and said so with RAISE WARNING) left no trace.
//
// WARNING and ERROR notices become Warning log lines; every other notice becomes Information.
// The service log then shows lines like:
//   Postgres WARNING during migration: <the message>
// deploy/RUNBOOK.md says to look for them after every deploy.
public static class MigrationNotices
{
    // Runs `work` (the adopt step and Migrate in Program.cs) while listening for notices on the
    // context's connection. Npgsql raises Notice on the NpgsqlConnection object, and EF keeps one
    // connection object per context, so the listener hears every command EF runs on it, even when EF
    // opens and closes it between steps. The connection is NOT opened here: before Migrate the
    // database may not exist yet (Migrate creates it), and opening it would fail.
    public static async Task<T> RunWithNoticesLoggedAsync<T>(DbContext db, ILogger log, Func<Task<T>> work)
    {
        if (db.Database.GetDbConnection() is not NpgsqlConnection connection)
        {
            // Not Postgres (never the case in the app): nothing to listen to.
            return await work();
        }

        void OnNotice(object? sender, NpgsqlNoticeEventArgs e)
        {
            var severity = e.Notice.InvariantSeverity;
            var level = severity is "WARNING" or "ERROR" ? LogLevel.Warning : LogLevel.Information;
            log.Log(level, "Postgres {Severity} during migration: {Message}", severity, e.Notice.MessageText);
        }

        connection.Notice += OnNotice;
        try
        {
            return await work();
        }
        finally
        {
            connection.Notice -= OnNotice;
        }
    }
}
