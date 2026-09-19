#nullable enable



using System;

using System.Collections.Generic;

using System.Linq;

using System.Text.Json;

using System.Text.RegularExpressions;



namespace ArmGov.Harness;



public static class ToolDefinitions

{

    private static readonly string[] DashboardMetricNames =

    [

        "overview", "process", "leaders", "leader_tasks", "stuck",

        "by_kind", "departments", "my_tasks", "appeal_topics"

    ];



    private static readonly HashSet<string> PeriodMetrics = new(StringComparer.Ordinal)

    {

        "overview", "process", "stuck", "by_kind", "departments"

    };



    private static readonly HashSet<string> ProcessKeyMetrics = new(StringComparer.Ordinal)

    {

        "process", "stuck", "by_kind", "departments"

    };



    private static readonly Regex SqlParameterName =

        new("^[a-z][a-z0-9_]{0,31}$", RegexOptions.CultureInvariant);



    private static readonly IReadOnlyList<ToolDefinition> AllTools = BuildAll();



    public static IReadOnlyList<ToolDefinition> All => AllTools;

    private static ValidationResult Success { get; } =
        new(true, Array.Empty<HarnessError>());

    public static ValidationResult Validate(string toolName, JsonElement arguments)

    {

        if (string.IsNullOrWhiteSpace(toolName))

        {

            return Fail(new HarnessError(

                "unknown_function",

                "Tool name is required.",

                false));

        }



        var tool = AllTools.FirstOrDefault(candidate =>

            string.Equals(candidate.Name, toolName, StringComparison.Ordinal));

        if (tool is null)

        {

            return Fail(new HarnessError(

                "unknown_function",

                "Requested an unknown tool.",

                false));

        }



        if (!JsonSchemaValidator.IsValid(arguments, tool.Parameters))

        {

            return Fail(new HarnessError(

                "invalid_function_arguments",

                "Arguments do not match the tool schema.",

                true));

        }



        return toolName switch

        {

            "execute_sql" => ValidateExecuteSql(arguments),

            "dashboard_metric" => ValidateDashboardMetric(arguments),

            _ => Success

        };

    }



    public static ValidationResult ValidateSchemaDefinition(JsonElement schema) =>

        JsonSchemaValidator.ValidateDefinition(schema);



    private static ValidationResult ValidateExecuteSql(JsonElement arguments)

    {

        if (!arguments.TryGetProperty("parameters", out var parameters) ||

            parameters.ValueKind != JsonValueKind.Object)

        {

            return Fail(new HarnessError(

                "invalid_function_arguments",

                "execute_sql parameters must be an object.",

                true));

        }



        foreach (var property in parameters.EnumerateObject())

        {

            if (!SqlParameterName.IsMatch(property.Name))

            {

                return Fail(new HarnessError(

                    "invalid_function_arguments",

                    "execute_sql parameter names must match [a-z][a-z0-9_]{0,31}.",

                    true));

            }



            if (property.Name is "from" or "to" or "asOf")

            {

                return Fail(new HarnessError(

                    "invalid_function_arguments",

                    "from/to/asOf are reserved server parameters.",

                    true));

            }



            if (property.Value.ValueKind is not (

                JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True

                or JsonValueKind.False or JsonValueKind.Null))

            {

                return Fail(new HarnessError(

                    "invalid_function_arguments",

                    "execute_sql parameters must be scalar or null.",

                    true));

            }

        }



        return Success;

    }



    private static ValidationResult ValidateDashboardMetric(JsonElement arguments)

