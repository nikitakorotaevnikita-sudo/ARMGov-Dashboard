using System;
using System.Collections.Generic;
using System.Linq;

internal sealed class SqlQueryResult
{
    public List<string> Cols;
    public List<string> Types;
    public List<object[]> Rows;
    public bool Truncated;
}

internal sealed class ChartKeepResult
{
    public object Dataset;
    public string DatasetError;
    public string[] KeepMissing;
}

// Отбор строк для графика: модель называет подписи (keep), числа берутся
// из уже исполненных SQL-шагов. Искать с конца, чтобы более поздний запрос
// перекрыл ранний по тому же имени.
internal static class SqlChartKeep
{
    const int MaxCols = 12;

    public static ChartKeepResult Build(
        IReadOnlyList<SqlQueryResult> sqlResults,
        string[] columns,
        string[] keep,
        string hint,
        int limit)
    {
        var names = Dedup(keep);
        if (names.Count == 0)
            return new ChartKeepResult { KeepMissing = Array.Empty<string>() };

        var hits = new List<(SqlQueryResult step, object[] row)>();
        var missing = new List<string>();
        foreach (var k in names)
        {
            var hit = Find(sqlResults, columns, k);
            if (hit.row != null) hits.Add(hit);
            else missing.Add(k);
        }

        if (hits.Count == 0)
        {
            return new ChartKeepResult
            {
                DatasetError = "keep: не совпал ни с одной строкой SQL",
                KeepMissing = missing.ToArray()
            };
        }

        var schemaStep = hits[0].step;
        List<string> schemaNames;
        if (columns != null && columns.Length > 0)
            schemaNames = columns.Where(n => !string.IsNullOrWhiteSpace(n)).Take(MaxCols).ToList();
        else
            schemaNames = schemaStep.Cols.Take(MaxCols).ToList();
        if (schemaNames.Count == 0)
        {
            return new ChartKeepResult
            {
                DatasetError = "keep: не совпал ни с одной строкой SQL",
                KeepMissing = names.ToArray()
            };
        }

        var schemaTypes = schemaNames.Select(n =>
        {
            int i = IndexOf(schemaStep.Cols, n);
            return i >= 0 && i < schemaStep.Types.Count ? schemaStep.Types[i] : "text";
        }).ToList();

        var rows = hits.Select(h => Project(h.step, h.row, schemaNames)).ToList();
        var dataset = new
        {
            source = "sql",
            cols = schemaNames.Select((c, i) => new { name = c, title = c, type = schemaTypes[i] }),
            rows,
            rowCount = rows.Count,
            truncated = false,
            hint,
            limit
        };

        string err = missing.Count > 0 ? "keep: не найдены: " + string.Join(", ", missing) : null;
        return new ChartKeepResult
        {
            Dataset = dataset,
            DatasetError = err,
            KeepMissing = missing.ToArray()
        };
    }

    static List<string> Dedup(string[] keep)
    {
        var names = new List<string>();
        var seen = new HashSet<string>();
        if (keep == null) return names;
        foreach (var raw in keep)
        {
            var k = (raw ?? "").Trim();
            if (k.Length == 0 || !seen.Add(k)) continue;
            names.Add(k);
        }
        return names;
    }

    static (SqlQueryResult step, object[] row) Find(
        IReadOnlyList<SqlQueryResult> sqlResults, string[] columns, string k)
    {
        if (sqlResults == null) return (null, null);
        for (int s = sqlResults.Count - 1; s >= 0; s--)
        {
            var step = sqlResults[s];
            if (step == null || step.Cols == null || step.Rows == null) continue;
            int li = LabelColIndex(step, columns);
            if (li < 0) continue;
            foreach (var row in step.Rows)
            {
                if (row == null || li >= row.Length) continue;
                if (CellStr(row[li]) == k) return (step, row);
            }
        }
        return (null, null);
    }

    static int LabelColIndex(SqlQueryResult step, string[] columns)
    {
        bool IsText(int i) => i >= 0 && i < step.Types.Count && step.Types[i] == "text";
        if (columns != null)
        {
            foreach (var name in columns)
            {
                int i = IndexOf(step.Cols, name);
                if (IsText(i)) return i;
            }
        }
        for (int i = 0; i < step.Cols.Count; i++)
            if (IsText(i)) return i;
        return -1;
    }

    static object[] Project(SqlQueryResult step, object[] row, List<string> schemaNames)
    {
        var outRow = new object[schemaNames.Count];
        for (int i = 0; i < schemaNames.Count; i++)
        {
            int j = IndexOf(step.Cols, schemaNames[i]);
            outRow[i] = (j >= 0 && row != null && j < row.Length) ? row[j] : null;
        }
        return outRow;
    }

    static int IndexOf(List<string> cols, string name)
    {
        if (cols == null || name == null) return -1;
        for (int i = 0; i < cols.Count; i++)
            if (cols[i] == name) return i;
        return -1;
    }

    static string CellStr(object v) => v == null ? "" : Convert.ToString(v).Trim();
}
