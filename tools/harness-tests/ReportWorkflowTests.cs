#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArmGov.Harness;

public static class ReportWorkflowTests
{
    private static readonly Interpretation Meaning = new(
        "personal_instruction_count", "Поручения", "поручение", null, null, null);

    public static async Task ValidDraftRendersExactStoredFactAndUsesContextInterpretation()
    {
        var model = new ScriptedStageModel(ValidDraft(Meaning with { Unit = "invented" }));
        var context = ContextWithResult(12);
        var service = new ReportDraftService(model);

        var outcome = await service.CreateAsync("Сколько поручений", context, 2, CancellationToken.None);

        Check.Equal(1, outcome.ModelCalls);
        Check.Equal(0, outcome.Repairs);
        Check.Equal(12m, outcome.Report.Facts["total"].GetDecimal());
        Check.Equal(Meaning, outcome.Report.Interpretation);
        Check.Equal(1, model.DraftReportCalls);
        Check.Equal(0, model.RepairReportCalls);
    }

    public static void ResultManifestKeepsStoredIdentityAndBoundsPromptRows()
    {
        var context = ContextWithRows(55, new Truncation(true, false, ["0:n"], false));

        var manifest = ResultManifestFactory.Create(context.Results).Single();

        Check.Equal("r1", manifest.ResultId);
        Check.Equal(55, manifest.RowCount);
        Check.Equal(50, manifest.Rows.Length);
        Check.True(manifest.Truncation.Rows);
        Check.True(manifest.Truncation.Cells.Contains("0:n"));
        Check.Equal(0, manifest.Rows[0][0].GetInt32());
        Check.Equal(49, manifest.Rows[49][0].GetInt32());
    }

    public static void ValidatorDistinguishesAliasEmptyUnknownResultAndUnknownColumn()
    {
        var context = ContextWithResult(3);

        Check.Equal("invalid_result_id", FirstError("tool:leaders", "n", context));
        Check.Equal("invalid_result_id", FirstError("", "n", context));
        Check.Equal("unknown_result", FirstError("r404", "n", context));
        Check.Equal("unknown_column", FirstError("r1", "missing", context));
    }

    public static async Task InvalidFirstDraftIsRepairedOnceWithSameManifest()
    {
        var model = new ScriptedStageModel(
            DraftWithText("Получено 12"),
            ValidDraft(Meaning));
        var context = ContextWithResult(12);
        var service = new ReportDraftService(model);

        var outcome = await service.CreateAsync("Сколько поручений", context, 2, CancellationToken.None);

        Check.Equal(2, outcome.ModelCalls);
        Check.Equal(1, outcome.Repairs);
        Check.Equal(12m, outcome.Report.Facts["total"].GetDecimal());
        Check.Equal(1, model.RepairReportCalls);
        Check.Equal("unverified_numeric_text", model.RepairInput!.Errors.Single().Code);
        Check.Equal("r1", model.RepairInput.Original.Results.Single().ResultId);
        Check.Equal(1, model.RepairInput.Original.Results.Single().Rows.Length);
    }

    public static async Task InvalidDraftWithoutRepairBudgetThrowsInvalidReport()
    {
        var model = new ScriptedStageModel(DraftWithText("Получено 12"));
        var service = new ReportDraftService(model);

        var error = await ThrowsHarness(() => service.CreateAsync(
            "Сколько поручений", ContextWithResult(12), 1, CancellationToken.None));

        Check.Equal("invalid_report", error.Code);
        Check.Equal(0, model.RepairReportCalls);
    }

    public static async Task TwoInvalidDraftsNeverReturnRenderedReport()
    {
        var model = new ScriptedStageModel(DraftWithText("Получено 12"), DraftWithText("Итого 13"));
        var service = new ReportDraftService(model);

        var error = await ThrowsHarness(() => service.CreateAsync(
            "Сколько поручений", ContextWithResult(12), 2, CancellationToken.None));

        Check.Equal("invalid_report", error.Code);
        Check.Equal(1, model.DraftReportCalls);
        Check.Equal(1, model.RepairReportCalls);
    }

