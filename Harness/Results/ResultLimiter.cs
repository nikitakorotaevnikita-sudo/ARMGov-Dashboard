#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ArmGov.Harness;

public static class ResultLimiter
{
    private const int MaxRows = 200;
    private const int MaxColumns = 60;
    private const int MaxStringLength = 200;
    private const int MaxArrayLength = 50;

    public static QueryResult Limit(QueryResult result, int byteLimit = 1048576)
    {
        if (byteLimit <= 0)
            throw Error("invalid_result_limit", "Лимит результата должен быть положительным.");

        var columns = result.Columns.Take(MaxColumns)
            .Select(column => column with { })
            .ToArray();
        var rows = new List<JsonElement[]>();
        var changedCells = new HashSet<string>(
            result.Truncation.Cells ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        var rowCount = Math.Min(result.Rows.Length, MaxRows);

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var sourceRow = result.Rows[rowIndex] ?? Array.Empty<JsonElement>();
            var cellCount = Math.Min(sourceRow.Length, columns.Length);
            var row = new JsonElement[cellCount];
            for (var columnIndex = 0; columnIndex < cellCount; columnIndex++)
            {
                row[columnIndex] = LimitCell(sourceRow[columnIndex], out var changed);
                if (changed)
                    changedCells.Add($"{rowIndex}:{columns[columnIndex].Name}");
            }
            rows.Add(row);
        }

        var structurallyLimited = new QueryResult(
            result.Source,
            columns,
            rows.ToArray(),
            result.EffectiveSql,
            result.RetrievedAt,
            new Truncation(
                result.Truncation.Rows || result.Rows.Length > MaxRows,
                result.Truncation.Columns || result.Columns.Length > MaxColumns,
                changedCells.ToArray(),
                result.Truncation.Bytes),
            (result.Warnings ?? Array.Empty<string>()).ToArray());

        if (Size(structurallyLimited) <= byteLimit)
            return Clone(structurallyLimited);

        var retainedRows = new List<JsonElement[]>();
        var bytesTruncation = structurallyLimited.Truncation with { Bytes = true };
        var empty = structurallyLimited with
        {
            Rows = Array.Empty<JsonElement[]>(),
            Truncation = bytesTruncation
        };
        if (Size(empty) > byteLimit)
            throw Error(
                "result_metadata_too_large",
                "Метаданные результата превышают допустимый размер.");

        foreach (var row in structurallyLimited.Rows)
        {
            var candidateRows = retainedRows.Append(row).ToArray();
            var candidate = structurallyLimited with
            {
                Rows = candidateRows,
                Truncation = bytesTruncation
            };
            if (Size(candidate) > byteLimit)
                break;
            retainedRows.Add(row);
        }

        return Clone(structurallyLimited with
        {
            Rows = retainedRows.ToArray(),
            Truncation = bytesTruncation
        });
    }

    internal static QueryResult Clone(QueryResult result)
    {
        return new QueryResult(
            result.Source,
            result.Columns.Select(column => column with { }).ToArray(),
            result.Rows.Select(row => row.Select(CloneElement).ToArray()).ToArray(),
            result.EffectiveSql,
            result.RetrievedAt,
            new Truncation(
                result.Truncation.Rows,
                result.Truncation.Columns,
                (result.Truncation.Cells ?? Array.Empty<string>()).ToArray(),
                result.Truncation.Bytes),
            (result.Warnings ?? Array.Empty<string>()).ToArray());
    }

    internal static int Size(QueryResult result)
    {
        return JsonSerializer.SerializeToUtf8Bytes(result, HarnessJson.Options).Length;
    }

    private static JsonElement LimitCell(JsonElement cell, out bool changed)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            changed = WriteLimited(writer, cell);
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static bool WriteLimited(Utf8JsonWriter writer, JsonElement element)
    {
        var changed = false;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                if (value.Length > MaxStringLength)
                {
                    value = value[..MaxStringLength];
                    changed = true;
                }
                writer.WriteStringValue(value);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index++ >= MaxArrayLength)
                    {
                        changed = true;
                        break;
                    }
                    changed |= WriteLimited(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    changed |= WriteLimited(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
        return changed;
    }

    private static JsonElement CloneElement(JsonElement element)
    {
        using var document = JsonDocument.Parse(element.GetRawText());
        return document.RootElement.Clone();
    }

    private static HarnessException Error(string code, string message)
    {
        return new HarnessException(new HarnessError(code, message, false));
    }
}
