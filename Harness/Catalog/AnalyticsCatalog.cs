#nullable enable

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ArmGov.Harness;

public sealed record MetricDefinition(
    string Id,
    string Unit,
    string DateField,
    string Definition);

public sealed class AnalyticsCatalog
{
    private const string GenericMetricId = "generic_query";
    private const string GenericMetricDefinition =
        "Произвольный SELECT-анализ; используйте, если ни одна метрика каталога не подходит.";
    private static readonly FrozenDictionary<string, string> DashboardMetricMappings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["execution_discipline"] = "execution_discipline"
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly FrozenDictionary<string, CatalogRelation> _relations;
    private readonly FrozenDictionary<string, MetricDefinition> _metrics;
    private readonly CatalogRelationship[] _relationships;

    private AnalyticsCatalog(CatalogDocument document)
    {
        if (document.Version <= 0)
            throw new InvalidDataException("Catalog version must be positive.");

        _relations = document.Relations
            .ToFrozenDictionary(
                relation => relation.QualifiedName,
                StringComparer.OrdinalIgnoreCase);
        _metrics = document.Metrics
            .ToFrozenDictionary(metric => metric.Id, StringComparer.OrdinalIgnoreCase);
        _relationships = document.Relationships.ToArray();
        AllowedRelations = _relations.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> AllowedRelations { get; }

    public static AnalyticsCatalog Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Catalog path is required.", nameof(path));

        using var stream = File.OpenRead(Path.GetFullPath(path));
        var document = JsonSerializer.Deserialize<CatalogDocument>(
            stream,
            HarnessJson.Options)
            ?? throw new InvalidDataException("Catalog is empty.");
        return new AnalyticsCatalog(document);
    }

