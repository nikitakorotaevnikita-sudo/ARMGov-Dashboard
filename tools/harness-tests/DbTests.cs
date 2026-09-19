#nullable enable

using System.Text.Json;
using ArmGov.Harness;
using Npgsql;

public static class DbTests
{
    public static async Task ExecutorReadsAndLimitsRows()
    {
        var connectionString = TestConnectionString();
        using var slots = new SemaphoreSlim(2, 2);
        var executor = new ReadOnlyExecutor(
            connectionString,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "public.harness_items"
            },
            slots);
        var result = await executor.ExecuteAsync(
            new QuerySpec(
                "select id, name from public.harness_items order by id",
                new Dictionary<string, JsonElement>(),
                "fixture",
                null,
                null),
            CancellationToken.None);

        Check.Equal(200, result.Rows.Length);
        Check.True(result.Truncation.Rows);
        Check.Equal(2, slots.CurrentCount);
    }

    public static async Task ReadOnlyTransactionAndRoleBothRejectWrites()
    {
        var connectionString = TestConnectionString();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var before = await CountAsync(connection);

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var readOnly = new NpgsqlCommand(
                "set transaction read only",
                connection,
                transaction))
                await readOnly.ExecuteNonQueryAsync();

            Check.True(await IsRejectedAsync(new NpgsqlCommand(
                "insert into public.harness_items(name) values ('forbidden')",
                connection,
                transaction)));
            await transaction.RollbackAsync();
        }

        Check.True(await IsRejectedAsync(new NpgsqlCommand(
            "insert into public.harness_items(name) values ('forbidden')",
            connection)));
        Check.True(await IsRejectedAsync(new NpgsqlCommand(
            "create table public.forbidden_table(id integer)",
            connection)));
        Check.Equal(before, await CountAsync(connection));
    }

    public static async Task ThreeQueriesReleaseTwoSharedSlots()
    {
        var connectionString = TestConnectionString();
        using var slots = new SemaphoreSlim(2, 2);
        var executor = new ReadOnlyExecutor(
            connectionString,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "public.harness_items"
            },
            slots);
        var query = new QuerySpec(
            "select count(*) from public.harness_items",
            new Dictionary<string, JsonElement>(),
            "fixture",
            null,
            null);

        await Task.WhenAll(
            executor.ExecuteAsync(query, CancellationToken.None),
            executor.ExecuteAsync(query, CancellationToken.None),
            executor.ExecuteAsync(query, CancellationToken.None));

        Check.Equal(2, slots.CurrentCount);
    }

    public static async Task CancellationDuringDatabaseWorkReleasesSlot()
    {
        var connectionString = TestConnectionString();
        using var slots = new SemaphoreSlim(1, 1);
        var executor = new ReadOnlyExecutor(
            connectionString,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "public.harness_items"
            },
            slots);
        var query = new QuerySpec(
            "select count(*) from public.harness_items a " +
            "cross join public.harness_items b " +
            "cross join public.harness_items c " +
            "cross join public.harness_items d",
            new Dictionary<string, JsonElement>(),
            "fixture",
            null,
            null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        var cancelled = false;
        try
        {
            await executor.ExecuteAsync(query, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Check.True(cancelled);
        Check.Equal(1, slots.CurrentCount);
    }

    private static string TestConnectionString()
    {
        var raw = Environment.GetEnvironmentVariable("ARMGOV_TEST_DB");
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "BLOCKED: ARMGOV_TEST_DB is not set; RX config.json is never used.");

        var builder = new NpgsqlConnectionStringBuilder(raw);
        var loopback = string.Equals(builder.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(builder.Host, "::1", StringComparison.OrdinalIgnoreCase);
        if (!loopback || !string.Equals(
                builder.Database,
                "harness_test",
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "BLOCKED: ARMGOV_TEST_DB must target harness_test on loopback.");
        return builder.ConnectionString;
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "select count(*) from public.harness_items",
            connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> IsRejectedAsync(NpgsqlCommand command)
    {
        await using (command)
        {
            try
            {
                await command.ExecuteNonQueryAsync();
                return false;
            }
            catch (PostgresException)
            {
                return true;
            }
        }
    }
}
