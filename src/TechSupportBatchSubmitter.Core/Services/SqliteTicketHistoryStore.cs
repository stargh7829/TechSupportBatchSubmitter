using Microsoft.Data.Sqlite;
using TechSupportBatchSubmitter.Core.Interfaces;
using TechSupportBatchSubmitter.Core.Models;

namespace TechSupportBatchSubmitter.Core.Services;

public sealed class SqliteTicketHistoryStore : ITicketHistoryStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteTicketHistoryStore(string databasePath)
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
                CREATE TABLE IF NOT EXISTS submission_history (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    occurred_at TEXT NOT NULL,
                    workbook_path TEXT NOT NULL,
                    worksheet_name TEXT NOT NULL,
                    excel_row_number INTEGER NOT NULL,
                    fingerprint TEXT NOT NULL,
                    case_id TEXT NULL,
                    title TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    system_name TEXT NOT NULL,
                    applicant TEXT NOT NULL,
                    assignee TEXT NOT NULL,
                    state INTEGER NOT NULL,
                    message TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_submission_history_case
                ON submission_history(case_id);

                CREATE INDEX IF NOT EXISTS ix_submission_history_source
                ON submission_history(workbook_path, worksheet_name, excel_row_number, fingerprint);

                CREATE TABLE IF NOT EXISTS close_history (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    occurred_at TEXT NOT NULL,
                    source_kind TEXT NOT NULL,
                    workbook_path TEXT NULL,
                    worksheet_name TEXT NULL,
                    excel_row_number INTEGER NULL,
                    fingerprint TEXT NULL,
                    case_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    system_name TEXT NOT NULL,
                    applicant TEXT NOT NULL,
                    assignee TEXT NOT NULL,
                    state INTEGER NOT NULL,
                    message TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_close_history_case
                ON close_history(case_id);

                CREATE INDEX IF NOT EXISTS ix_close_history_source
                ON close_history(workbook_path, worksheet_name, excel_row_number, fingerprint);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordSubmissionAsync(
        string workbookPath,
        string worksheetName,
        TicketRow row,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO submission_history (
                    occurred_at,
                    workbook_path,
                    worksheet_name,
                    excel_row_number,
                    fingerprint,
                    case_id,
                    title,
                    event_type,
                    system_name,
                    applicant,
                    assignee,
                    state,
                    message
                )
                VALUES (
                    $occurredAt,
                    $workbookPath,
                    $worksheetName,
                    $excelRowNumber,
                    $fingerprint,
                    $caseId,
                    $title,
                    $eventType,
                    $systemName,
                    $applicant,
                    $assignee,
                    $state,
                    $message
                );
                """;
            command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$workbookPath", Path.GetFullPath(workbookPath));
            command.Parameters.AddWithValue("$worksheetName", worksheetName);
            command.Parameters.AddWithValue("$excelRowNumber", row.ExcelRowNumber);
            command.Parameters.AddWithValue("$fingerprint", row.Fingerprint);
            command.Parameters.AddWithValue("$caseId", (object?)NullIfWhiteSpace(row.TicketNumber) ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", row.Title);
            command.Parameters.AddWithValue("$eventType", row.EventType);
            command.Parameters.AddWithValue("$systemName", row.SystemName);
            command.Parameters.AddWithValue("$applicant", row.Applicant);
            command.Parameters.AddWithValue("$assignee", row.Assignee);
            command.Parameters.AddWithValue("$state", (int)row.State);
            command.Parameters.AddWithValue("$message", (object?)Sanitize(message) ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordCloseAsync(
        PendingTicketRow ticket,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO close_history (
                    occurred_at,
                    source_kind,
                    workbook_path,
                    worksheet_name,
                    excel_row_number,
                    fingerprint,
                    case_id,
                    title,
                    event_type,
                    system_name,
                    applicant,
                    assignee,
                    state,
                    message
                )
                VALUES (
                    $occurredAt,
                    $sourceKind,
                    $workbookPath,
                    $worksheetName,
                    $excelRowNumber,
                    $fingerprint,
                    $caseId,
                    $title,
                    $eventType,
                    $systemName,
                    $applicant,
                    $assignee,
                    $state,
                    $message
                );
                """;
            command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$sourceKind", NullIfWhiteSpace(ticket.SourceKind) ?? "PendingQuery");
            command.Parameters.AddWithValue("$workbookPath", (object?)ticket.WorkbookPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$worksheetName", (object?)ticket.WorksheetName ?? DBNull.Value);
            command.Parameters.AddWithValue("$excelRowNumber", (object?)ticket.ExcelRowNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("$fingerprint", (object?)ticket.Fingerprint ?? DBNull.Value);
            command.Parameters.AddWithValue("$caseId", ticket.CaseId.Trim());
            command.Parameters.AddWithValue("$title", ticket.CaseTitle);
            command.Parameters.AddWithValue("$eventType", ticket.TypeName);
            command.Parameters.AddWithValue("$systemName", ticket.SystemName);
            command.Parameters.AddWithValue("$applicant", ticket.Applicant);
            command.Parameters.AddWithValue("$assignee", ticket.Assignee);
            command.Parameters.AddWithValue("$state", (int)ticket.CloseState);
            command.Parameters.AddWithValue("$message", (object?)Sanitize(ticket.CloseMessage) ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SubmissionHistoryRecord>> GetSubmissionHistoryAsync(
        int limit = 500,
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
                    id,
                    occurred_at,
                    workbook_path,
                    worksheet_name,
                    excel_row_number,
                    fingerprint,
                    case_id,
                    title,
                    event_type,
                    system_name,
                    applicant,
                    assignee,
                    state,
                    message
                FROM submission_history
                ORDER BY id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
            var records = new List<SubmissionHistoryRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add(new SubmissionHistoryRecord(
                    reader.GetInt64(0),
                    DateTimeOffset.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    (SubmissionState)reader.GetInt32(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13)));
            }

            return records;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CloseHistoryRecord>> GetCloseHistoryAsync(
        int limit = 500,
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
                    id,
                    occurred_at,
                    source_kind,
                    workbook_path,
                    worksheet_name,
                    excel_row_number,
                    fingerprint,
                    case_id,
                    title,
                    event_type,
                    system_name,
                    applicant,
                    assignee,
                    state,
                    message
                FROM close_history
                ORDER BY id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
            var records = new List<CloseHistoryRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add(new CloseHistoryRecord(
                    reader.GetInt64(0),
                    DateTimeOffset.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    (TicketCloseState)reader.GetInt32(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14)));
            }

            return records;
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

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Sanitize(string? message)
    {
        var text = NullIfWhiteSpace(message);
        if (text is null)
        {
            return null;
        }

        var safe = text
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return safe.Length <= 300 ? safe : safe[..300];
    }
}
