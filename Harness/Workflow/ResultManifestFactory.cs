#nullable enable

using System;
using System.Linq;
using System.Text.Json;

namespace ArmGov.Harness;

public static class ResultManifestFactory
{
    public const int MaxRowsPerResult = 50;

    public static ResultManifest[] Create(ResultStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        return store.All()
            .Select(CreateManifest)
            .ToArray();
    }

    private static ResultManifest CreateManifest(StoredResult stored)
    {
        var data = stored.Data;
        return new ResultManifest(
            stored.ResultId,
            data.Columns.Select(column => column with { }).ToArray(),
            data.Rows.Take(MaxRowsPerResult)
                .Select(CloneRow)
                .ToArray(),
            data.Rows.Length,
            new Truncation(
                data.Truncation.Rows,
                data.Truncation.Columns,
                data.Truncation.Cells.ToArray(),
                data.Truncation.Bytes));
    }

    private static JsonElement[] CloneRow(JsonElement[] row) =>
        row.Select(cell => cell.Clone()).ToArray();
}
