#nullable enable

using System.Text.Json;
using ArmGov.Harness;

public static class ReportsTests
{
    private static readonly Interpretation Meaning =
        new("personal_instruction_count", "Поручения", "поручение", null, null, null);

    public static void MissingEvidenceCannotBecomeReport()
    {
        var store = new ResultStore("a");
        var spec = Spec(
            new[] { new BlockSpec("bars", "r404", new[] { "name", "n" }, null, null) },
            new[] { new FactSpec("ivan", "cell", new[] { new CellRef("r404", 0, "n") }) },
            new[] { "У сотрудника {{ivan}} поручений" });

        Check.True(!ReportValidator.Validate(spec, store, Meaning).Ok);
    }

    public static void RealCellsProduceExactServerFactsAndChartFields()
    {
        var store = Store(
            new[]
            {
                new ColumnSpec("employee_id", "ID", "number"),
                new ColumnSpec("name", "Сотрудник", "string"),
                new ColumnSpec("n", "Количество", "number")
            },
            new[]
            {
                Row(101, "Иванов", 7),
                Row(202, "Петров", 3)
            });
        var spec = Spec(
            new[]
            {
                new BlockSpec(
                    "bars",
                    "r1",
                    new[] { "name", "n" },
                    new Dictionary<string, JsonElement>
                    {
                        ["employee_id"] = JsonSerializer.SerializeToElement(101)
                    },
                    10)
            },
            new[]
            {
                new FactSpec("ivan", "cell", new[] { new CellRef("r1", 0, "n") }),
                new FactSpec("petr", "cell", new[] { new CellRef("r1", 1, "n") }),
                new FactSpec("delta", "difference", new[]
                {
                    new CellRef("r1", 0, "n"),
                    new CellRef("r1", 1, "n")
                }),
                new FactSpec("ratio", "ratio", new[]
                {
                    new CellRef("r1", 0, "n"),
                    new CellRef("r1", 1, "n")
                })
            },
            new[] { "{{ivan}} против {{petr}}, разница {{delta}}, отношение {{ratio}}" });

        var report = ReportRenderer.Render(spec, store, Meaning);

        Check.Equal(7m, report.Facts["ivan"].GetDecimal());
        Check.Equal(3m, report.Facts["petr"].GetDecimal());
        Check.Equal(4m, report.Facts["delta"].GetDecimal());
        Check.Equal(7m / 3m, report.Facts["ratio"].GetDecimal());
        Check.Equal("name", report.Blocks[0].Columns[0]);
        Check.Equal("n", report.Blocks[0].Columns[1]);
        Check.True(report.VerifiedText[0].Contains("7"));
    }

    public static void RejectsUnknownColumnsRowsAndAlteredInterpretation()
    {
        var store = Store(StandardColumns(), new[] { Row("Иванов", 7) });

        Check.True(!Valid(Spec(
            new[] { new BlockSpec("table", "r1", new[] { "missing" }, null, null) },
            CellFact(), Text()), store));
        Check.True(!Valid(Spec(
            Array.Empty<BlockSpec>(),
            new[] { new FactSpec("n", "cell", new[] { new CellRef("r1", 1, "n") }) },
            Text()), store));
        Check.True(!Valid(Spec(
            Array.Empty<BlockSpec>(), CellFact(), Text()) with
            {
                Interpretation = Meaning with { Unit = "миллион" }
            }, store));
    }

    public static void RejectsInvalidBlockShapesFiltersAndDuplicateColumns()
    {
        var store = Store(StandardColumns(), new[] { Row("Иванов", 7) });
        var badTypeFilter = new Dictionary<string, JsonElement>
        {
            ["n"] = JsonSerializer.SerializeToElement("seven")
        };

        Check.True(!Valid(Spec(
            new[] { new BlockSpec("pie", "r1", new[] { "name", "n" }, null, null) },
            CellFact(), Text()), store));
        Check.True(!Valid(Spec(
            new[] { new BlockSpec("bars", "r1", new[] { "name", "name" }, null, null) },
            CellFact(), Text()), store));
        Check.True(!Valid(Spec(
            new[] { new BlockSpec("bars", "r1", new[] { "name", "n" }, badTypeFilter, null) },
            CellFact(), Text()), store));
    }

    public static void RejectsTruncatedFactsAndFullDatasetClaims()
    {
        var truncated = new Truncation(true, false, Array.Empty<string>(), false);
        var store = Store(StandardColumns(), new[] { Row("Иванов", 7) }, truncated);

        Check.True(!Valid(Spec(
            Array.Empty<BlockSpec>(), CellFact(), Text()), store));
        Check.True(!Valid(Spec(
            new[] { new BlockSpec("bars", "r1", new[] { "name", "n" }, null, null) },
            Array.Empty<FactSpec>(), Array.Empty<string>()), store));
        Check.True(Valid(Spec(
            new[] { new BlockSpec("bars", "r1", new[] { "name", "n" }, null, 10) },
            Array.Empty<FactSpec>(), Array.Empty<string>()), store));
        Check.True(!Valid(Spec(
            new[] { new BlockSpec("shares", "r1", new[] { "name", "n" }, null, null) },
            Array.Empty<FactSpec>(), Array.Empty<string>()), store));
    }

