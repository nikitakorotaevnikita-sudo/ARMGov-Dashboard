#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace ArmGov.Harness;

public sealed class ReadOnlyExecutor : IQueryExecutor
{
    private const int StatementTimeoutMilliseconds = 10_000;
    private static readonly Regex ParameterName = new(
        "^[a-z][a-z0-9_]{0,31}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> ReservedParameters =
        new(StringComparer.OrdinalIgnoreCase) { "from", "to", "asof" };

    private readonly string _connectionString;
    private readonly IReadOnlySet<string> _allowedRelations;
    private readonly SemaphoreSlim _slots;

    public ReadOnlyExecutor(
        string connectionString,
        IReadOnlySet<string> allowedRelations,
        SemaphoreSlim slots)
    {
        _connectionString = !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new ArgumentException("Connection string is required.", nameof(connectionString));
        _allowedRelations = allowedRelations
            ?? throw new ArgumentNullException(nameof(allowedRelations));
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
    }

    public async Task<QueryResult> ExecuteAsync(QuerySpec query, CancellationToken ct)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));

        var guard = SqlGuard.Check(query.Sql);
        if (!guard.Ok)
            throw Error("unsafe_sql", guard.Reason ?? "SQL отклонён.");
        var scope = SqlScopePolicy.Check(query.Sql, _allowedRelations);
        if (!scope.Ok)
            throw new HarnessException(scope.Errors[0]);
        var parameters = BuildParameters(query);

        var acquired = false;
        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(ct)
                .ConfigureAwait(false);
            try
            {
                await ExecuteControlAsync(
                    connection,
                    transaction,
                    "set transaction read only",
                    ct).ConfigureAwait(false);
                await ExecuteControlAsync(
                    connection,
                    transaction,
                    "set local statement_timeout = '10000ms'",
                    ct).ConfigureAwait(false);
                await ExecuteControlAsync(
                    connection,
                    transaction,
                    "set local search_path = public, pg_catalog",
                    ct).ConfigureAwait(false);

                var result = await ReadAsync(
                    connection,
                    transaction,
                    guard.Effective!,
                    parameters,
                    ct).ConfigureAwait(false);
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return ResultLimiter.Limit(result);
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                }
                catch when (ct.IsCancellationRequested)
                {
                    // Preserve the original cancellation while disposal closes the connection.
                }
                throw;
            }
        }
        finally
        {
            if (acquired) _slots.Release();
        }
    }

    private static async Task ExecuteControlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = 10
        };
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<QueryResult> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string effectiveSql,
        IReadOnlyList<NpgsqlParameter> parameters,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(effectiveSql, connection, transaction)
        {
            CommandTimeout = 10
        };
        foreach (var parameter in parameters)
            command.Parameters.Add(parameter);

        var columns = new List<ColumnSpec>();
        var rows = new List<JsonElement[]>();
        var moreRows = false;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(new ColumnSpec(
                    reader.GetName(i),
                    reader.GetName(i),
                    ColumnType(reader.GetFieldType(i))));

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (rows.Count == 200)
                {
                    moreRows = true;
                    break;
                }
                var row = new JsonElement[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    row[i] = ToJson(reader.IsDBNull(i) ? null : reader.GetValue(i));
                rows.Add(row);
            }
        }

        return new QueryResult(
            "sql",
            columns.ToArray(),
            rows.ToArray(),
            effectiveSql,
            DateTimeOffset.UtcNow,
            new Truncation(moreRows, false, Array.Empty<string>(), false),
            Array.Empty<string>());
    }

    private static List<NpgsqlParameter> BuildParameters(QuerySpec query)
    {
        var result = new List<NpgsqlParameter>();
        foreach (var pair in query.Parameters
                     ?? throw Error("invalid_sql_parameter", "Параметры SQL обязательны."))
        {
            var name = pair.Key.TrimStart('@');
            if (!ParameterName.IsMatch(name) || ReservedParameters.Contains(name))
                throw Error("invalid_sql_parameter", $"Недопустимое имя параметра: {pair.Key}.");
            result.Add(new NpgsqlParameter(name, ScalarValue(pair.Value)));
        }
        if (query.From is not null)
            result.Add(new NpgsqlParameter("from", query.From.Value));
        if (query.To is not null)
            result.Add(new NpgsqlParameter("to", query.To.Value));
        return result;
    }

    private static object ScalarValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => DBNull.Value,
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.Number => value.GetDouble(),
            _ => throw Error(
                "invalid_sql_parameter",
                "Параметры SQL могут быть только scalar или null.")
        };
    }

    private static string ColumnType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly)) return "date";
        if (type == typeof(byte) || type == typeof(short) || type == typeof(int)
            || type == typeof(long) || type == typeof(float) || type == typeof(double)
            || type == typeof(decimal)) return "number";
        return "string";
    }

    private static JsonElement ToJson(object? value)
    {
        if (value is null) return JsonSerializer.SerializeToElement<object?>(null);
        if (value is string or bool or byte or short or int or long or float or double
            or decimal or DateTime or DateTimeOffset or DateOnly or Guid)
            return JsonSerializer.SerializeToElement(value, value.GetType(), HarnessJson.Options);
        if (value is Array array)
        {
            var items = array.Cast<object?>().Select(ToPlainValue).ToArray();
            return JsonSerializer.SerializeToElement(items, HarnessJson.Options);
        }
        return JsonSerializer.SerializeToElement(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            HarnessJson.Options);
    }

    private static object? ToPlainValue(object? value)
    {
        return value is null || value is DBNull
            ? null
            : value is string or bool or byte or short or int or long or float or double
                or decimal or DateTime or DateTimeOffset or DateOnly or Guid
                ? value
                : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static HarnessException Error(string code, string message)
    {
        return new HarnessException(new HarnessError(code, message, false));
    }
}
