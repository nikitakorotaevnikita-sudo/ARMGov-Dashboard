#nullable enable

namespace ArmGov.Harness;

public static class WorkflowLimits
{
    public const int MaxModelCalls = 5;
    public const int MaxSqlRepairs = 1;
    public const int MaxReportRepairs = 1;
    public const int MaxEmployeeMentions = 10;
    public const int MaxRelationHints = 12;
    public const int MaxSqlLength = 20_000;
}
