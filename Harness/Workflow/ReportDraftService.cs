#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public sealed record ReportDraftOutcome(
    RenderedReport Report,
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
        var first = BindInterpretation(draft, context.Interpretation);
        var firstValidation = ReportValidator.Validate(first, context.Results, context.Interpretation);
        if (firstValidation.Ok)
            return new ReportDraftOutcome(
                ReportRenderer.Render(first, context.Results, context.Interpretation),
                1,
                0);

        if (remainingModelCalls < 2)
            throw Invalid(firstValidation.Errors);

        var repairedDraft = await _model.RepairReportAsync(
            new ReportRepairInput(original, draft, firstValidation.Errors),
            ct).ConfigureAwait(false);
        var repaired = BindInterpretation(repairedDraft, context.Interpretation);
        var repairedValidation = ReportValidator.Validate(repaired, context.Results, context.Interpretation);
        if (!repairedValidation.Ok)
            throw Invalid(repairedValidation.Errors);

        return new ReportDraftOutcome(
            ReportRenderer.Render(repaired, context.Results, context.Interpretation),
            2,
            1);
    }

    private static ReportSpec BindInterpretation(ReportDraft draft, Interpretation interpretation)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft.ToReportSpec() with { Interpretation = interpretation };
    }

    private static HarnessException Invalid(params HarnessError[] errors) =>
        Invalid(string.Join(" ", errors.Select(error => Sanitize(error.Message))));

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
