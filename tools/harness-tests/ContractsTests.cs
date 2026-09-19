#nullable enable

using System.Text.Json;
using ArmGov.Harness;

public static class ContractsTests
{
    public static void WireNamesAreStable()
    {
        var request = new AnalysisRequest(
            "Сравни поручения",
            Array.Empty<EntitySelection>());

        var json = JsonSerializer.Serialize(request, HarnessJson.Options);

        Check.True(json.Contains("\"question\""));
        Check.True(!json.Contains("\"Question\""));
    }

    public static void UnknownInputFieldIsRejected()
    {
        Check.Throws<JsonException>(() => JsonSerializer.Deserialize<AnalysisRequest>(
            "{\"question\":\"q\",\"selections\":[],\"connectionString\":\"x\"}",
            HarnessJson.Options));
    }

    public static void MaximumDepthIsThirtyTwo()
    {
        Check.Equal(32, HarnessJson.Options.MaxDepth);
    }

    public static void EnumsUseStringWireValues()
    {
        var json = JsonSerializer.Serialize(SampleStatus.Completed, HarnessJson.Options);

        Check.Equal("\"completed\"", json);
    }

    public static void Int64AndDecimalKeepExactJsonValues()
    {
        var values = new Dictionary<string, JsonElement>
        {
            ["employeeId"] = JsonSerializer.SerializeToElement(long.MaxValue),
            ["amount"] = JsonSerializer.SerializeToElement(12345678901234567890.12m)
        };

        var json = JsonSerializer.Serialize(values, HarnessJson.Options);

        Check.True(json.Contains(long.MaxValue.ToString()));
        Check.True(json.Contains("12345678901234567890.12"));
    }

    public static void EmptyQuestionIsRejected()
    {
        var result = AnalysisRequestValidator.Validate(Request(""));

        Check.True(!result.Ok);
    }

    public static void FourThousandCharacterQuestionIsAccepted()
    {
        var result = AnalysisRequestValidator.Validate(Request(new string('я', 4000)));

        Check.True(result.Ok);
    }

    public static void FourThousandAndOneCharacterQuestionIsRejected()
    {
        var result = AnalysisRequestValidator.Validate(Request(new string('я', 4001)));

        Check.True(!result.Ok);
    }

    public static void NullQuestionIsRejected()
    {
        var result = AnalysisRequestValidator.Validate(Request(null!));

        Check.True(!result.Ok);
    }

    public static void NullSelectionsAreRejected()
    {
        var result = AnalysisRequestValidator.Validate(
            new AnalysisRequest("Вопрос", null!));

        Check.True(!result.Ok);
    }

    public static void MoreThanTenSelectionsAreRejected()
    {
        var selections = Enumerable.Range(1, 11)
            .Select(id => new EntitySelection($"Сотрудник {id}", id))
            .ToArray();

        var result = AnalysisRequestValidator.Validate(
            new AnalysisRequest("Вопрос", selections));

        Check.True(!result.Ok);
    }

    public static void NonPositiveEmployeeIdIsRejected()
    {
        var result = AnalysisRequestValidator.Validate(
            new AnalysisRequest("Вопрос", [new EntitySelection("Иванов", 0)]));

        Check.True(!result.Ok);
    }

    public static void EmptyMentionIsRejected()
    {
        var result = AnalysisRequestValidator.Validate(
            new AnalysisRequest("Вопрос", [new EntitySelection(" ", 1)]));

        Check.True(!result.Ok);
    }

    private static AnalysisRequest Request(string question) =>
        new(question, Array.Empty<EntitySelection>());

    private enum SampleStatus
    {
        Completed
    }
}
