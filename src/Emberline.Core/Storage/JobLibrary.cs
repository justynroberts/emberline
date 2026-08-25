using System.Globalization;
using Microsoft.Data.Sqlite;
using Emberline.Core.Jobs;

namespace Emberline.Core.Storage;

/// <summary>
/// The job library: what was burned, on what, with which settings, and whether it
/// worked.
///
/// SQLite rather than a folder of JSON because the useful question is "what did I
/// use on 3 mm walnut last month", and answering that by deserialising nine
/// hundred files is not a design. Everything stays local — one file the user can
/// copy, back up or delete.
/// </summary>
public sealed class JobLibrary : IDisposable
{
    private readonly SqliteConnection _connection;

    public JobLibrary(string? databasePath = null)
    {
        var path = databasePath ?? AppPaths.DatabaseFile;

        // ":memory:" has no directory, and neither does a bare filename.
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
        Migrate();
    }

    /// <summary>An in-memory library, for tests.</summary>
    public static JobLibrary InMemory() => new(":memory:");

    private void Migrate()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS jobs (
                id             TEXT PRIMARY KEY,
                name           TEXT NOT NULL,
                started_at     TEXT NOT NULL,
                finished_at    TEXT,
                outcome        TEXT NOT NULL,
                machine_name   TEXT,
                material_name  TEXT,
                speed_mm_min   REAL NOT NULL DEFAULT 0,
                power_percent  REAL NOT NULL DEFAULT 0,
                passes         INTEGER NOT NULL DEFAULT 1,
                total_lines    INTEGER NOT NULL DEFAULT 0,
                lines_done     INTEGER NOT NULL DEFAULT 0,
                width_mm       REAL NOT NULL DEFAULT 0,
                height_mm      REAL NOT NULL DEFAULT 0,
                source_files   TEXT,
                thumbnail_path TEXT,
                gcode_path     TEXT,
                failure_reason TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_jobs_started ON jobs (started_at DESC);
            CREATE INDEX IF NOT EXISTS idx_jobs_material ON jobs (material_name);
            """;
        command.ExecuteNonQuery();
    }

    public void Record(JobRecord record)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (id, name, started_at, finished_at, outcome, machine_name, material_name,
                              speed_mm_min, power_percent, passes, total_lines, lines_done,
                              width_mm, height_mm, source_files, thumbnail_path, gcode_path, failure_reason)
            VALUES ($id, $name, $started, $finished, $outcome, $machine, $material,
                    $speed, $power, $passes, $total, $done,
                    $width, $height, $sources, $thumb, $gcode, $failure)
            ON CONFLICT(id) DO UPDATE SET
                finished_at = excluded.finished_at,
                outcome = excluded.outcome,
                lines_done = excluded.lines_done,
                failure_reason = excluded.failure_reason;
            """;

        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$name", record.Name);
        command.Parameters.AddWithValue("$started", record.StartedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$finished", (object?)record.FinishedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", record.Outcome.ToString());
        command.Parameters.AddWithValue("$machine", (object?)record.MachineName ?? DBNull.Value);
        command.Parameters.AddWithValue("$material", (object?)record.MaterialName ?? DBNull.Value);
        command.Parameters.AddWithValue("$speed", record.SpeedMmMin);
        command.Parameters.AddWithValue("$power", record.PowerPercent);
        command.Parameters.AddWithValue("$passes", record.Passes);
        command.Parameters.AddWithValue("$total", record.TotalLines);
        command.Parameters.AddWithValue("$done", record.LinesCompleted);
        command.Parameters.AddWithValue("$width", record.WidthMm);
        command.Parameters.AddWithValue("$height", record.HeightMm);
        command.Parameters.AddWithValue("$sources", string.Join('\n', record.SourceFiles));
        command.Parameters.AddWithValue("$thumb", (object?)record.ThumbnailPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$gcode", (object?)record.GcodePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$failure", (object?)record.FailureReason ?? DBNull.Value);

        command.ExecuteNonQuery();
    }

