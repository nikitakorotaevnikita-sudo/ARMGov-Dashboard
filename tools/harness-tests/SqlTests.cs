#nullable enable

using System.Text.Json;
using ArmGov.Harness;

public static class SqlTests
{
    public static void GuardRejectsSmokeMaliciousSql()
    {
        var malicious = new[]
        {
            "drop table sungero_wf_task",
            "select 1; delete from sungero_wf_task",
            "select 1; select 2",
            "select 1 -- harmless\n; delete from sungero_wf_task",
            "update sungero_wf_task set subject = 'x'",
            "select dblink_exec('dbname=x','select 1')",
            "select pg_read_binary_file('pg_hba.conf')",
            "select pg_terminate_backend(1)",
            "select set_config('statement_timeout','0',false)",
            "select pg_advisory_lock(42)",
            "select query_to_xml('select 1', true, false, '')",
            "select * into zzz from sungero_wf_task",
            "select U&\"pg_sl\\0065ep\"(60)",
            "select dbl/**/ink_exec('dbname=x','select 1')",
            "select * in/**/to zzz from sungero_wf_task",
            "select se/**/t_config('statement_timeout','0',false)",
            "select \"pg_sleep\"(60)",
            "select \"dblink_exec\"('dbname=x','select 1')",
            "select rolpassword from pg_authid",
            "select 'unterminated"
        };

        foreach (var sql in malicious)
        {
            var result = SqlGuard.Check(sql);
            Check.True(!result.Ok);
            Check.True(result.Effective is null);
        }
    }

    public static void GuardPreservesSmokeLiteralAndCommentBehavior()
    {
        var accepted = new[]
        {
            "select /* delete */ count(*) from sungero_wf_task",
            "select 1 -- delete from t",
            "select subject from sungero_wf_task where subject like '%update%'",
            "select string_agg(subject, '; ') from sungero_wf_task",
            "select '-- data, not comment' as x",
            "select 'it''s ok' as x",
            "select \"stran;ny\" from t",
            "select $tag$ text with ; and -- inside $tag$"
        };

        foreach (var sql in accepted)
        {
            var result = SqlGuard.Check(sql);
            Check.True(result.Ok);
            Check.True(result.Effective is not null);
        }

        var split = SqlGuard.Check("select a/**/from sungero_wf_task");
        Check.True(!split.Effective!.Contains("afrom", StringComparison.Ordinal));
    }

    public static void GuardAlwaysAddsOuterRowLimit()
    {
        var result = SqlGuard.Check(
            "select * from a where a.x in (select y from sungero_wf_task limit 10)");

        Check.True(result.Ok);
        Check.True(result.Effective!.EndsWith("limit 201", StringComparison.OrdinalIgnoreCase));
        Check.True(result.Effective.Count(c => c == ';') == 0);
    }

    public static void PublicGuardRetainsLegacyDenyList()
    {
        foreach (var sql in new[]
        {
            "select dblink_connect('x')",
            "select pg_read_file('x')",
            "select lo_export(1, '/tmp/x')",
            "select * into stolen from public.sungero_wf_task",
            "select rolpassword from pg_shadow"
        })
            Check.True(!SqlGuard.Check(sql).Ok);

        var ok = SqlGuard.Check("select 'drop; pg_sleep' as harmless");
        Check.True(ok.Ok);
        Check.True(ok.Effective!.EndsWith("limit 201", StringComparison.OrdinalIgnoreCase));
    }

    public static void ScopeRejectsUnknownRelationsAndFunctions()
    {
        var allowed = Allowed();

        Check.True(!SqlScopePolicy.Check("select * from private.payroll", allowed).Ok);
        Check.True(SqlScopePolicy.Check(
            "select count(*) from public.sungero_wf_task", allowed).Ok);
        Check.True(!SqlScopePolicy.Check(
            "select public.run_external_command('x')", allowed).Ok);
        Check.True(!SqlScopePolicy.Check(
            "select * from public.generate_series(1, 2)", allowed).Ok);
    }

