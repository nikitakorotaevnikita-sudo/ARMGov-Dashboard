#nullable enable

using System.Text.Json;
using ArmGov.Harness;

public static class ToolsTests
{
    public static void AllSchemasSelfValidate()
    {
        foreach (var tool in ToolDefinitions.All)
        {
            var result = ToolDefinitions.ValidateSchemaDefinition(tool.Parameters);
            Check.True(result.Ok);
        }
    }

    public static void UnknownSchemaKeywordFails()
    {
        var schema = JsonDocument.Parse("""
            {"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false,"format":"email"}
            """).RootElement;

        var result = ToolDefinitions.ValidateSchemaDefinition(schema);

        Check.True(!result.Ok);
        Check.Equal("invalid_schema", result.Errors[0].Code);
    }

    public static void ExecuteSqlRequiresMetricId()
    {
        var args = JsonDocument.Parse("""
            {"sql":"select 1","parameters":{},"metricId":""}
            """).RootElement;

        var result = ToolDefinitions.Validate("execute_sql", args);

        Check.True(!result.Ok);
        Check.Equal("invalid_function_arguments", result.Errors[0].Code);
    }

    public static void ExecuteSqlRejectsReservedParameter()
    {
        var args = JsonDocument.Parse("""
            {"sql":"select 1","parameters":{"from":"2024-01-01"},"metricId":"personal_instruction_count"}
            """).RootElement;

        var result = ToolDefinitions.Validate("execute_sql", args);

        Check.True(!result.Ok);
        Check.True(result.Errors[0].Message.Contains("from/to/asOf", StringComparison.Ordinal));
    }

    public static void ExecuteSqlRejectsNonScalarParameter()
    {
        var args = JsonDocument.Parse("""
            {"sql":"select 1","parameters":{"ids":[1,2]},"metricId":"personal_instruction_count"}
            """).RootElement;

        var result = ToolDefinitions.Validate("execute_sql", args);

        Check.True(!result.Ok);
    }

    public static void DashboardMetricRejectsUnknownArg()
    {
        var args = JsonDocument.Parse("""
            {"name":"my_tasks","args":{"period":"month"}}
            """).RootElement;

        var result = ToolDefinitions.Validate("dashboard_metric", args);

        Check.True(!result.Ok);
        Check.Equal("invalid_function_arguments", result.Errors[0].Code);
    }

    public static void DashboardMetricAllowsPeriodForOverview()
    {
        var args = JsonDocument.Parse("""
            {"name":"overview","args":{"period":"month"}}
            """).RootElement;

        var result = ToolDefinitions.Validate("dashboard_metric", args);

        Check.True(result.Ok);
    }

    public static void DashboardMetricRejectsInvalidProcessKey()
    {
        var args = JsonDocument.Parse("""
            {"name":"process","args":{"key":"unknown","period":"month"}}
            """).RootElement;

        var result = ToolDefinitions.Validate("dashboard_metric", args);

        Check.True(!result.Ok);
    }

    public static void UnknownToolIsRejected()
    {
        var args = JsonDocument.Parse("{}").RootElement;

        var result = ToolDefinitions.Validate("drop_database", args);

        Check.True(!result.Ok);
        Check.Equal("unknown_function", result.Errors[0].Code);
    }

    public static void TooDeepJsonIsRejected()
    {
        var deep = new string('[', 40) + "1" + new string(']', 40);
        Check.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<JsonElement>(deep, HarnessJson.Options));
    }

    public static void ClarifyRequiresCandidateIds()
    {
        var args = JsonDocument.Parse("""
            {"question":"Кого выбрать?"}
            """).RootElement;

        var result = ToolDefinitions.Validate("clarify", args);

        Check.True(!result.Ok);
        Check.Equal("invalid_function_arguments", result.Errors[0].Code);
    }
}
