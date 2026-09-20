#nullable enable

using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public interface IQwenJsonClient
{
    Task<T> CompleteAsync<T>(
        string systemPrompt,
        object input,
        int maxTokens,
        CancellationToken ct);
}