    {

        var name = arguments.GetProperty("name").GetString()!;

        if (!arguments.TryGetProperty("args", out var args) ||

            args.ValueKind != JsonValueKind.Object)

        {

            return Success;

        }



        var allowed = AllowedDashboardArgs(name);

        foreach (var property in args.EnumerateObject())

        {

            if (!allowed.Contains(property.Name))

            {

                return Fail(new HarnessError(

                    "invalid_function_arguments",

                    $"Argument '{property.Name}' is not allowed for dashboard_metric '{name}'.",

                    true));

            }

        }



        if (ProcessKeyMetrics.Contains(name) &&

            args.TryGetProperty("key", out var key) &&

            key.ValueKind == JsonValueKind.String &&

            key.GetString() is not ("poruchenia" or "appeals" or "npa"))

        {

            return Fail(new HarnessError(

                "invalid_function_arguments",

                "dashboard_metric key must be poruchenia, appeals, or npa.",

                true));

        }



        if (string.Equals(name, "leaders", StringComparison.Ordinal) &&

            args.TryGetProperty("by", out var by) &&

            by.ValueKind == JsonValueKind.String &&

            by.GetString() is not ("performer" or "dept" or "bu"))

        {

            return Fail(new HarnessError(

                "invalid_function_arguments",

                "dashboard_metric leaders.by must be performer, dept, or bu.",

                true));

        }



        return Success;

    }



    private static HashSet<string> AllowedDashboardArgs(string name)

    {

        var allowed = new HashSet<string>(StringComparer.Ordinal);

        if (PeriodMetrics.Contains(name))

            allowed.Add("period");

        if (ProcessKeyMetrics.Contains(name))

            allowed.Add("key");

        if (string.Equals(name, "leaders", StringComparison.Ordinal) ||

            string.Equals(name, "leader_tasks", StringComparison.Ordinal))

        {

            allowed.Add("by");

            if (string.Equals(name, "leader_tasks", StringComparison.Ordinal))

                allowed.Add("id");

        }



        return allowed;

    }



    private static ValidationResult Fail(HarnessError error) =>

        new(false, [error]);



    private static IReadOnlyList<ToolDefinition> BuildAll() =>

    [

        Tool("search_catalog", "Поиск по каталогу метрик и таблиц.", """

            {"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":100}},"required":["query"],"additionalProperties":false}

            """),

        Tool("describe_table", "Описание полей разрешённой таблицы.", """

            {"type":"object","properties":{"schema":{"type":"string","minLength":1},"table":{"type":"string","minLength":1},"fields":{"type":"array","items":{"type":"string","minLength":1},"minItems":1}},"required":["schema","table","fields"],"additionalProperties":false}

            """),

        Tool("find_employees", "Поиск сотрудников по токенам ФИО.", """

            {"type":"object","properties":{"tokens":{"type":"array","items":{"type":"string","minLength":1},"minItems":1,"maxItems":5}},"required":["tokens"],"additionalProperties":false}

            """),

        Tool("set_context", "Задать метрику и период анализа.",
            "{\"type\":\"object\",\"properties\":{\"metricId\":{\"type\":\"string\",\"minLength\":1},\"period\":" +
            PeriodSchema + "},\"required\":[\"metricId\",\"period\"],\"additionalProperties\":false}"),

        // GigaChat 422, если type=object без properties (нужен хотя бы {}).
        Tool("dashboard_metric", "Готовая метрика дашборда.",
            "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"enum\":[" + MetricEnum +
            "]},\"args\":{\"type\":\"object\",\"properties\":{},\"additionalProperties\":true}},\"required\":[\"name\",\"args\"],\"additionalProperties\":false}"),

        Tool("execute_sql", "Выполнить проверенный SELECT.", """

            {"type":"object","properties":{"sql":{"type":"string","minLength":1},"parameters":{"type":"object","properties":{},"additionalProperties":true},"metricId":{"type":"string","minLength":1}},"required":["sql","parameters","metricId"],"additionalProperties":false}

            """),

        Tool("read_result", "Прочитать страницу сохранённого результата.", """

            {"type":"object","properties":{"resultId":{"type":"string","minLength":1},"offset":{"type":"integer","minimum":0},"take":{"type":"integer","minimum":1,"maximum":20}},"required":["resultId","offset","take"],"additionalProperties":false}

            """),

        Tool("submit_report", "Предложить проверяемый отчёт.",
            "{\"type\":\"object\",\"properties\":{\"report\":" + ReportSchema +
            "},\"required\":[\"report\"],\"additionalProperties\":false}"),

        Tool("clarify", "Запросить уточнение по кандидатам.", """

            {"type":"object","properties":{"question":{"type":"string","minLength":1},"candidateIds":{"type":"array","items":{"type":"integer"},"minItems":1}},"required":["question","candidateIds"],"additionalProperties":false}

            """)

    ];



