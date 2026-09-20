using Microsoft.Agents.AI.Workflows;

public static class WorkflowRuntimeTests
{
    public static async Task SequentialWorkflowReturnsTerminalExecutorData()
    {
        var upper = ((Func<string, string>)(value => value.ToUpperInvariant()))
            .BindAsExecutor("upper");
        var suffix = ((Func<string, string>)(value => value + "-OK"))
            .BindAsExecutor("suffix");
        var builder = new WorkflowBuilder(upper);
        builder.AddEdge(upper, suffix).WithOutputFrom(suffix);

        await using Run run = await InProcessExecution.RunAsync(
            builder.Build(),
            "qwen");

        var completed = run.NewEvents
            .OfType<ExecutorCompletedEvent>()
            .Single(item => item.ExecutorId == "suffix");
        Check.Equal("QWEN-OK", completed.Data as string);
    }
}
