#nullable enable

using System.Text.Json;
using ArmGov.Harness;

public static class ResultsTests
{
    public static void PreviousResultRemainsAddressable()
    {
        var store = new ResultStore("run-a");
        var data = Result(
            new[] { new ColumnSpec("n", "Количество", "number") },
            new[] { new[] { JsonSerializer.SerializeToElement(7) } });

        var first = store.Add(data);
        var second = store.Add(data with { Source = "tool:leaders" });

        Check.Equal("r1", first.ResultId);
        Check.Equal("r2", second.ResultId);
        Check.Equal(7, store.Get("run-a", first.ResultId).Data.Rows[0][0].GetInt32());
        Check.Throws<HarnessException>(() => store.Get("run-b", first.ResultId));
        Check.Throws<HarnessException>(() => store.Get("run-a", "r404"));
    }

    public static void LimitsRowsColumnsAndCellsSeparately()
    {
        var columns = Enumerable.Range(0, 61)
            .Select(i => new ColumnSpec($"c{i}", $"Колонка {i}", "string"))
            .ToArray();
        var rows = Enumerable.Range(0, 201)
            .Select(row => Enumerable.Range(0, 61)
                .Select(column => JsonSerializer.SerializeToElement(
                    row == 0 && column == 0
                        ? new string('я', 201)
                        : $"r{row}c{column}"))
                .ToArray())
            .ToArray();

        var limited = ResultLimiter.Limit(Result(columns, rows));

        Check.Equal(200, limited.Rows.Length);
        Check.Equal(60, limited.Columns.Length);
        Check.Equal(60, limited.Rows[0].Length);
        Check.Equal(200, limited.Rows[0][0].GetString()!.Length);
        Check.True(limited.Truncation.Rows);
        Check.True(limited.Truncation.Columns);
        Check.True(limited.Truncation.Cells.Contains("0:c0"));
        Check.True(!limited.Truncation.Bytes);
    }

    public static void LimitsNestedArraysAndMarksCell()
    {
        var nested = JsonSerializer.SerializeToElement(new
        {
            items = Enumerable.Range(0, 51).ToArray()
        });
        var limited = ResultLimiter.Limit(Result(
            new[] { new ColumnSpec("payload", "Данные", "string") },
            new[] { new[] { nested } }));

        var items = limited.Rows[0][0].GetProperty("items");
        Check.Equal(50, items.GetArrayLength());
        Check.True(limited.Truncation.Cells.Contains("0:payload"));
    }

    public static void ByteLimitKeepsOnlyWholeRows()
    {
        var result = Result(
            new[] { new ColumnSpec("text", "Текст", "string") },
            Enumerable.Range(0, 20)
                .Select(i => new[] { JsonSerializer.SerializeToElement($"{i}:{new string('x', 80)}") })
                .ToArray());

        var limited = ResultLimiter.Limit(result, 512);

        Check.True(limited.Rows.Length > 0);
        Check.True(limited.Rows.Length < result.Rows.Length);
        Check.True(limited.Truncation.Bytes);
        Check.True(!limited.Truncation.Rows);
        Check.True(JsonSerializer.SerializeToUtf8Bytes(limited, HarnessJson.Options).Length <= 512);
    }

    public static void OversizedMetadataIsRejected()
    {
        var result = Result(
            new[] { new ColumnSpec("n", new string('x', 600), "number") },
            Array.Empty<JsonElement[]>());

        try
        {
            ResultLimiter.Limit(result, 512);
            throw new InvalidOperationException("Expected metadata error.");
        }
        catch (HarnessException ex)
        {
            Check.Equal("result_metadata_too_large", ex.Error.Code);
        }
    }

    public static void RunBudgetIsCheckedBeforeRegistration()
    {
        var store = new ResultStore("run-a", 1024);
        var result = Result(
            new[] { new ColumnSpec("text", "Текст", "string") },
            new[] { new[] { JsonSerializer.SerializeToElement(new string('x', 350)) } });

        var first = store.Add(result);
        var successfulAdds = 1;
        try
        {
            while (true)
            {
                store.Add(result);
                successfulAdds++;
            }
        }
        catch (HarnessException ex)
        {
            Check.Equal("run_result_budget_exceeded", ex.Error.Code);
        }

        Check.Equal("r1", first.ResultId);
        Check.Equal(successfulAdds, store.All().Length);
        Check.True(successfulAdds > 0);
    }

