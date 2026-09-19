#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace ArmGov.Harness;

public static class SqlScopePolicy
{
    private static readonly HashSet<string> AllowedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "count", "sum", "avg", "min", "max", "coalesce", "nullif", "date_trunc",
        "extract", "round", "percentile_cont", "row_number", "rank", "dense_rank",
        "lag", "lead", "lower", "upper", "trim", "length"
    };
    private static readonly HashSet<string> FunctionLikeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "join", "where", "having", "on", "in", "exists", "as",
        "over", "filter", "within", "group", "order", "partition", "case", "when",
        "then", "else", "end", "and", "or", "not", "union", "all", "distinct",
        "limit", "offset", "asc", "desc", "nulls", "first", "last"
    };
    private static readonly HashSet<string> AllowedCastTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "smallint", "integer", "int", "bigint", "numeric", "decimal", "real",
        "double", "text", "varchar", "boolean", "bool", "date", "timestamp",
        "timestamptz", "uuid"
    };
    private static readonly HashSet<string> AllowedOperators = new(StringComparer.Ordinal)
    {
        "+", "-", "*", "/", "%", "=", "<", ">", "<=", ">=", "<>", "!=", "||"
    };
    private static readonly HashSet<string> FromTerminators = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "group", "having", "order", "limit", "offset", "union", "except",
        "intersect", "window", "returning"
    };

    public static ValidationResult Check(string sql, IReadOnlySet<string> allowedRelations)
    {
        if (allowedRelations is null)
            return Invalid("invalid_scope", "Список разрешённых отношений обязателен.");

        var guard = SqlGuard.Check(sql);
        if (!guard.Ok)
            return Invalid("unsafe_sql", guard.Reason ?? "SQL отклонён.");

        if (!SqlGuard.TryTokenizeInput(sql, out _, out var code, out var scanError))
            return Invalid("unsupported_sql", scanError ?? "SQL не распознан.");

        var tokens = Tokenize(code);
        if (tokens.Count == 0)
            return Invalid("unsupported_sql", "SQL не содержит распознаваемых токенов.");
        if (tokens.Any(t => t.Equals("recursive", StringComparison.OrdinalIgnoreCase)))
            return Invalid("unsupported_sql", "Рекурсивные CTE не поддерживаются.");

        var ctes = ParseCtes(tokens, out var cteError);
        if (cteError is not null)
            return Invalid("unsupported_sql", cteError);

        var functions = ValidateFunctionsAndCasts(tokens);
        if (functions is not null)
            return Invalid("unsupported_sql", functions);
        var operators = ValidateOperators(tokens);
        if (operators is not null)
            return Invalid("unsupported_sql", operators);

        var relationError = ValidateRelations(tokens, ctes, allowedRelations);
        if (relationError is not null)
            return Invalid("relation_not_allowed", relationError);

        return new ValidationResult(true, Array.Empty<HarnessError>());
    }

    private static string? ValidateFunctionsAndCasts(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == "::")
            {
                if (i + 1 >= tokens.Count || !IsIdentifier(tokens[i + 1])
                    || !AllowedCastTypes.Contains(Unquote(tokens[i + 1])))
                    return "Нераспознанное приведение типа запрещено.";
                if (i + 2 < tokens.Count && tokens[i + 2] == ".")
                    return "Квалифицированные пользовательские типы запрещены.";
            }

            if (!IsIdentifier(tokens[i])) continue;
            var end = i;
            var name = Unquote(tokens[i]);
            var qualified = false;
            while (end + 2 < tokens.Count && tokens[end + 1] == "."
                   && IsIdentifier(tokens[end + 2]))
            {
                qualified = true;
                end += 2;
                name = Unquote(tokens[end]);
            }
            if (end + 1 >= tokens.Count || tokens[end + 1] != "(") continue;
            if (FunctionLikeKeywords.Contains(name)) continue;
            if (qualified || !AllowedFunctions.Contains(name))
                return $"Функция {string.Join("", tokens.Skip(i).Take(end - i + 1))} не разрешена.";
        }
        return null;
    }

    private static string? ValidateOperators(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token == "@" && i + 1 < tokens.Count && IsIdentifier(tokens[i + 1]))
                continue;
            if (token.Length > 0 && IsOperatorCharacter(token[0])
                && !AllowedOperators.Contains(token))
                return $"Оператор {token} не входит в разрешённое подмножество SQL.";
        }
        return null;
    }

    private sealed record CteDefinition(string Name, int BodyStart, int BodyEnd);

    private static List<CteDefinition> ParseCtes(
        IReadOnlyList<string> tokens,
        out string? error)
    {
        var ctes = new List<CteDefinition>();
        error = null;
        if (!tokens[0].Equals("with", StringComparison.OrdinalIgnoreCase))
            return ctes;

        var i = 1;
        while (i < tokens.Count)
        {
            if (!IsIdentifier(tokens[i]))
            {
                error = "После WITH ожидалось имя CTE.";
                return ctes;
            }
            var name = Unquote(tokens[i++]);
            if (i < tokens.Count && tokens[i] == "(")
            {
                var columnsEnd = MatchingParen(tokens, i);
                if (columnsEnd < 0)
                {
                    error = "Незакрытый список колонок CTE.";
                    return ctes;
                }
                i = columnsEnd + 1;
            }
            if (i >= tokens.Count || !tokens[i].Equals("as", StringComparison.OrdinalIgnoreCase)
                || i + 1 >= tokens.Count || tokens[i + 1] != "(")
            {
                error = "CTE должен иметь форму name AS (...).";
                return ctes;
            }
            var end = MatchingParen(tokens, i + 1);
            if (end < 0)
            {
                error = "Незакрытое тело CTE.";
                return ctes;
            }
            ctes.Add(new CteDefinition(name, i + 2, end));
            i = end + 1;
            if (i < tokens.Count && tokens[i] == ",")
            {
                i++;
                continue;
            }
            break;
        }
        return ctes;
    }

    private static string? ValidateRelations(
        IReadOnlyList<string> tokens,
        IReadOnlyList<CteDefinition> ctes,
        IReadOnlySet<string> allowed)
    {
        var depth = 0;
        var fromDepth = -1;
        var expecting = false;
        var functionStack = new Stack<string?>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token == "(")
            {
                var function = i > 0 && IsIdentifier(tokens[i - 1])
                    ? Unquote(tokens[i - 1])
                    : null;
                functionStack.Push(function);
                depth++;
                if (expecting) expecting = false;
                continue;
            }
            if (token == ")")
            {
                depth--;
                if (functionStack.Count > 0) functionStack.Pop();
                if (fromDepth > depth) fromDepth = -1;
                continue;
            }

            if (token.Equals("from", StringComparison.OrdinalIgnoreCase))
            {
                if (functionStack.Count > 0
                    && string.Equals(functionStack.Peek(), "extract", StringComparison.OrdinalIgnoreCase))
                    continue;
                fromDepth = depth;
                expecting = true;
                continue;
            }
            if (token.Equals("join", StringComparison.OrdinalIgnoreCase))
            {
                expecting = true;
                continue;
            }
            if (fromDepth == depth && FromTerminators.Contains(token))
            {
                fromDepth = -1;
                expecting = false;
                continue;
            }
            if (fromDepth == depth && token == ",")
            {
                expecting = true;
                continue;
            }
            if (!expecting) continue;
            if (token.Equals("lateral", StringComparison.OrdinalIgnoreCase)
                || token.Equals("only", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsIdentifier(token))
                return "После FROM/JOIN ожидалось имя отношения или подзапрос.";

            var first = Unquote(token);
            var relation = first;
            var qualified = false;
            if (i + 2 < tokens.Count && tokens[i + 1] == "." && IsIdentifier(tokens[i + 2]))
            {
                qualified = true;
                relation = first + "." + Unquote(tokens[i + 2]);
                i += 2;
            }
            var isCteReference = !qualified && IsCteBoundAt(ctes, first, i);
            if (!qualified && !isCteReference)
            {
                relation = "public." + first;
            }

            if (i + 1 < tokens.Count && tokens[i + 1] == "(")
                return $"Табличная функция {relation} запрещена.";
            if (!isCteReference && !ContainsRelation(allowed, relation))
                return $"Отношение {relation} отсутствует в разрешённом каталоге.";
            expecting = false;
        }
        return expecting ? "После FROM/JOIN отсутствует отношение." : null;
    }

    private static bool IsCteBoundAt(
        IReadOnlyList<CteDefinition> ctes,
        string name,
        int tokenIndex)
    {
        var containingCte = -1;
        for (var i = 0; i < ctes.Count; i++)
        {
            if (tokenIndex >= ctes[i].BodyStart && tokenIndex < ctes[i].BodyEnd)
            {
                containingCte = i;
                break;
            }
        }

        var boundCount = containingCte >= 0 ? containingCte : ctes.Count;
        for (var i = 0; i < boundCount; i++)
        {
            if (ctes[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool ContainsRelation(IReadOnlySet<string> allowed, string relation)
    {
        if (allowed.Contains(relation)) return true;
        return allowed.Any(item => item.Equals(relation, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> Tokenize(string code)
    {
        var tokens = new List<string>();
        for (var i = 0; i < code.Length;)
        {
            if (char.IsWhiteSpace(code[i])) { i++; continue; }
            if (code[i] == '"')
            {
                var start = i++;
                while (i < code.Length)
                {
                    if (code[i] == '"' && i + 1 < code.Length && code[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }
                    if (code[i++] == '"') break;
                }
                tokens.Add(code[start..i]);
                continue;
            }
            if (char.IsLetter(code[i]) || code[i] == '_')
            {
                var start = i++;
                while (i < code.Length
                       && (char.IsLetterOrDigit(code[i]) || code[i] is '_' or '$')) i++;
                tokens.Add(code[start..i]);
                continue;
            }
            if (code[i] == ':' && i + 1 < code.Length && code[i + 1] == ':')
            {
                tokens.Add("::");
                i += 2;
                continue;
            }
            if (IsOperatorCharacter(code[i]))
            {
                var start = i++;
                while (i < code.Length && IsOperatorCharacter(code[i])) i++;
                tokens.Add(code[start..i]);
                continue;
            }
            tokens.Add(code[i++].ToString());
        }
        return tokens;
    }

    private static int MatchingParen(IReadOnlyList<string> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i] == "(") depth++;
            else if (tokens[i] == ")" && --depth == 0) return i;
        }
        return -1;
    }

    private static bool IsIdentifier(string token)
    {
        if (token.Length >= 2 && token[0] == '"' && token[^1] == '"') return true;
        return token.Length > 0 && (char.IsLetter(token[0]) || token[0] == '_')
            && token.Skip(1).All(c => char.IsLetterOrDigit(c) || c is '_' or '$');
    }

    private static bool IsOperatorCharacter(char value)
    {
        return value is '+' or '-' or '*' or '/' or '%' or '<' or '>' or '='
            or '!' or '|' or '&' or '#' or '~' or '^' or '?' or '@';
    }

    private static string Unquote(string token)
    {
        return token.Length >= 2 && token[0] == '"' && token[^1] == '"'
            ? token[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : token.ToLowerInvariant();
    }

    private static ValidationResult Invalid(string code, string message)
    {
        return new ValidationResult(
            false,
            new[] { new HarnessError(code, message, false) });
    }
}
