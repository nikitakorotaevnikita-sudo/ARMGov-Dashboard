#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public interface IModelProvider
{
    Task<ModelAction> NextAsync(
        IReadOnlyList<JsonElement> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct);

    JsonElement Feedback(ModelAction action, object result);
}