    public static async Task StructurallyInvalidDraftIsRepairedWithStructuredFeedback()
    {
        var malformed = new ReportDraft(new ImmutableReportSpec(
            "Поручения",
            null!,
            ImmutableArray<ImmutableBlockSpec>.Empty,
            ImmutableArray<ImmutableFactSpec>.Empty,
            ImmutableArray<string>.Empty,
            null));
        var model = new ScriptedStageModel(malformed, ValidDraft(Meaning));
        var service = new ReportDraftService(model);

        var outcome = await service.CreateAsync(
            "Сколько поручений", ContextWithResult(12), 2, CancellationToken.None);

        Check.Equal(2, outcome.ModelCalls);
        Check.Equal(1, outcome.Repairs);
        Check.Equal("invalid_report_structure", model.RepairInput!.Errors.Single().Code);
    }

    public static async Task NullNestedDraftElementsAreRepairedWithStructuredFeedback()
    {
        var malformedDrafts = new[]
        {
            new ReportDraft(new ImmutableReportSpec(
                "Поручения", ImmutableInterpretation.FromInterpretation(Meaning), [null!], ImmutableArray<ImmutableFactSpec>.Empty,
                ImmutableArray<string>.Empty, null)),
            new ReportDraft(new ImmutableReportSpec(
                "Поручения", ImmutableInterpretation.FromInterpretation(Meaning), ImmutableArray<ImmutableBlockSpec>.Empty, [null!],
                ImmutableArray<string>.Empty, null)),
            new ReportDraft(new ImmutableReportSpec(
                "Поручения", ImmutableInterpretation.FromInterpretation(Meaning), ImmutableArray<ImmutableBlockSpec>.Empty,
                [new ImmutableFactSpec("total", "cell", [null!])],
                ImmutableArray<string>.Empty, null))
        };

        foreach (var malformed in malformedDrafts)
        {
            var model = new ScriptedStageModel(malformed, ValidDraft(Meaning));
            var outcome = await new ReportDraftService(model).CreateAsync(
                "Сколько поручений", ContextWithResult(12), 2, CancellationToken.None);

            Check.Equal(1, outcome.Repairs);
            Check.Equal("invalid_report_structure", model.RepairInput!.Errors.Single().Code);
        }
    }

    public static async Task NullTitleIsRepairedWithStructuredFeedback()
    {
        var model = new ScriptedStageModel(NullTitleDraft(), ValidDraft(Meaning));
        var outcome = await new ReportDraftService(model).CreateAsync(
            "Сколько поручений", ContextWithResult(12), 2, CancellationToken.None);

        Check.Equal(2, outcome.ModelCalls);
        Check.Equal(1, outcome.Repairs);
        Check.Equal("invalid_report_structure", model.RepairInput!.Errors.Single().Code);
    }

    public static async Task SecondNullTitleTerminatesWithInvalidReportAndStructuredFeedback()
    {
        var model = new ScriptedStageModel(NullTitleDraft(), NullTitleDraft());

        var exception = await ThrowsHarnessException(() => new ReportDraftService(model).CreateAsync(
            "Сколько поручений", ContextWithResult(12), 2, CancellationToken.None));

        Check.Equal("invalid_report", exception.Error.Code);
        Check.Equal("invalid_report_structure", exception.ValidationErrors.Single().Code);
    }

    public static async Task NoBudgetInvalidReportRetainsValidatorCodes()
    {
        var model = new ScriptedStageModel(DraftWithBlock("r404", "n"));
        var service = new ReportDraftService(model);

        var exception = await ThrowsHarnessException(() => service.CreateAsync(
            "Сколько поручений", ContextWithResult(12), 1, CancellationToken.None));

        Check.Equal("invalid_report", exception.Error.Code);
        Check.Equal("unknown_result", exception.ValidationErrors.Single().Code);
    }

    public static async Task InvalidRepairRetainsValidatorCodes()
    {
        var model = new ScriptedStageModel(
            DraftWithBlock("r1", "missing"),
            DraftWithText("Получено 12"));
        var service = new ReportDraftService(model);

        var exception = await ThrowsHarnessException(() => service.CreateAsync(
            "Сколько поручений", ContextWithResult(12), 2, CancellationToken.None));

        Check.Equal("invalid_report", exception.Error.Code);
        Check.Equal("unverified_numeric_text", exception.ValidationErrors.Single().Code);
    }

