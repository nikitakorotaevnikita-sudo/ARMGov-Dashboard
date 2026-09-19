#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace ArmGov.Harness;

public sealed class EmployeeResolver : IEmployeeResolver
{
    private const int MaximumCandidates = 20;
    private readonly string _connectionString;

    public EmployeeResolver(string connectionString)
    {
        _connectionString = !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new ArgumentException("Connection string is required.", nameof(connectionString));
    }

    public async Task<EmployeeCandidate[]> SearchAsync(
        string[] tokens,
        CancellationToken ct)
    {
        ValidateTokens(tokens);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(BuildSearchSql(tokens.Length), connection)
        {
            CommandTimeout = 10
        };
        for (var index = 0; index < tokens.Length; index++)
            command.Parameters.AddWithValue($"token{index}", $"%{EscapeLike(tokens[index].Trim())}%");

        var candidates = await ReadCandidatesAsync(command, ct).ConfigureAwait(false);
        if (candidates.Count > MaximumCandidates)
            throw Error(
                "too_many_candidates",
                "Найдено более 20 сотрудников. Уточните имя или подразделение.",
                true);
        return candidates.ToArray();
    }

    public async Task<EmployeeCandidate?> GetAsync(long id, CancellationToken ct)
    {
        if (id <= 0)
            return null;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            CandidateProjection +
            "where r.id = @id " +
            "and exists (select 1 from public.sungero_wf_assignment a where a.performer = r.id) " +
            "limit 1",
            connection)
        {
            CommandTimeout = 10
        };
        command.Parameters.AddWithValue("id", id);
        var candidates = await ReadCandidatesAsync(command, ct).ConfigureAwait(false);
        return candidates.SingleOrDefault();
    }

    private const string CandidateProjection =
        "select r.id, r.name::text, coalesce(d.name::text, ''), " +
        "coalesce(to_jsonb(r)->>'status', '') " +
        "from public.sungero_core_recipient r " +
        "left join public.sungero_core_recipient d " +
        "on d.id = r.department_company_sungero ";

    private static string BuildSearchSql(int tokenCount)
    {
        var predicates = Enumerable.Range(0, tokenCount)
            .Select(index => $"r.name::text ilike @token{index} escape '\\'");
        return CandidateProjection +
            "where r.name is not null " +
            "and exists (select 1 from public.sungero_wf_assignment a where a.performer = r.id) " +
            $"and {string.Join(" and ", predicates)} " +
            "order by r.name::text, r.id " +
            $"limit {MaximumCandidates + 1}";
    }

    private static async Task<List<EmployeeCandidate>> ReadCandidatesAsync(
        NpgsqlCommand command,
        CancellationToken ct)
    {
        var candidates = new List<EmployeeCandidate>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var department = reader.GetString(2);
            var status = reader.GetString(3);
            var description = string.IsNullOrWhiteSpace(status) ||
                              status.Equals("Active", StringComparison.OrdinalIgnoreCase)
                ? department
                : string.IsNullOrWhiteSpace(department)
                    ? $"Статус: {status}"
                    : $"{department} · статус: {status}";
            candidates.Add(new EmployeeCandidate(
                reader.GetInt64(0),
                reader.GetString(1),
                description));
        }
        return candidates;
    }

    private static void ValidateTokens(string[] tokens)
    {
        if (tokens is null ||
            tokens.Length is < 1 or > 5 ||
            tokens.Any(token => string.IsNullOrWhiteSpace(token) || token.Trim().Length > 100))
            throw Error(
                "invalid_employee_tokens",
                "Для поиска требуется от 1 до 5 токенов длиной от 1 до 100 символов.",
                false);
    }

    private static string EscapeLike(string value) =>
        value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static HarnessException Error(string code, string message, bool retryable) =>
        new(new HarnessError(code, message, retryable));
}