    private const string PeriodSchema = """

        {"type":"object","properties":{"kind":{"type":"string","enum":["all","months","range"]},"months":{"type":"integer","minimum":1,"maximum":1200},"from":{"type":"string"},"to":{"type":"string"}},"required":["kind"],"additionalProperties":false}

        """;



    private const string ReportSchema = """

        {"type":"object","properties":{"title":{"type":"string","minLength":1},"interpretation":{"type":"object","properties":{"metricId":{"type":"string","minLength":1},"label":{"type":"string","minLength":1},"unit":{"type":"string","minLength":1},"from":{"type":"string"},"to":{"type":"string"},"dateField":{"type":"string"}},"required":["metricId","label","unit"],"additionalProperties":false},"blocks":{"type":"array","items":{"type":"object","properties":{"kind":{"type":"string","enum":["table","bars","line","shares","kpi"]},"resultId":{"type":"string","minLength":1},"columns":{"type":"array","items":{"type":"string","minLength":1},"minItems":1},"equalsFilter":{"type":"object","properties":{},"additionalProperties":true},"limit":{"type":"integer","minimum":1}},"required":["kind","resultId","columns"],"additionalProperties":false},"minItems":1},"facts":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string","minLength":1},"operation":{"type":"string","enum":["cell","sum","difference","ratio"]},"inputs":{"type":"array","items":{"type":"object","properties":{"resultId":{"type":"string","minLength":1},"row":{"type":"integer","minimum":0},"column":{"type":"string","minLength":1}},"required":["resultId","row","column"],"additionalProperties":false},"minItems":1}},"required":["id","operation","inputs"],"additionalProperties":false}},"textTemplates":{"type":"array","items":{"type":"string","minLength":1},"minItems":1},"commentary":{"type":"string"}},"required":["title","interpretation","blocks","facts","textTemplates"],"additionalProperties":false}

        """;



    private static string MetricEnum => string.Join(

        ",",

        DashboardMetricNames.Select(name => JsonSerializer.Serialize(name)));



    private static ToolDefinition Tool(string name, string description, string schemaJson) =>

        new(name, description, JsonDocument.Parse(schemaJson).RootElement.Clone());



    public static class JsonSchemaValidator

    {

        private static readonly HashSet<string> KnownKeywords = new(StringComparer.Ordinal)

        {

            "type", "properties", "required", "additionalProperties", "items", "enum",

            "minLength", "maxLength", "minimum", "maximum", "minItems", "maxItems"

        };



        public static bool IsValid(JsonElement value, JsonElement schema) =>

            ValidateDefinition(schema).Ok && Matches(value, schema);



        public static ValidationResult ValidateDefinition(JsonElement schema)

        {

            var errors = new List<HarnessError>();

            CollectUnknownKeywords(schema, "$", errors);

            return errors.Count == 0

                ? Success

                : new ValidationResult(false, errors.ToArray());

        }



        private static void CollectUnknownKeywords(

            JsonElement schema,

            string path,

            List<HarnessError> errors)

        {

            if (schema.ValueKind != JsonValueKind.Object)

                return;



            foreach (var property in schema.EnumerateObject())

            {

                if (!KnownKeywords.Contains(property.Name))

                {

                    errors.Add(new HarnessError(

                        "invalid_schema",

                        $"Unknown schema keyword '{property.Name}' at {path}.",

                        false));

                }



                switch (property.Name)

                {

                    case "properties" when property.Value.ValueKind == JsonValueKind.Object:

                        foreach (var nested in property.Value.EnumerateObject())

                            CollectUnknownKeywords(nested.Value, $"{path}.properties.{nested.Name}", errors);

                        break;

                    case "items":

                        CollectUnknownKeywords(property.Value, $"{path}.items", errors);

                        break;

                }

            }

        }