    public IReadOnlyList<JobRecord> Recent(int limit = 50) =>
        Query("SELECT * FROM jobs ORDER BY started_at DESC LIMIT $limit", c => c.Parameters.AddWithValue("$limit", limit));

    public IReadOnlyList<JobRecord> ForMaterial(string material, int limit = 50) =>
        Query("SELECT * FROM jobs WHERE material_name = $material ORDER BY started_at DESC LIMIT $limit", c =>
        {
            c.Parameters.AddWithValue("$material", material);
            c.Parameters.AddWithValue("$limit", limit);
        });

    public IReadOnlyList<JobRecord> Search(string term, int limit = 50) =>
        Query("""
            SELECT * FROM jobs
            WHERE name LIKE $term OR material_name LIKE $term OR machine_name LIKE $term
            ORDER BY started_at DESC LIMIT $limit
            """, c =>
        {
            c.Parameters.AddWithValue("$term", $"%{term}%");
            c.Parameters.AddWithValue("$limit", limit);
        });

    public JobRecord? Find(string id) =>
        Query("SELECT * FROM jobs WHERE id = $id", c => c.Parameters.AddWithValue("$id", id)).FirstOrDefault();

    public bool Delete(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM jobs WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public int Count()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM jobs";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The settings most recently used successfully on a material. This is what
    /// makes the library worth keeping: it is a record of what actually worked on
    /// this machine, which beats any built-in table.
    /// </summary>
    public JobRecord? LastSuccessfulFor(string material) =>
        Query("""
            SELECT * FROM jobs
            WHERE material_name = $material AND outcome = 'Completed'
            ORDER BY started_at DESC LIMIT 1
            """, c => c.Parameters.AddWithValue("$material", material)).FirstOrDefault();

    private List<JobRecord> Query(string sql, Action<SqliteCommand>? bind = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);

        var results = new List<JobRecord>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            results.Add(new JobRecord
            {
                Id = reader.GetString(reader.GetOrdinal("id")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                StartedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at")), CultureInfo.InvariantCulture),
                FinishedAt = ReadNullableDate(reader, "finished_at"),
                Outcome = Enum.TryParse<JobState>(reader.GetString(reader.GetOrdinal("outcome")), out var state) ? state : JobState.Failed,
                MachineName = ReadNullableString(reader, "machine_name"),
                MaterialName = ReadNullableString(reader, "material_name"),
                SpeedMmMin = reader.GetDouble(reader.GetOrdinal("speed_mm_min")),
                PowerPercent = reader.GetDouble(reader.GetOrdinal("power_percent")),
                Passes = reader.GetInt32(reader.GetOrdinal("passes")),
                TotalLines = reader.GetInt32(reader.GetOrdinal("total_lines")),
                LinesCompleted = reader.GetInt32(reader.GetOrdinal("lines_done")),
                WidthMm = reader.GetDouble(reader.GetOrdinal("width_mm")),
                HeightMm = reader.GetDouble(reader.GetOrdinal("height_mm")),
                SourceFiles = ReadNullableString(reader, "source_files")?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [],
                ThumbnailPath = ReadNullableString(reader, "thumbnail_path"),
                GcodePath = ReadNullableString(reader, "gcode_path"),
                FailureReason = ReadNullableString(reader, "failure_reason"),
            });
        }

        return results;
    }

    private static string? ReadNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader reader, string column)
    {
        var text = ReadNullableString(reader, column);
        return text is null ? null : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
    }

    /// <summary>Archive the G-code so a job can be reproduced byte for byte later.</summary>
    public static string ArchiveGcode(string jobId, IReadOnlyList<string> lines)
    {
        Directory.CreateDirectory(AppPaths.Jobs);
        var path = Path.Combine(AppPaths.Jobs, $"{jobId}.nc");
        File.WriteAllLines(path, lines);
        return path;
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}
