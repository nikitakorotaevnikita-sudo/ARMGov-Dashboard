#nullable enable

using System;

namespace ArmGov.Harness.Hosting;

public static class LocalAccess
{
    public static bool IsLocalHostName(string? hostName)
    {
        var h = hostName ?? "";
        return h.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)
            || h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("127.0.0.1:", StringComparison.Ordinal)
            || h.Equals("127.0.0.1", StringComparison.Ordinal)
            || h.StartsWith("[::1]:", StringComparison.Ordinal)
            || h.Equals("[::1]", StringComparison.Ordinal);
    }

    public static bool IsSameOrigin(string? origin, string? hostName)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return true;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            return false;

        var host = hostName ?? "";
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        var expected = uri.Host + port;
        return string.Equals(expected, host, StringComparison.OrdinalIgnoreCase)
            || (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                host.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase))
            || (uri.Host.Equals("127.0.0.1", StringComparison.Ordinal) &&
                host.StartsWith("127.0.0.1:", StringComparison.Ordinal))
            || (uri.Host.Equals("[::1]", StringComparison.Ordinal) &&
                host.StartsWith("[::1]:", StringComparison.Ordinal));
    }
}