    public MetricDefinition GetMetric(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !_metrics.TryGetValue(id, out var metric))
            throw new KeyNotFoundException($"Unknown metric: {id}");
        return metric;
    }

    public MetricSummary[] GetPlanningMetricSummaries()
    {
        var summaries = _metrics.Values
            .Select(metric => new MetricSummary(
                metric.Id,
                metric.Definition,
                DashboardMetricMappings.GetValueOrDefault(metric.Id)))
            .ToList();
        if (!_metrics.ContainsKey(GenericMetricId))
            summaries.Add(new MetricSummary(GenericMetricId, GenericMetricDefinition, null));
        return summaries
            .OrderBy(metric => metric.MetricId, StringComparer.Ordinal)
            .ToArray();
    }

    public PlanningRelationSummary[] GetPlanningRelationSummaries() =>
        _relations.Values
            .OrderBy(relation => relation.QualifiedName, StringComparer.Ordinal)
            .Select(relation => new PlanningRelationSummary(
                relation.QualifiedName,
                relation.Title,
                relation.Description))
            .ToArray();

    public CatalogProjection CreateProjection(string metricId, IEnumerable<string> relationHints)
    {
        var requested = new HashSet<string>(relationHints ?? Array.Empty<string>(), StringComparer.Ordinal);
        var relations = requested
            .Where(name => _relations.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                var relation = _relations[name];
                return new CatalogRelationProjection(
                    relation.QualifiedName,
                    relation.Description,
                    relation.Fields.Select(field => new CatalogFieldProjection(
                        field.Name, field.Type, field.Description)).ToArray());
            })
            .ToArray();
        var included = relations.Select(relation => relation.Name).ToHashSet(StringComparer.Ordinal);
        var relationships = _relationships
            .Where(link => included.Contains(RelationName(link.From)) && included.Contains(RelationName(link.To)))
            .Select(link => new CatalogRelationshipProjection(link.From, link.To, link.Direction, link.Cardinality))
            .ToArray();
        var metric = _metrics.TryGetValue(metricId, out var known)
            ? known
            : new MetricDefinition(GenericMetricId, "строка результата", "", "Произвольный SELECT-анализ.");
        return new CatalogProjection(relations, relationships, metric);
    }

    public JsonElement Search(string query, int take = 10)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 100)
            throw new ArgumentException("Search query must contain 1 to 100 characters.", nameof(query));
        if (take is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(take), "Take must be between 1 and 20.");

        var tokens = SignificantSearchTokens(query);

        var metricHits = _metrics.Values
            .Select(metric => new
            {
                Metric = metric,
                Score = TokenScore(
                    string.Join(" ", metric.Id, metric.Unit, metric.DateField, metric.Definition),
                    tokens)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Metric.Id, StringComparer.Ordinal)
            .Select(item => (object)new
            {
                kind = "metric",
                id = item.Metric.Id,
                unit = item.Metric.Unit,
                dateField = item.Metric.DateField,
                definition = item.Metric.Definition
            });

        // Если метрик нет — отдаём generic_query, чтобы модель не выдумывала metricId.
        var metricList = metricHits.ToList();
        if (metricList.Count == 0)
        {
            metricList.Add(new
            {
                kind = "metric",
                id = GenericMetricId,
                unit = "строка результата",
                dateField = (string?)null,
                definition =
                    GenericMetricDefinition
            });
        }

        var relationHits = _relations.Values
            .Select(relation => new
            {
                Relation = relation,
                Score = TokenScore(
                    string.Join(
                        " ",
                        relation.QualifiedName,
                        relation.Title,
                        relation.Description,
                        string.Join(" ", relation.Fields.Select(
                            field => $"{field.Name} {field.Description}"))),
                    tokens)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Relation.QualifiedName, StringComparer.Ordinal)
            .Select(item => (object)new
            {
                kind = "relation",
                relation = item.Relation.QualifiedName,
                title = item.Relation.Title,
                description = item.Relation.Description
            });

        var matches = metricList
            .Concat(relationHits)
            .Take(take)
            .ToArray();

        return JsonSerializer.SerializeToElement(matches, HarnessJson.Options);
    }

    public JsonElement Describe(string schema, string table, string[] fields)
    {
        var qualifiedName = $"{schema}.{table}";
        if (!_relations.TryGetValue(qualifiedName, out var relation))
            return SchemaMismatch(qualifiedName, fields ?? Array.Empty<string>());
        if (fields is null || fields.Length == 0)
            return SchemaMismatch(qualifiedName, Array.Empty<string>());

        var allowedFields = relation.Fields.ToFrozenDictionary(
            field => field.Name,
            StringComparer.OrdinalIgnoreCase);
        var missing = fields
            .Where(field => string.IsNullOrWhiteSpace(field) || !allowedFields.ContainsKey(field))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
            return SchemaMismatch(qualifiedName, missing);

        var selected = fields.Select(field => allowedFields[field]).Select(field => new
        {
            field.Name,
            field.Type,
            field.Description,
            examples = field.Examples ?? Array.Empty<string>()
        });
        var relationships = _relationships
            .Where(link =>
                link.From.StartsWith(qualifiedName + ".", StringComparison.OrdinalIgnoreCase) ||
                link.To.StartsWith(qualifiedName + ".", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return JsonSerializer.SerializeToElement(new
        {
            relation = qualifiedName,
            fields = selected,
            relationships
        }, HarnessJson.Options);
    }

    private static readonly HashSet<string> SearchStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "покажи", "дай", "статистику", "статистика", "по", "за", "в", "и", "на", "для",
        "про", "как", "что", "кто", "где", "сравни", "нужна", "нужен", "месяц", "месяца",
        "месяцев", "год", "года", "лет", "квартал", "топ", "the", "a", "an"
    };

    private static string[] SignificantSearchTokens(string query)
    {
        var raw = query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var significant = raw
            .Where(token =>
                token.Length > 1 &&
                !token.All(char.IsDigit) &&
                !SearchStopwords.Contains(token))
            .Take(5)
            .ToArray();
        if (significant.Length > 0)
            return significant;
        return raw.Take(5).ToArray();
    }

    private static int TokenScore(string searchable, string[] tokens)
    {
        if (tokens.Length == 0)
            return 0;
        var score = 0;
        foreach (var token in tokens)
        {
            if (searchable.Contains(token, StringComparison.OrdinalIgnoreCase))
                score++;
        }
        return score;
    }

    private static string RelationName(string fieldReference)
    {
        var separator = fieldReference.LastIndexOf('.');
        return separator > 0 ? fieldReference[..separator] : string.Empty;
    }

    private static JsonElement SchemaMismatch(string relation, string[] fields) =>
        JsonSerializer.SerializeToElement(new
        {
            error = new
            {
                code = "schema_mismatch",
                message = "Запрошенная таблица или поле отсутствует в разрешённом каталоге.",
                relation,
                fields
            }
        }, HarnessJson.Options);

    private sealed record CatalogDocument(
        int Version,
        CatalogRelation[] Relations,
        CatalogRelationship[] Relationships,
        MetricDefinition[] Metrics);

    private sealed record CatalogRelation(
        string Schema,
        string Table,
        string Title,
        string Description,
        CatalogField[] Fields)
    {
        public string QualifiedName => $"{Schema}.{Table}";
    }

    private sealed record CatalogField(
        string Name,
        string Type,
        string Description,
        string[]? Examples);

    private sealed record CatalogRelationship(
        string From,
        string To,
        string Direction,
        string Cardinality);
}