        private static bool Matches(JsonElement value, JsonElement schema)

        {

            if (schema.ValueKind != JsonValueKind.Object)

                return false;



            if (schema.TryGetProperty("enum", out var enumValues) &&

                enumValues.ValueKind == JsonValueKind.Array &&

                !enumValues.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value)))

            {

                return false;

            }



            if (schema.TryGetProperty("type", out var type) &&

                type.ValueKind == JsonValueKind.String &&

                !MatchesType(value, type.GetString()!))

            {

                return false;

            }



            return value.ValueKind switch

            {

                JsonValueKind.Object => ValidObject(value, schema),

                JsonValueKind.Array => ValidArray(value, schema),

                JsonValueKind.String => ValidString(value, schema),

                JsonValueKind.Number => ValidNumber(value, schema),

                _ => true

            };

        }



        private static bool MatchesType(JsonElement value, string type) =>

            type switch

            {

                "object" => value.ValueKind == JsonValueKind.Object,

                "array" => value.ValueKind == JsonValueKind.Array,

                "string" => value.ValueKind == JsonValueKind.String,

                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),

                "number" => value.ValueKind == JsonValueKind.Number,

                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,

                "null" => value.ValueKind == JsonValueKind.Null,

                _ => false

            };



        private static bool ValidObject(JsonElement value, JsonElement schema)

        {

            var properties = schema.TryGetProperty("properties", out var props) &&

                             props.ValueKind == JsonValueKind.Object

                ? props

                : default;



            if (schema.TryGetProperty("required", out var required) &&

                required.ValueKind == JsonValueKind.Array)

            {

                foreach (var item in required.EnumerateArray())

                {

                    if (item.ValueKind != JsonValueKind.String ||

                        !value.TryGetProperty(item.GetString()!, out _))

                    {

                        return false;

                    }

                }

            }



            var rejectExtras =

                schema.TryGetProperty("additionalProperties", out var additional) &&

                additional.ValueKind == JsonValueKind.False;

            foreach (var property in value.EnumerateObject())

            {

                if (properties.ValueKind != JsonValueKind.Object ||

                    !properties.TryGetProperty(property.Name, out var propertySchema))

                {

                    if (rejectExtras)

                        return false;

                    continue;

                }



                if (!Matches(property.Value, propertySchema))

                    return false;

            }



            return true;

        }



        private static bool ValidArray(JsonElement value, JsonElement schema)

        {

            if (schema.TryGetProperty("minItems", out var minItems) &&

                minItems.TryGetInt32(out var min) &&

                value.GetArrayLength() < min)

            {

                return false;

            }



            if (schema.TryGetProperty("maxItems", out var maxItems) &&

                maxItems.TryGetInt32(out var max) &&

                value.GetArrayLength() > max)

            {

                return false;

            }



            if (!schema.TryGetProperty("items", out var items))

                return true;

            return value.EnumerateArray().All(item => Matches(item, items));

        }



        private static bool ValidString(JsonElement value, JsonElement schema)

        {

            var text = value.GetString()!;

            if (schema.TryGetProperty("minLength", out var minLength) &&

                minLength.TryGetInt32(out var min) &&

                text.Length < min)

            {

                return false;

            }



            return !schema.TryGetProperty("maxLength", out var maxLength) ||

                   !maxLength.TryGetInt32(out var max) ||

                   text.Length <= max;

        }



        private static bool ValidNumber(JsonElement value, JsonElement schema)

        {

            if (!value.TryGetDecimal(out var number))

                return false;

            if (schema.TryGetProperty("minimum", out var minimum) &&

                minimum.TryGetDecimal(out var min) &&

                number < min)

            {

                return false;

            }



            return !schema.TryGetProperty("maximum", out var maximum) ||

                   !maximum.TryGetDecimal(out var max) ||

                   number <= max;

        }

    }

}