    public static void ScopeChecksCtesNestedQueriesAndUnionBranches()
    {
        var allowed = Allowed();
        var accepted =
            "with recent as (" +
            "select id, row_number() over (order by id) rn from public.sungero_wf_task" +
            ") select count(*) from recent where id in (" +
            "select id from public.sungero_wf_assignment) " +
            "union select count(*) from \"public\".\"sungero_wf_task\"";
        Check.True(SqlScopePolicy.Check(accepted, allowed).Ok);

        Check.True(!SqlScopePolicy.Check(
            "select * from (select * from private.payroll) p", allowed).Ok);
        Check.True(!SqlScopePolicy.Check(
            "select id from public.sungero_wf_task union select id from secret.data", allowed).Ok);
        Check.True(!SqlScopePolicy.Check(
            "with x as (select pg_sleep(1)) select * from x", allowed).Ok);
        Check.True(!SqlScopePolicy.Check(
            "with recursive x as (select 1) select * from x", allowed).Ok);
    }

    public static void ScopeHandlesStringsCommentsQualificationAndCasts()
    {
        var allowed = Allowed();
        var accepted = new[]
        {
            "select lower(subject) from sungero_wf_task",
            "select E'from private.payroll\\n' from public.sungero_wf_task",
            "select $tag$join private.payroll$tag$ from public.sungero_wf_task",
            "select count(*) /* from private.payroll */ from public.sungero_wf_task",
            "select extract(year from created), round(avg(id)) from public.sungero_wf_task",
            "select id::bigint from public.sungero_wf_task"
        };
        foreach (var sql in accepted)
            Check.True(SqlScopePolicy.Check(sql, allowed).Ok);

        Check.True(!SqlScopePolicy.Check(
            "select id::private.secret_type from public.sungero_wf_task", allowed).Ok);
        Check.True(!SqlScopePolicy.Check(
            "select count(*) from \"private\".\"payroll\"", allowed).Ok);
        Check.True(!SqlScopePolicy.Check(
            "select id #=# 1 from public.sungero_wf_task", allowed).Ok);
    }

    public static async Task ExecutorCancellationWhileWaitingDoesNotLeakSlot()
    {
        using var slots = new SemaphoreSlim(0, 1);
        var executor = new ReadOnlyExecutor(
            "Host=127.0.0.1;Database=harness_test;Username=none;Password=none",
            Allowed(),
            slots);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var query = new QuerySpec(
            "select count(*) from public.sungero_wf_task",
            new Dictionary<string, JsonElement>(),
            "test",
            null,
            null);

        var cancelledAsExpected = false;
        try
        {
            await executor.ExecuteAsync(query, cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            cancelledAsExpected = true;
        }

        Check.True(cancelledAsExpected);
        Check.Equal(0, slots.CurrentCount);
    }

    public static async Task ExecutorRejectsNonScalarParametersBeforeConnecting()
    {
        using var slots = new SemaphoreSlim(1, 1);
        var executor = new ReadOnlyExecutor(
            "Host=127.0.0.1;Database=harness_test;Username=none;Password=none",
            Allowed(),
            slots);
        var query = new QuerySpec(
            "select count(*) from public.sungero_wf_task where id = @ids",
            new Dictionary<string, JsonElement>
            {
                ["ids"] = JsonSerializer.SerializeToElement(new[] { 1, 2 })
            },
            "test",
            null,
            null);

        var rejected = false;
        try
        {
            await executor.ExecuteAsync(query, CancellationToken.None);
        }
        catch (HarnessException ex)
        {
            rejected = ex.Error.Code == "invalid_sql_parameter";
        }

        Check.True(rejected);
        Check.Equal(1, slots.CurrentCount);
    }

    private static HashSet<string> Allowed() => new(StringComparer.OrdinalIgnoreCase)
    {
        "public.sungero_wf_task",
        "public.sungero_wf_assignment"
    };
}
