#nullable enable

using System.Text.Json;

public static class ChartKeepTests
{
    public static void KeepMergesPeopleFromTwoSqlStepsAndDropsLookalikes()
    {
        var sql1 = Step(
            ["name", "completed"],
            ["text", "number"],
            Row("Босов Александр", 972L));
        var sql2 = Step(
            ["name", "completed"],
            ["text", "number"],
            Row("Иванов Иван Иванович", 51730L),
            Row("Концева Надежда Ивановна", 5061L));

        var result = SqlChartKeep.Build(
            [sql1, sql2],
            ["name", "completed"],
            ["Иванов Иван Иванович", "Босов Александр"],
            "bars",
            0);

        Check.True(result.Dataset != null);
        Check.True(string.IsNullOrEmpty(result.DatasetError));
        Check.Equal(0, result.KeepMissing.Length);
        var ds = Json(result.Dataset);
        Check.Equal(2, ds.GetProperty("rowCount").GetInt32());
        var rows = ds.GetProperty("rows").EnumerateArray().ToArray();
        Check.Equal("Иванов Иван Иванович", rows[0][0].GetString());
        Check.Equal(51730, rows[0][1].GetInt64());
        Check.Equal("Босов Александр", rows[1][0].GetString());
        Check.Equal(972, rows[1][1].GetInt64());
        Check.True(JsonSerializer.Serialize(result.Dataset).IndexOf("Концева", StringComparison.Ordinal) < 0);
    }

    public static void EmptyKeepIsNotThisBuilder()
    {
        var sql = Step(
            ["name", "completed"],
            ["text", "number"],
            Row("A", 1L), Row("B", 2L));
        var result = SqlChartKeep.Build(
            [sql], ["name", "completed"], [], "bars", 0);
        Check.True(result.Dataset == null);
        Check.True(result.DatasetError == null);
    }

    public static void PartialKeepDrawsFoundAndListsMissing()
    {
        var sql = Step(
            ["name", "completed"],
            ["text", "number"],
            Row("Босов Александр", 972L));
        var result = SqlChartKeep.Build(
            [sql],
            ["name", "completed"],
            ["Иванов Иван Иванович", "Босов Александр"],
            null,
            0);
        Check.True(result.Dataset != null);
        Check.True(result.DatasetError != null && result.DatasetError.Contains("Иванов Иван Иванович"));
        Check.Equal(1, result.KeepMissing.Length);
        var ds = Json(result.Dataset);
        Check.Equal(1, ds.GetProperty("rowCount").GetInt32());
        Check.Equal("Босов Александр", ds.GetProperty("rows")[0][0].GetString());
    }

    public static void AllKeepMissingYieldsNoDataset()
    {
        var sql = Step(
            ["name", "completed"],
            ["text", "number"],
            Row("Концева Надежда Ивановна", 1L));
        var result = SqlChartKeep.Build(
            [sql], ["name", "completed"], ["Иванов Иван Иванович"], null, 0);
        Check.True(result.Dataset == null);
        Check.True(result.DatasetError != null);
        Check.Equal(1, result.KeepMissing.Length);
    }

    public static void NoSqlStepsYieldsNoDataset()
    {
        var result = SqlChartKeep.Build(
            [], ["name", "completed"], ["Босов Александр"], null, 0);
        Check.True(result.Dataset == null);
        Check.True(result.DatasetError != null);
    }

    public static void LaterSqlWinsOnSameName()
    {
        var sql1 = Step(["name", "completed"], ["text", "number"], Row("Босов Александр", 10L));
        var sql2 = Step(["name", "completed"], ["text", "number"], Row("Босов Александр", 972L));
        var result = SqlChartKeep.Build(
            [sql1, sql2], ["name", "completed"], ["Босов Александр"], null, 0);
        var ds = Json(result.Dataset!);
        Check.Equal(972, ds.GetProperty("rows")[0][1].GetInt64());
    }

    public static void DuplicateKeepKeepsFirst()
    {
        var sql = Step(["name", "completed"], ["text", "number"], Row("Босов Александр", 972L));
        var result = SqlChartKeep.Build(
            [sql], ["name", "completed"],
            ["Босов Александр", "Босов Александр"], null, 0);
        Check.Equal(1, Json(result.Dataset!).GetProperty("rowCount").GetInt32());
    }

    public static void IlikeLookalikeDoesNotMatch()
    {
        var sql = Step(
            ["name", "completed"],
            ["text", "number"],
            Row("Концева Надежда Ивановна", 5061L));
        var result = SqlChartKeep.Build(
            [sql], ["name", "completed"], ["Иванов"], null, 0);
        Check.True(result.Dataset == null);
    }

    static SqlQueryResult Step(string[] cols, string[] types, params object[][] rows) =>
        new() { Cols = [.. cols], Types = [.. types], Rows = [.. rows], Truncated = false };

    static object[] Row(string name, long completed) => [name, completed];

    static JsonElement Json(object dataset)
    {
        var json = JsonSerializer.Serialize(dataset);
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
