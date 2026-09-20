#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public sealed record ReportDraftOutcome(
    RenderedReport Report,
    ReportSpec Spec,
    int ModelCalls,
    int Repairs);

public sealed class ReportDraftService
{
    private readonly IAnalysisStageModel _model;

    public ReportDraftService(IAnalysisStageModel model) =>
        _model = model ?? throw new ArgumentNullException(nameof(model));

    public async Task<ReportDraftOutcome> CreateAsync(
        string question,
        RunContext context,
        int remainingModelCalls,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (context.Interpretation is null)
            throw Invalid("Отчёт требует установленного контекста запуска.");
        if (remainingModelCalls < 1)
            throw Invalid("Недостаточно вызовов модели для черновика отчёта.");

        var original = new ReportGenerationInput(
            question ?? string.Empty,
            context.Interpretation,
            ResultManifestFactory.Create(context.Results));
        var draft = await _model.DraftReportAsync(original, ct).ConfigureAwait(false);
        var firstValidation = BindAndValidate(
            draft,
            context.Results,
            context.Interpretation,
            out var first);
        if (firstValidation.Ok)
            return new ReportDraftOutcome(
                ReportRenderer.Render(first!, context.Results, context.Interpretation),
                first!,
                1,
                0);

        if (remainingModelCalls < 2)
            throw Invalid(firstValidation.Errors);

        var repairedDraft = await _model.RepairReportAsync(
            new ReportRepairInput(original, draft, firstValidation.Errors),
            ct).ConfigureAwait(false);
        var repairedValidation = BindAndValidate(
            repairedDraft,
            context.Results,
            context.Interpretation,
            out var repaired);
        if (!repairedValidation.Ok)
            throw Invalid(repairedValidation.Errors);

        return new ReportDraftOutcome(
            ReportRenderer.Render(repaired!, context.Results, context.Interpretation),
            repaired!,
            2,
            1);
    }

    private static ValidationResult BindAndValidate(
        ReportDraft? draft,
        ResultStore store,
        Interpretation interpretation,
        out ReportSpec? spec)
    {
        spec = null;
        if (!HasConvertibleGraph(draft))
            return StructuralFailure();

        try
        {
            spec = draft!.ToReportSpec() with { Interpretation = interpretation };
            return ReportValidator.Validate(spec, store, interpretation);
        }
        catch (ArgumentException)
        {
            return StructuralFailure();
        }
        catch (InvalidOperationException)
        {
            return StructuralFailure();
        }
        catch (NullReferenceException)
        {
            return StructuralFailure();
        }
    }

    private static bool HasConvertibleGraph(ReportDraft? draft)
    {
        if (draft?.Report is not { } report ||
            string.IsNullOrWhiteSpace(report.Title) ||
            report.Interpretation is null)
            return false;
        if (report.Blocks.Any(block => block is null) ||
            report.Facts.Any(fact => fact is null))
            return false;
        return !report.Facts.Any(fact => fact.Inputs.Any(input => input is null));
    }

    private static ValidationResult StructuralFailure() => new(
        false,
        [new HarnessError(
            "invalid_report_structure",
            "Структура черновика отчёта неполна или недопустима.",
            false)]);

    private static HarnessException Invalid(params HarnessError[] errors)
    {
        var details = errors
            .Select(error => error with { Message = Sanitize(error.Message) })
            .ToArray();
        return new HarnessException(
            new HarnessError(
                "invalid_report",
                string.Join(" ", details.Select(error => error.Message)),
                false),
            details);
    }

    private static HarnessException Invalid(string message) =>
        new(new HarnessError("invalid_report", Sanitize(message), false));

    private static string Sanitize(string? value)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .ToArray())
            .Trim();
        return cleaned.Length <= 1000 ? cleaned : cleaned[..1000];
    }
}