    public static void RejectsTruncatedCellMarker()
    {
        var store = Store(
            StandardColumns(),
            new[] { Row("Иванов", 7) },
            new Truncation(false, false, new[] { "0:n" }, false));

        Check.True(!Valid(Spec(Array.Empty<BlockSpec>(), CellFact(), Text()), store));
    }

    public static void RejectsNumericLiteralsUnknownPlaceholdersAndDuplicateFacts()
    {
        var store = Store(StandardColumns(), new[] { Row("Иванов", 7) });

        Check.True(!Valid(Spec(Array.Empty<BlockSpec>(), CellFact(), new[] { "Получено 12 и ８" }), store));
        Check.True(!Valid(Spec(Array.Empty<BlockSpec>(), CellFact(), new[] { "{{unknown}}" }), store));
        Check.True(!Valid(Spec(
            Array.Empty<BlockSpec>(),
            new[]
            {
                new FactSpec("n", "cell", new[] { new CellRef("r1", 0, "n") }),
                new FactSpec("n", "cell", new[] { new CellRef("r1", 0, "n") })
            },
            Text()), store));
    }

    public static void RejectsOverflowButRendersDivisionByZeroAsNull()
    {
        var overflowStore = Store(
            StandardColumns(),
            new[] { Row("Максимум", decimal.MaxValue), Row("Минус", -1m) });
        var overflow = Spec(
            Array.Empty<BlockSpec>(),
            new[] { new FactSpec("n", "difference", new[]
            {
                new CellRef("r1", 0, "n"),
                new CellRef("r1", 1, "n")
            }) },
            Text());
        Check.True(!Valid(overflow, overflowStore));

        var zeroStore = Store(StandardColumns(), new[] { Row("Иванов", 7), Row("Ноль", 0) });
        var division = Spec(
            Array.Empty<BlockSpec>(),
            new[] { new FactSpec("n", "ratio", new[]
            {
                new CellRef("r1", 0, "n"),
                new CellRef("r1", 1, "n")
            }) },
            Text());
        var report = ReportRenderer.Render(division, zeroStore, Meaning);
        Check.Equal(JsonValueKind.Null, report.Facts["n"].ValueKind);
    }

    public static void ValidatesSharesKpiAndLineSemantics()
    {
        var negative = Store(StandardColumns(), new[] { Row("Иванов", -1) });
        Check.True(!Valid(Spec(
            new[] { new BlockSpec("shares", "r1", new[] { "name", "n" }, null, null) },
            Array.Empty<FactSpec>(), Array.Empty<string>()), negative));

        var twoRows = Store(StandardColumns(), new[] { Row("Иванов", 7), Row("Петров", 3) });
        Check.True(!Valid(Spec(
            new[] { new BlockSpec("kpi", "r1", new[] { "n" }, null, null) },
            Array.Empty<FactSpec>(), Array.Empty<string>()), twoRows));

        var lineStore = Store(
            new[]
            {
                new ColumnSpec("day", "Дата", "date"),
                new ColumnSpec("n", "Количество", "number")
            },
            new[] { Row("2026-09-19", 7) });
        Check.True(Valid(Spec(
            new[] { new BlockSpec("line", "r1", new[] { "day", "n" }, null, null) },
            Array.Empty<FactSpec>(), Array.Empty<string>()), lineStore));
    }

    public static void EscapesModelTextAndLabelsCommentary()
    {
        var store = Store(StandardColumns(), new[] { Row("<img>", 7) });
        var spec = Spec(Array.Empty<BlockSpec>(), CellFact(), new[] { "<b>{{n}}</b>" }) with
        {
            Title = "<script>alert</script>",
            Commentary = "<img src=x>"
        };

        var report = ReportRenderer.Render(spec, store, Meaning);

        Check.True(!report.Title.Contains("<script>"));
        Check.True(!report.VerifiedText[0].Contains("<b>"));
        Check.True(report.Commentary!.StartsWith("Комментарий модели, не проверен:"));
        Check.True(!report.Commentary.Contains("<img"));
    }

    private static bool Valid(ReportSpec spec, ResultStore store) =>
        ReportValidator.Validate(spec, store, Meaning).Ok;

    private static ReportSpec Spec(
        BlockSpec[] blocks,
        FactSpec[] facts,
        string[] text) =>
        new("Сравнение", Meaning, blocks, facts, text, null);

    private static FactSpec[] CellFact() =>
        new[] { new FactSpec("n", "cell", new[] { new CellRef("r1", 0, "n") }) };

    private static string[] Text() => new[] { "Получено {{n}}" };

    private static ColumnSpec[] StandardColumns() =>
        new[]
        {
            new ColumnSpec("name", "Сотрудник", "string"),
            new ColumnSpec("n", "Количество", "number")
        };

    private static ResultStore Store(
        ColumnSpec[] columns,
        JsonElement[][] rows,
        Truncation? truncation = null)
    {
        var store = new ResultStore("run-a");
        store.Add(new QueryResult(
            "fixture",
            columns,
            rows,
            "select fixture",
            DateTimeOffset.Parse("2026-09-19T12:00:00+00:00"),
            truncation ?? new Truncation(false, false, Array.Empty<string>(), false),
            Array.Empty<string>()));
        return store;
    }

    private static JsonElement[] Row(params object[] values) =>
        values.Select(value => JsonSerializer.SerializeToElement(value, value.GetType())).ToArray();
}
