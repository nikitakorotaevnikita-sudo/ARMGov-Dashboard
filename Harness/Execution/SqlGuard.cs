#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ArmGov.Harness;

public static class SqlGuard
{
    private const int MaxRows = 200;
    private static readonly string[] DenyKeywords =
    {
        "insert", "update", "delete", "drop", "alter", "create", "truncate",
        "grant", "revoke", "copy", "vacuum", "call", "do", "set", "reset",
        "begin", "commit", "rollback", "into", "merge"
    };
    private static readonly string[] DenyFamilies =
    {
        "dblink", "pg_read", "pg_ls", "pg_stat_file", "pg_sleep", "pg_terminate",
        "pg_cancel", "pg_advisory", "pg_import", "lo_", "set_config",
        "query_to_xml", "table_to_xml", "xmlparse"
    };
    private static readonly string[] DenyCredentialRelations =
    {
        "pg_authid", "pg_shadow", "pg_user", "pg_roles"
    };
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly (Regex Regex, string Word)[] KeywordPatterns =
        Compile(DenyKeywords, suffixBoundary: true);
    private static readonly (Regex Regex, string Word)[] FamilyPatterns =
        Compile(DenyFamilies, suffixBoundary: false);
    private static readonly (Regex Regex, string Word)[] CredentialPatterns =
        Compile(DenyCredentialRelations, suffixBoundary: true);

    public static (bool Ok, string? Reason, string? Effective) Check(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return (false, "пустой запрос", null);

        if (!TryScan(sql, out var cleaned, out var code, out var glued, out var error))
            return (false, error, null);

        cleaned = cleaned.Trim();
        code = code.Trim();
        glued = glued.Trim();

        var firstSemicolon = code.IndexOf(';');
        if (firstSemicolon >= 0)
        {
            var oneAtEnd = code.IndexOf(';', firstSemicolon + 1) < 0
                && code[(firstSemicolon + 1)..].Trim().Length == 0;
            if (!oneAtEnd)
                return (false, "разрешён только один оператор", null);
            var cleanSemicolon = cleaned.LastIndexOf(';');
            cleaned = (cleanSemicolon < 0 ? cleaned : cleaned[..cleanSemicolon]).TrimEnd();
        }

        var denied = FindDenied(code, glued, KeywordPatterns)
            ?? FindDenied(code, glued, FamilyPatterns)
            ?? FindDenied(code, glued, CredentialPatterns);
        if (denied is not null)
            return (false, "запрещённая конструкция: " + denied, null);

        var start = code.TrimStart();
        if (!start.StartsWith("select", StringComparison.Ordinal)
            && !start.StartsWith("with", StringComparison.Ordinal))
            return (false, "запрос должен начинаться с SELECT или WITH", null);

        return (true, null, $"select * from ({cleaned}) t limit {MaxRows + 1}");
    }

    internal static bool TryTokenizeInput(
        string sql,
        out string cleaned,
        out string code,
        out string? error)
    {
        return TryScan(sql, out cleaned, out code, out _, out error);
    }

    private static (Regex Regex, string Word)[] Compile(
        IEnumerable<string> words,
        bool suffixBoundary)
    {
        var result = new List<(Regex, string)>();
        foreach (var word in words)
        {
            var pattern = @"\b" + Regex.Escape(word) + (suffixBoundary ? @"\b" : "");
            result.Add((new Regex(pattern, RegexOptions.Compiled, RegexTimeout), word));
        }
        return result.ToArray();
    }

    private static string? FindDenied(
        string code,
        string glued,
        (Regex Regex, string Word)[] patterns)
    {
        foreach (var item in patterns)
            if (item.Regex.IsMatch(code) || item.Regex.IsMatch(glued))
                return item.Word;
        return null;
    }

