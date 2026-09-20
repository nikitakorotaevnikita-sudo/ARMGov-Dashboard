#nullable enable

using System;

namespace ArmGov.Harness;

public static class JsonObjectExtractor
{
    public static string Extract(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalized = RemoveOptionalFence(text);
        if (normalized.Length == 0 || normalized[0] != '{')
            throw ProtocolError();

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = 0; index < normalized.Length; index++)
        {
            var character = normalized[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
                continue;

            if (character == '{')
            {
                depth++;
                continue;
            }

            if (character != '}')
                continue;

            depth--;
            if (depth < 0)
                throw ProtocolError();

            if (depth == 0)
            {
                if (!string.IsNullOrWhiteSpace(normalized[(index + 1)..]))
                    throw ProtocolError();

                return normalized[..(index + 1)];
            }
        }

        throw ProtocolError();
    }

    private static string RemoveOptionalFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var openingEnd = trimmed.IndexOf('\n');
        if (openingEnd < 0 || !trimmed.EndsWith("```", StringComparison.Ordinal))
            throw ProtocolError();

        var body = trimmed[(openingEnd + 1)..^3];
        return body.Trim();
    }

    private static HarnessException ProtocolError() =>
        new(new HarnessError(
            "provider_protocol_error",
            "Qwen response must contain exactly one JSON object.",
            true));
}
