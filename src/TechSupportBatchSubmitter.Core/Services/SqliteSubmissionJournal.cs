using Microsoft.Data.Sqlite;
using TechSupportBatchSubmitter.Core.Interfaces;
using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Services;

public sealed class SqliteSubmissionJournal : ISubmissionJournal
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteSubmissionJournal(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS submission_journal (
                    workbook_path TEXT NOT NULL,
                    worksheet_name TEXT NOT NULL,
                    excel_row_number INTEGER NOT NULL,
                    fingerprint TEXT NOT NULL,
                    candidate_case_id TEXT NOT NULL,
                    state INTEGER NOT NULL,
                    updated_at TEXT NOT NULL,
                    last_error TEXT NULL,
                    PRIMARY KEY (workbook_path, worksheet_name, excel_row_number, fingerprint)
                );

                CREATE INDEX IF NOT EXISTS ix_submission_journal_candidate
                ON submission_journal(candidate_case_id);

                CREATE TABLE IF NOT EXISTS submission_metadata (
                    key TEXT NOT NULL PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertCandidateAsync(
        string workbookPath,
        string worksheetName,
        TicketRow row,
        string candidateCaseId,
        SubmissionState state,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(candidateCaseId))
        {
            throw new ArgumentException("候选编号不能为空。", nameof(candidateCaseId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO submission_journal (
                    workbook_path,
                    worksheet_name,
                    excel_row_number,
                    fingerprint,
                    candidate_case_id,
                    state,
                    updated_at,
                    last_error
                )
                VALUES (
                    $workbookPath,
                    $worksheetName,
                    $excelRowNumber,
                    $fingerprint,
                    $candidateCaseId,
                    $state,
                    $updatedAt,
                    $lastError
                )
                ON CONFLICT(workbook_path, worksheet_name, excel_row_number, fingerprint)
                DO UPDATE SET
                    candidate_case_id = excluded.candidate_case_id,
                    state = excluded.state,
                    updated_at = excluded.updated_at,
                    last_error = excluded.last_error;
                """;
            AddKeyParameters(command, workbookPath, worksheetName, row);
            command.Parameters.AddWithValue("$candidateCaseId", candidateCaseId.Trim());
            command.Parameters.AddWithValue("$state", (int)state);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$lastError", (object?)error ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JournalEntry?> GetAsync(
        string workbookPath,
        string worksheetName,
        TicketRow row,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    workbook_path,
                    worksheet_name,
                    excel_row_number,
                    fingerprint,
                    candidate_case_id,
                    state,
                    updated_at,
                    last_error
                FROM submission_journal
                WHERE workbook_path = $workbookPath
                  AND worksheet_name = $worksheetName
                  AND excel_row_number = $excelRowNumber
                  AND fingerprint = $fingerprint;
                """;
            AddKeyParameters(command, workbookPath, worksheetName, row);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new JournalEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                (SubmissionState)reader.GetInt32(5),
                DateTimeOffset.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkStateAsync(
        string workbookPath,
        string worksheetName,
        TicketRow row,
        SubmissionState state,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE submission_journal
                SET state = $state,
                    updated_at = $updatedAt,
                    last_error = $lastError
                WHERE workbook_path = $workbookPath
                  AND worksheet_name = $worksheetName
                  AND excel_row_number = $excelRowNumber
                  AND fingerprint = $fingerprint;
                """;
            AddKeyParameters(command, workbookPath, worksheetName, row);
            command.Parameters.AddWithValue("$state", (int)state);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$lastError", (object?)error ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DateTimeOffset?> GetLastSaveAttemptAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT value
                FROM submission_metadata
                WHERE key = 'last_save_attempt_utc';
                """;
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is string text && DateTimeOffset.TryParse(text, out var timestamp)
                ? timestamp
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordSaveAttemptAsync(
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO submission_metadata (key, value)
                VALUES ('last_save_attempt_utc', $value)
                ON CONFLICT(key)
                DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$value", attemptedAt.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void AddKeyParameters(
        SqliteCommand command,
        string workbookPath,
        string worksheetName,
        TicketRow row)
    {
        command.Parameters.AddWithValue("$workbookPath", Path.GetFullPath(workbookPath));
        command.Parameters.AddWithValue("$worksheetName", worksheetName);
        command.Parameters.AddWithValue("$excelRowNumber", row.ExcelRowNumber);
        command.Parameters.AddWithValue("$fingerprint", row.Fingerprint);
    }
}
