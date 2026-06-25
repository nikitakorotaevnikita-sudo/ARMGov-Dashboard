// dbq — разовые SQL-запросы к БД Directum RX, к которой подключён дашборд.
// Переиспользует строку подключения из config.json основного проекта (секция Db),
// чтобы не дублировать креды. Вывод — TSV (первая строка: имена колонок).
//
// Запуск:
//   dotnet run --project tools/dbq -- "select id, discriminator from sungero_wf_task limit 5"
// Опции:
//   --db <Database>   переопределить имя базы (тот же сервер/креды из config.json)
//   --config <path>   явный путь к config.json (иначе ищется вверх от каталога запуска)
using System.Text;
using System.Text.Json;
using Npgsql;

string sql = null;
string dbOverride = null;
string cfgPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--db" && i + 1 < args.Length) { dbOverride = args[++i]; continue; }
    if (args[i] == "--config" && i + 1 < args.Length) { cfgPath = args[++i]; continue; }
    sql ??= args[i];
}
if (string.IsNullOrWhiteSpace(sql))
{
    Console.Error.WriteLine("usage: dbq \"<SQL>\" [--db <Database>] [--config <path>]");
    return 2;
}

cfgPath ??= FindConfig();
if (cfgPath == null || !File.Exists(cfgPath))
{
    Console.Error.WriteLine("config.json не найден (укажите --config)");
    return 3;
}

using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
var db = doc.RootElement.GetProperty("Db");
string Get(string k) => db.TryGetProperty(k, out var v) ? v.GetString() : null;
string host = Get("Host"), port = Get("Port"), name = dbOverride ?? Get("Database"),
       user = Get("Username"), pass = Get("Password");
string connStr = $"Host={host};Port={port};Database={name};Username={user};Password={pass};Timeout=15;Command Timeout=60";

try
{
    using var c = new NpgsqlConnection(connStr);
    c.Open();
    using var cmd = new NpgsqlCommand(sql, c);
    using var r = cmd.ExecuteReader();
    do
    {
        var cols = new string[r.FieldCount];
        for (int i = 0; i < r.FieldCount; i++) cols[i] = r.GetName(i);
        Console.WriteLine(string.Join("\t", cols));
        while (r.Read())
        {
            var sb = new StringBuilder();
            for (int i = 0; i < r.FieldCount; i++)
            {
                if (i > 0) sb.Append('\t');
                sb.Append(r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
            }
            Console.WriteLine(sb.ToString());
        }
    } while (r.NextResult());
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("DB error: " + ex.Message);
    return 1;
}

static string FindConfig()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            var p = Path.Combine(dir.FullName, "config.json");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
    }
    return null;
}
