#nullable enable

using System.Threading;

namespace ArmGov.Harness;

public static class HarnessSqlSlots
{
    public static SemaphoreSlim Instance { get; } = new(2, 2);
}
