using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Boo.Core;
using Microsoft.Data.Sqlite;

namespace Boo.Storage;

public sealed class SessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions CheckJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _databasePath;

    public SessionStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("A database path is required.", nameof(databasePath));

        _databasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using var connection = OpenConnection();
        EnsureSchema(connection);
    }

    public void Save(FocusSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var connection = OpenConnection();
        EnsureSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO FocusSessions
                (Id, Task, Duration, StartedAt, Elapsed, RealElapsed, Status,
                 DeveloperMode, UsedDeveloperMode, ChecksJson)
            VALUES
                ($id, $task, $duration, $startedAt, $elapsed, $realElapsed, $status,
                 $developerMode, $usedDeveloperMode, $checks)
            ON CONFLICT(Id) DO UPDATE SET
                Task = excluded.Task,
                Duration = excluded.Duration,
                StartedAt = excluded.StartedAt,
                Elapsed = excluded.Elapsed,
                RealElapsed = excluded.RealElapsed,
                Status = excluded.Status,
                DeveloperMode = excluded.DeveloperMode,
                UsedDeveloperMode = excluded.UsedDeveloperMode,
                ChecksJson = excluded.ChecksJson;
            """;
        command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        command.Parameters.AddWithValue("$task", session.Task);
        command.Parameters.AddWithValue("$duration", session.Duration);
        command.Parameters.AddWithValue("$startedAt", session.StartedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$elapsed", session.Elapsed);
        command.Parameters.AddWithValue("$realElapsed", session.RealElapsed);
        command.Parameters.AddWithValue("$status", session.Status.ToString());
        command.Parameters.AddWithValue("$developerMode", session.DeveloperMode ? 1 : 0);
        command.Parameters.AddWithValue("$usedDeveloperMode", session.UsedDeveloperMode ? 1 : 0);
        command.Parameters.AddWithValue("$checks", JsonSerializer.Serialize(session.Checks, CheckJsonOptions));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<FocusSnapshot> List()
    {
        using var connection = OpenConnection();
        EnsureSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Task, Duration, StartedAt, Elapsed, RealElapsed, Status,
                   DeveloperMode, UsedDeveloperMode, ChecksJson
            FROM FocusSessions;
            """;
        using var reader = command.ExecuteReader();
        var sessions = new List<FocusSnapshot>();
        while (reader.Read())
            sessions.Add(ReadSnapshot(reader));

        return sessions
            .OrderByDescending(session => session.StartedAt)
            .ThenByDescending(session => session.Id)
            .ToArray();
    }

    /// <summary>
    /// Settles sessions left Running by a crash or kill as Paused (progress kept). Call once at startup,
    /// before any new session starts. Returns how many sessions were recovered.
    /// </summary>
    public int RecoverInterrupted()
    {
        var recovered = 0;
        foreach (var session in List())
        {
            if (session.Status != FocusStatus.Running)
                continue;
            Save(FocusRecovery.RecoverInterrupted(session));
            recovered++;
        }
        return recovered;
    }

    public void Delete(Guid id)
    {
        using var connection = OpenConnection();
        EnsureSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM FocusSessions WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > 1)
            throw new InvalidDataException("Focus history uses a newer schema. Update Boo before opening this history.");

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS FocusSessions (
                Id TEXT NOT NULL PRIMARY KEY,
                Task TEXT NOT NULL,
                Duration REAL NOT NULL,
                StartedAt TEXT NOT NULL,
                Elapsed REAL NOT NULL,
                RealElapsed REAL NOT NULL,
                Status TEXT NOT NULL,
                DeveloperMode INTEGER NOT NULL,
                UsedDeveloperMode INTEGER NOT NULL,
                ChecksJson TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        if (version == 0)
        {
            using var setVersionCommand = connection.CreateCommand();
            setVersionCommand.CommandText = "PRAGMA user_version = 1;";
            setVersionCommand.ExecuteNonQuery();
        }
    }

    private static FocusSnapshot ReadSnapshot(SqliteDataReader reader)
    {
        try
        {
            var id = Guid.Parse(reader.GetString(0));
            var task = reader.GetString(1);
            var duration = reader.GetDouble(2);
            var startedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var elapsed = reader.GetDouble(4);
            var realElapsed = reader.GetDouble(5);
            if (!Enum.TryParse<FocusStatus>(reader.GetString(6), out var status))
                throw new FormatException();
            var developerMode = reader.GetInt64(7) != 0;
            var usedDeveloperMode = reader.GetInt64(8) != 0;
            var checks = JsonSerializer.Deserialize<FocusCheck[]>(reader.GetString(9), CheckJsonOptions)
                         ?? Array.Empty<FocusCheck>();
            return new FocusSnapshot(id, task, duration, startedAt, elapsed, realElapsed,
                status, developerMode, usedDeveloperMode, checks);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or InvalidCastException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("Focus history is corrupt or unreadable. Delete the history database and try again.");
        }
    }
}