    private static string FirstError(string resultId, string column, RunContext context)
    {
        var spec = new ReportSpec(
            "Поручения",
            Meaning,
            [new BlockSpec("table", resultId, [column], null, null)],
            [],
            [],
            null);
        return ReportValidator.Validate(spec, context.Results, Meaning).Errors[0].Code;
    }

    private static RunContext ContextWithResult(int total)
    {
        var context = Context();
        context.Results.Add(Result(
            [new ColumnSpec("n", "Количество", "number")],
            [[JsonSerializer.SerializeToElement(total)]],
            new Truncation(false, false, [], false)));
        return context;
    }

    private static RunContext ContextWithRows(int count, Truncation truncation)
    {
        var context = Context();
        context.Results.Add(Result(
            [new ColumnSpec("n", "Количество", "number")],
            Enumerable.Range(0, count)
                .Select(number => new[] { JsonSerializer.SerializeToElement(number) })
                .ToArray(),
            truncation));
        return context;
    }

    private static RunContext Context()
    {
        var context = new RunContext("report-workflow", TimeProvider.System, CancellationToken.None)
        {
            Interpretation = Meaning
        };
        return context;
    }

    private static QueryResult Result(
        ColumnSpec[] columns,
        JsonElement[][] rows,
        Truncation truncation) => new(
            "tool:leaders",
            columns,
            rows,
            "select fixture",
            DateTimeOffset.Parse("2026-09-20T12:00:00+00:00"),
            truncation,
            []);

    private static ReportDraft ValidDraft(Interpretation interpretation) => new(new ReportSpec(
        "Поручения",
        interpretation,
        [new BlockSpec("table", "r1", ["n"], null, null)],
        [new FactSpec("total", "cell", [new CellRef("r1", 0, "n")])],
        ["Получено {{total}}"],
        null));

    private static ReportDraft DraftWithText(string text) => new(new ReportSpec(
        "Поручения",
        Meaning,
        [],
        [],
        [text],
        null));

    private static ReportDraft DraftWithBlock(string resultId, string column) => new(new ReportSpec(
        "Поручения",
        Meaning,
        [new BlockSpec("table", resultId, [column], null, null)],
        [],
        [],
        null));

    private static ReportDraft NullTitleDraft() => new(new ImmutableReportSpec(
        null!,
        ImmutableInterpretation.FromInterpretation(Meaning),
        ImmutableArray<ImmutableBlockSpec>.Empty,
        ImmutableArray<ImmutableFactSpec>.Empty,
        ImmutableArray<string>.Empty,
        null));

    private static async Task<HarnessError> ThrowsHarness(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (HarnessException exception)
        {
            return exception.Error;
        }

        throw new InvalidOperationException("Expected HarnessException.");
    }

    private static async Task<HarnessException> ThrowsHarnessException(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (HarnessException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected HarnessException.");
    }

    private sealed class ScriptedStageModel : IAnalysisStageModel
    {
        private readonly Queue<ReportDraft> _reports;

        public ScriptedStageModel(params ReportDraft[] reports) => _reports = new Queue<ReportDraft>(reports);
        public int DraftReportCalls { get; private set; }
        public int RepairReportCalls { get; private set; }
        public ReportRepairInput? RepairInput { get; private set; }

        public Task<AnalysisPlan> PlanAsync(PlanningInput input, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SqlDraft> DraftSqlAsync(SqlGenerationInput input, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SqlDraft> RepairSqlAsync(SqlRepairInput input, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ReportDraft> DraftReportAsync(ReportGenerationInput input, CancellationToken ct)
        {
            DraftReportCalls++;
            return Task.FromResult(_reports.Dequeue());
        }
        public Task<ReportDraft> RepairReportAsync(ReportRepairInput input, CancellationToken ct)
        {
            RepairReportCalls++;
            RepairInput = input;
            return Task.FromResult(_reports.Dequeue());
        }
    }
}
