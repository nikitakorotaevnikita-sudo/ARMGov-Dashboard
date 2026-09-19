#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using ArmGov.Harness;

public sealed class ScriptedProvider : IModelProvider
{
    private readonly ModelAction[] _script;
    private int _index;

    public ScriptedProvider(params ModelAction[] script) =>
        _script = script ?? throw new ArgumentNullException(nameof(script));

    public int CallCount { get; private set; }
    public List<(ModelAction Action, object Result)> FeedbackLog { get; } = new();

    public Task<ModelAction> NextAsync(
        IReadOnlyList<JsonElement> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct)
    {
        _ = messages;
        _ = tools;
        ct.ThrowIfCancellationRequested();
        CallCount++;
        if (_index >= _script.Length)
            throw new HarnessException(new HarnessError(
                "provider_exhausted",
                "Scripted provider has no more actions.",
                false));

        return Task.FromResult(_script[_index++]);
    }

    public JsonElement Feedback(ModelAction action, object result)
    {
        FeedbackLog.Add((action, result));
        return JsonSerializer.SerializeToElement(new
        {
            role = "user",
            content = JsonSerializer.Serialize(new
            {
                tool = action.Name,
                result
            }, HarnessJson.Options)
        }, HarnessJson.Options);
    }
}
