#nullable enable

using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness;

public interface IAnalysisRunner
{
    Task<AnalysisResponse> RunAsync(AnalysisRequest request, CancellationToken ct);
}
