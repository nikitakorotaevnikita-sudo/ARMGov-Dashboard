#nullable enable

using System.Linq;
using System.Text.Json;
using ArmGov.Harness;

public static class CatalogTests
{
    public static void PersonalCountHasExplicitGrain()
    {
        var catalog = LoadCatalog();
        var metric = catalog.GetMetric("personal_instruction_count");

        Check.Equal("поручение", metric.Unit);
        Check.Equal("root.created", metric.DateField);
        Check.True(metric.Definition.Contains("distinct", StringComparison.OrdinalIgnoreCase));
    }

    public static void CatalogContainsTwelveAllowedRelations()
    {
        var catalog = LoadCatalog();

        Check.Equal(12, catalog.AllowedRelations.Count);
        Check.True(catalog.AllowedRelations.Contains("public.sungero_wf_task"));
        Check.True(catalog.AllowedRelations.Contains("public.sungero_wf_assignment"));
    }

    public static void SearchFindsCatalogEntries()
    {
        var result = LoadCatalog().Search("задания исполнитель", 10);

        Check.Equal(JsonValueKind.Array, result.ValueKind);
        Check.True(result.GetArrayLength() > 0);
        var relation = result.EnumerateArray()
            .First(item =>
                item.TryGetProperty("kind", out var kind) &&
                kind.GetString() == "relation");
        Check.Equal(
            "public.sungero_wf_assignment",
            relation.GetProperty("relation").GetString());
    }

    public static void SearchReturnsMetricsForOverdueSynonyms()
    {
        var result = LoadCatalog().Search("затор", 10);

        Check.Equal(JsonValueKind.Array, result.ValueKind);
        var metric = result.EnumerateArray()
            .First(item =>
                item.TryGetProperty("kind", out var kind) &&
                kind.GetString() == "metric");
        Check.Equal("overdue_assignment_kpi", metric.GetProperty("id").GetString());
    }

    public static void SearchFallsBackToGenericQuery()
    {
        var result = LoadCatalog().Search("квантовый флюс", 10);

        Check.Equal(JsonValueKind.Array, result.ValueKind);
        Check.Equal("metric", result[0].GetProperty("kind").GetString());
        Check.Equal("generic_query", result[0].GetProperty("id").GetString());
    }

    public static void SearchTruncatesLongPhrasesInsteadOfThrowing()
    {
        var result = LoadCatalog().Search(
            "дай статистику по вопросам в обращениях топ десять",
            10);

        Check.Equal(JsonValueKind.Array, result.ValueKind);
        Check.True(result.GetArrayLength() > 0);
    }

    public static void SearchFindsAppealTopics()
    {
        var result = LoadCatalog().Search("обращения топ", 10);
        var metric = result.EnumerateArray()
            .FirstOrDefault(item =>
                item.TryGetProperty("kind", out var kind) &&
                kind.GetString() == "metric" &&
                item.GetProperty("id").GetString() == "appeal_topics");
        Check.Equal("appeal_topics", metric.GetProperty("id").GetString());
    }

    public static void SearchFindsExecutionDisciplineFromInflectedPhrase()
    {
        var result = LoadCatalog().Search(
            "покажи исполнительску дисциплину за 12 месяцев",
            10);
        var metric = result.EnumerateArray()
            .First(item =>
                item.TryGetProperty("kind", out var kind) &&
                kind.GetString() == "metric");
        Check.Equal("execution_discipline", metric.GetProperty("id").GetString());
    }

    public static void InferPeriodTwelveMonthsFromQuestion()
    {
        var period = ToolDispatcher.InferPeriodElement(
            "покажи исполнительскую дисциплину за 12 месяцев");
        Check.Equal("months", period.GetProperty("kind").GetString());
        Check.Equal(12, period.GetProperty("months").GetInt32());
    }

    public static void ExecutionDisciplineDeclaresItsActualPeriodField()
    {
        var metric = LoadCatalog().GetMetric("execution_discipline");
        Check.Equal("task.created", metric.DateField);
    }

    public static void DescribeRejectsUnknownFields()
    {
        var result = LoadCatalog().Describe(
            "public",
            "sungero_wf_task",
            new[] { "id", "invented_field" });

        Check.Equal(
            "schema_mismatch",
            result.GetProperty("error").GetProperty("code").GetString());
    }

    public static void DescribeReturnsOnlyRequestedAllowedFields()
    {
        var result = LoadCatalog().Describe(
            "public",
            "sungero_wf_assignment",
            new[] { "performer", "status" });

        Check.Equal("public.sungero_wf_assignment", result.GetProperty("relation").GetString());
        var fields = result.GetProperty("fields");
        Check.Equal(2, fields.GetArrayLength());
        Check.Equal("performer", fields[0].GetProperty("name").GetString());
        Check.Equal("status", fields[1].GetProperty("name").GetString());
    }

    public static async Task ResolverRejectsInvalidTokensBeforeOpeningDatabase()
    {
        var resolver = new EmployeeResolver(
            "Host=127.0.0.1;Port=1;Database=unreachable;Username=nobody;Password=none");

        await CheckHarnessErrorAsync(
            () => resolver.SearchAsync(
                new[] { "one", "two", "three", "four", "five", "six" },
                CancellationToken.None),
            "invalid_employee_tokens");
        await CheckHarnessErrorAsync(
            () => resolver.SearchAsync(new[] { new string('x', 101) }, CancellationToken.None),
            "invalid_employee_tokens");
    }

    private static AnalyticsCatalog LoadCatalog()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "armgov-standalone.csproj")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("Repository root was not found.");

        return AnalyticsCatalog.Load(
            Path.Combine(directory.FullName, "Harness", "Catalog", "catalog.json"));
    }

    private static async Task CheckHarnessErrorAsync(
        Func<Task<EmployeeCandidate[]>> action,
        string code)
    {
        try
        {
            await action();
        }
        catch (HarnessException ex)
        {
            Check.Equal(code, ex.Error.Code);
            return;
        }

        throw new InvalidOperationException($"Expected HarnessException with code {code}.");
    }
}