    private static bool TryScan(
        string sql,
        out string cleaned,
        out string code,
        out string glued,
        out string? error)
    {
        var clean = new StringBuilder(sql.Length);
        var visible = new StringBuilder(sql.Length);
        var joined = new StringBuilder(sql.Length);
        error = null;

        for (var i = 0; i < sql.Length;)
        {
            var c = sql[i];
            if ((c is 'u' or 'U') && i + 2 < sql.Length && sql[i + 1] == '&'
                && sql[i + 2] is '"' or '\'')
            {
                cleaned = code = glued = string.Empty;
                error = "юникод-экранирование идентификаторов/строк запрещено";
                return false;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                i += 2;
                while (i < sql.Length && sql[i] != '\n') i++;
                visible.Append(' ');
                if (i < sql.Length)
                {
                    clean.Append('\n');
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var depth = 1;
                i += 2;
                while (i < sql.Length && depth > 0)
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else i++;
                }
                if (depth != 0)
                {
                    cleaned = code = glued = string.Empty;
                    error = "незакрытый комментарий /* ... */";
                    return false;
                }
                clean.Append(' ');
                visible.Append(' ');
                continue;
            }

            if (c == '\'')
            {
                var escape = IsEscapeString(sql, i);
                var start = i;
                clean.Append(c);
                visible.Append(' ');
                joined.Append(' ');
                i++;
                var closed = false;
                while (i < sql.Length)
                {
                    if (escape && sql[i] == '\\')
                    {
                        clean.Append(sql[i++]);
                        if (i < sql.Length) clean.Append(sql[i++]);
                        continue;
                    }
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            clean.Append("''");
                            i += 2;
                            continue;
                        }
                        clean.Append(sql[i++]);
                        closed = true;
                        break;
                    }
                    clean.Append(sql[i++]);
                }
                if (!closed)
                {
                    cleaned = code = glued = string.Empty;
                    error = "незакрытый строковый литерал, начатый в позиции " + start;
                    return false;
                }
                continue;
            }

            if (c == '"')
            {
                var start = i;
                clean.Append(c);
                visible.Append(c);
                joined.Append(c);
                i++;
                var closed = false;
                while (i < sql.Length)
                {
                    if (sql[i] == '"')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '"')
                        {
                            clean.Append("\"\"");
                            visible.Append("\"\"");
                            joined.Append("\"\"");
                            i += 2;
                            continue;
                        }
                        clean.Append('"');
                        visible.Append('"');
                        joined.Append('"');
                        i++;
                        closed = true;
                        break;
                    }
                    var item = sql[i++];
                    clean.Append(item);
                    visible.Append(item == ';' ? ' ' : char.ToLowerInvariant(item));
                    joined.Append(item == ';' ? ' ' : char.ToLowerInvariant(item));
                }
                if (!closed)
                {
                    cleaned = code = glued = string.Empty;
                    error = "незакрытый идентификатор, начатый в позиции " + start;
                    return false;
                }
                continue;
            }

            if (c == '$' && TryDollarTag(sql, i, out var tag))
            {
                var end = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    cleaned = code = glued = string.Empty;
                    error = "незакрытая долларовая кавычка " + tag;
                    return false;
                }
                var length = end + tag.Length - i;
                clean.Append(sql, i, length);
                visible.Append(' ');
                joined.Append(' ');
                i += length;
                continue;
            }

            clean.Append(c);
            visible.Append(char.ToLowerInvariant(c));
            joined.Append(char.ToLowerInvariant(c));
            i++;
        }

        cleaned = clean.ToString();
        code = visible.ToString();
        glued = joined.ToString();
        return true;
    }

    private static bool IsEscapeString(string sql, int quote)
    {
        var index = quote - 1;
        if (index < 0 || sql[index] is not ('e' or 'E')) return false;
        return index == 0 || (!char.IsLetterOrDigit(sql[index - 1]) && sql[index - 1] != '_');
    }

    private static bool TryDollarTag(string sql, int index, out string tag)
    {
        tag = string.Empty;
        if (sql[index] != '$') return false;
        var cursor = index + 1;
        if (cursor < sql.Length && sql[cursor] == '$')
        {
            tag = "$$";
            return true;
        }
        if (cursor >= sql.Length || (!char.IsLetter(sql[cursor]) && sql[cursor] != '_'))
            return false;
        cursor++;
        while (cursor < sql.Length
               && (char.IsLetterOrDigit(sql[cursor]) || sql[cursor] == '_')) cursor++;
        if (cursor >= sql.Length || sql[cursor] != '$') return false;
        tag = sql[index..(cursor + 1)];
        return true;
    }
}