    public static void PagingValidatesBoundariesAndClonesData()
    {
        var store = new ResultStore("run-a");
        store.Add(Result(
            new[] { new ColumnSpec("n", "N", "number") },
            Enumerable.Range(0, 25)
                .Select(i => new[] { JsonSerializer.SerializeToElement(i) })
                .ToArray()));

        var first = store.Page("run-a", "r1");
        var last = store.Page("run-a", "r1", 20, 20);
        Check.Equal(20, first.Rows.Length);
        Check.True(first.HasMore);
        Check.Equal(5, last.Rows.Length);
        Check.True(!last.HasMore);
        Check.Equal(25, last.StoredRowCount);
        Check.Throws<HarnessException>(() => store.Page("run-a", "r1", -1, 20));
        Check.Throws<HarnessException>(() => store.Page("run-a", "r1", 0, 0));
        Check.Throws<HarnessException>(() => store.Page("run-a", "r1", 0, 21));

        first.Columns[0] = new ColumnSpec("changed", "Changed", "string");
        first.Rows[0][0] = JsonSerializer.SerializeToElement(999);
        var reread = store.Page("run-a", "r1");
        Check.Equal("n", reread.Columns[0].Name);
        Check.Equal(0, reread.Rows[0][0].GetInt32());
    }

    public static void EmptyRowsAndExactNumbersSurvive()
    {
        const long large = 9007199254740993L;
        const decimal exact = 12345678901234567890.123456789m;
        var empty = ResultLimiter.Limit(Result(
            new[] { new ColumnSpec("n", "N", "number") },
            Array.Empty<JsonElement[]>()));
        Check.Equal(0, empty.Rows.Length);

        var limited = ResultLimiter.Limit(Result(
            new[]
            {
                new ColumnSpec("large", "Large", "number"),
                new ColumnSpec("exact", "Exact", "number")
            },
            new[]
            {
                new[]
                {
                    JsonSerializer.SerializeToElement(large),
                    JsonSerializer.SerializeToElement(exact)
                }
            }));
        Check.Equal(large, limited.Rows[0][0].GetInt64());
        Check.Equal(exact, limited.Rows[0][1].GetDecimal());
    }

    public static void AddDoesNotRetainMutableInputsOrExposeStoredArrays()
    {
        var columns = new[] { new ColumnSpec("n", "N", "number") };
        var row = new[] { JsonSerializer.SerializeToElement(7) };
        var rows = new[] { row };
        var cells = Array.Empty<string>();
        var warnings = new[] { "original" };
        var source = Result(columns, rows) with
        {
            Truncation = new Truncation(false, false, cells, false),
            Warnings = warnings
        };
        var store = new ResultStore("run-a");

        store.Add(source);
        columns[0] = new ColumnSpec("changed", "Changed", "string");
        row[0] = JsonSerializer.SerializeToElement(999);
        warnings[0] = "changed";

        var read = store.Get("run-a", "r1");
        Check.Equal("n", read.Data.Columns[0].Name);
        Check.Equal(7, read.Data.Rows[0][0].GetInt32());
        Check.Equal("original", read.Data.Warnings[0]);

        read.Data.Rows[0][0] = JsonSerializer.SerializeToElement(1000);
        Check.Equal(7, store.Get("run-a", "r1").Data.Rows[0][0].GetInt32());
    }

    private static QueryResult Result(ColumnSpec[] columns, JsonElement[][] rows)
    {
        return new QueryResult(
            "sql",
            columns,
            rows,
            "select fixture",
            DateTimeOffset.Parse("2026-09-19T12:00:00+00:00"),
            new Truncation(false, false, Array.Empty<string>(), false),
            Array.Empty<string>());
    }
}
