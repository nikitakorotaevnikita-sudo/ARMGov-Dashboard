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

    public static async Task EmployeeResolverFindsOnlyAssignmentPerformers()
    {
        var resolver = new EmployeeResolver(TestConnectionString());

        var candidates = await resolver.SearchAsync(
            new[] { "Тестович", "Иванов" },
            CancellationToken.None);

        Check.Equal(2, candidates.Length);
        Check.Equal(900000001L, candidates[0].Id);
        Check.Equal(900000002L, candidates[1].Id);
        Check.True(candidates[1].Department.Contains("Closed", StringComparison.Ordinal));
        Check.Equal(
            0,
            (await resolver.SearchAsync(
                new[] { "Ивановский" },
                CancellationToken.None)).Length);
    }

    public static async Task EmployeeResolverDoesNotTreatWildcardsAsPatterns()
    {
        var resolver = new EmployeeResolver(TestConnectionString());

        Check.Equal(
            0,
            (await resolver.SearchAsync(new[] { "%" }, CancellationToken.None)).Length);
        Check.Equal(
            0,
            (await resolver.SearchAsync(new[] { "_" }, CancellationToken.None)).Length);
    }

    public static async Task EmployeeResolverReturnsOnlyExistingEmployees()
    {
        var resolver = new EmployeeResolver(TestConnectionString());

        Check.Equal(
            900000003L,
            (await resolver.GetAsync(900000003, CancellationToken.None))!.Id);
        Check.True(await resolver.GetAsync(900000100, CancellationToken.None) is null);
        Check.True(await resolver.GetAsync(999999999, CancellationToken.None) is null);
    }

    public static async Task RootDeduplicationCountsDistinctInstructions()
    {
        var connectionString = TestConnectionString();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        const string sql = """
            select count(distinct root.id) as instructions
            from public.sungero_core_recipient p
            join public.sungero_wf_assignment a on a.performer = p.id
            join public.sungero_wf_task t on t.id = a.task
            join public.sungero_wf_task root on root.id = coalesce(nullif(t.maintask, 0), t.id)
            where p.id = 900000001
              and root.discriminator = 'c290b098-12c7-487d-bb38-73e2c98f9789'
              and root.created >= '2026-01-01'::timestamptz
              and root.created < '2027-01-01'::timestamptz
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        var count = Convert.ToInt64(await command.ExecuteScalarAsync());
        Check.Equal(1L, count);
    }

    public static async Task EmployeeResolverReportsCandidateOverflow()
    {
        var resolver = new EmployeeResolver(TestConnectionString());
        try
        {
            await resolver.SearchAsync(
                new[] { "Переполнение" },
                CancellationToken.None);
        }
        catch (HarnessException ex)
        {
            Check.Equal("too_many_candidates", ex.Error.Code);
            Check.True(ex.Error.Retryable);
            return;
        }

        throw new InvalidOperationException("Expected too_many_candidates.");
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
