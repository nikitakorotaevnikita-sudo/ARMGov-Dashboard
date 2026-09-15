using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Npgsql;

// ARMGov standalone dashboard (MVP, redesign) — read-only viewer over DRX DB.
// 3 процесса: поручения / обращения-рассмотрение / НПА(тематика). Direct PG, SELECT-only.
class Program
{
    // Вендоренные сборки (Npgsql + Microsoft.Extensions.Logging.Abstractions) лежат рядом с exe —
    // резолвер грузит их из папки приложения, привязки к стенду нет (портируется на любую машину).
    static readonly string Bin = AppContext.BaseDirectory;
    // Подключения и параметры берутся из config.json рядом с exe (редактируются в разделе «Бэк-офис»).
    // Секретов (пароль БД, токен LLM) в исходнике НЕТ — они только в config.json (в .gitignore) или в env.
    // Env-override (удобно для Docker): ARMGOV_PREFIX / ARMGOV_DB_PASSWORD / ARMGOV_LLM_TOKEN.
    static AppConfig Conf = new();
    static string Cs => $"Host={Conf.Db.Host};Port={Conf.Db.Port};Database={Conf.Db.Database};Username={Conf.Db.Username};Password={Conf.Db.Password};SSL Mode=Prefer;Trust Server Certificate=true;Timeout=15;Command Timeout=120";
    static string Prefix => string.IsNullOrWhiteSpace(Conf.Prefix) ? "http://localhost:5080/" : Conf.Prefix;
    static string RxBase => Conf.RxBase;
    // Ссылка на карточку задачи в веб-клиенте RX: #/card/{тип-сущности}/{id}.
    // Тип берётся из колонки discriminator самой задачи (sungero_wf_task.discriminator).
    // Старый формат #/Task/{id} в RX 2.6 даёт «Страница не найдена».
    static string RxTaskLink(long id, string discriminator) =>
        string.IsNullOrEmpty(discriminator) ? RxBase + "Task/" + id : RxBase + "card/" + discriminator + "/" + id;
    static string LlmUrl => Conf.Llm.Url;
    static string LlmModel => Conf.Llm.Model;
    static string LlmToken => Conf.Llm.Token;
    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestVersion = HttpVersion.Version11;
        c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        c.DefaultRequestHeaders.ExpectContinue = false;
        return c;
    }

    // ---------- конфигурация ----------
    public class AppConfig
    {
        public string Prefix { get; set; } = "http://localhost:5080/";
        public string RxBase { get; set; } = "http://172.16.96.98/Client/#/";
        public DbCfg Db { get; set; } = new();
        public LlmCfg Llm { get; set; } = new();
        public Thresholds Thresholds { get; set; } = new();
        public List<string> ChatPrompts { get; set; } = new()
        {
            "Где главный затор и кто перегружен?",
            "Кто чаще всех возвращает на доработку и почему это проблема?",
            "Почему обращения граждан в красной зоне?",
            "Назови 3 самых срочных действия на сегодня."
        };
        public string ActiveProfile { get; set; } = "Руководитель";
        public List<Profile> Profiles { get; set; } = DefaultProfiles();
    }
    public class DbCfg { public string Host { get; set; } = "192.168.52.18"; public string Port { get; set; } = "5432"; public string Database { get; set; } = "Polud"; public string Username { get; set; } = "admin"; public string Password { get; set; } = ""; }
    public const string LlmQwenId = "qwen";
    public const string LlmGigaId = "gigachat";
    public const string LlmQwenUrl = "https://llm.ario.directum360.ru/v1/chat/completions";
    public const string LlmQwenModel = "Qwen/Qwen3.8-27B";
    public const string LlmGigaUrl = "https://gigachat.devices.sberbank.ru/api/v1/chat/completions";
    public const string LlmGigaModel = "GigaChat-2";
    public class LlmCfg
    {
        public string Active { get; set; } = LlmQwenId;
        public string Url { get; set; } = LlmQwenUrl;
        public string Model { get; set; } = LlmQwenModel;
        public string Token { get; set; } = "";
        public string Scope { get; set; } = "";
        public List<LlmPreset> Presets { get; set; } = DefaultLlmPresets();
    }
    public class LlmPreset
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Model { get; set; } = "";
        public string Token { get; set; } = "";
        public string Scope { get; set; } = "";
    }
    static List<LlmPreset> DefaultLlmPresets() => new()
    {
        new LlmPreset { Id = LlmQwenId, Name = "Qwen 3.8 (Ario)", Url = LlmQwenUrl, Model = LlmQwenModel },
        new LlmPreset { Id = LlmGigaId, Name = "GigaChat-2", Url = LlmGigaUrl, Model = LlmGigaModel, Scope = "GIGACHAT_API_CORP" }
    };
    static LlmPreset FindPreset(string id) =>
        Conf.Llm.Presets?.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    static void EnsureLlmPresets()
    {
        Conf.Llm ??= new LlmCfg();
        Conf.Llm.Presets ??= new List<LlmPreset>();
        foreach (var d in DefaultLlmPresets())
        {
            var p = FindPreset(d.Id);
            if (p == null) Conf.Llm.Presets.Add(new LlmPreset { Id = d.Id, Name = d.Name, Url = d.Url, Model = d.Model, Scope = d.Scope });
            else
            {
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = d.Name;
                if (string.IsNullOrWhiteSpace(p.Url)) p.Url = d.Url;
                if (string.IsNullOrWhiteSpace(p.Model)) p.Model = d.Model;
                if (string.IsNullOrWhiteSpace(p.Scope)) p.Scope = d.Scope;
            }
        }
        var inferred = IsGigaChat() ? LlmGigaId : LlmQwenId;
        if (IsGigaChat()) Conf.Llm.Active = LlmGigaId;
        else if (string.IsNullOrWhiteSpace(Conf.Llm.Active)) Conf.Llm.Active = LlmQwenId;
        var cur = FindPreset(Conf.Llm.Active) ?? FindPreset(inferred);
        if (cur != null)
        {
            if (!string.IsNullOrWhiteSpace(Conf.Llm.Url)) cur.Url = Conf.Llm.Url;
            if (!string.IsNullOrWhiteSpace(Conf.Llm.Model)) cur.Model = Conf.Llm.Model;
            if (!string.IsNullOrWhiteSpace(Conf.Llm.Token)) cur.Token = Conf.Llm.Token;
            if (!string.IsNullOrWhiteSpace(Conf.Llm.Scope)) cur.Scope = Conf.Llm.Scope;
        }
    }
    static void ApplyLlmPreset(string id)
    {
        var p = FindPreset(id);
        if (p == null) return;
        Conf.Llm.Active = p.Id;
        Conf.Llm.Url = p.Url ?? "";
        Conf.Llm.Model = p.Model ?? "";
        Conf.Llm.Token = p.Token ?? "";
        Conf.Llm.Scope = p.Scope ?? "";
        InvalidateGigaChatToken();
    }
    static void SyncLlmIntoPreset(string id)
    {
        var p = FindPreset(id);
        if (p == null) return;
        if (!string.IsNullOrWhiteSpace(Conf.Llm.Url)) p.Url = Conf.Llm.Url;
        if (!string.IsNullOrWhiteSpace(Conf.Llm.Model)) p.Model = Conf.Llm.Model;
        if (!string.IsNullOrWhiteSpace(Conf.Llm.Token)) p.Token = Conf.Llm.Token;
        if (!string.IsNullOrWhiteSpace(Conf.Llm.Scope)) p.Scope = Conf.Llm.Scope;
    }
    public class Thresholds { public int ThroughputRed { get; set; } = 50; public int ThroughputAmber { get; set; } = 75; public int LongRunnerDays { get; set; } = 3; }
    public class Profile { public string Name { get; set; } = ""; public List<string> Overview { get; set; } = new(); public List<string> Process { get; set; } = new(); }
    // Ключи блоков: overview = throughput|bottleneck|longrunners|burning|svetofor
    // process = funnel|stages|hist|trend|risk|neg|pos|eff|rework|formal|workload|overdue|backlog|ftr|loops|breakdown
    static List<Profile> DefaultProfiles() => new()
    {
        new Profile{ Name="Руководитель", Overview=new(){"throughput","bottleneck","longrunners","burning","svetofor"},
            Process=new(){"funnel","backlog","flow","pickup","ftr","risk","neg","eff","workload","overdue","trend","breakdown"} },
        new Profile{ Name="Контроль исполнения", Overview=new(){"longrunners","bottleneck","burning","svetofor"},
            Process=new(){"overdue","neg","eff","rework","loops","pickup","ftr","flow","backlog","risk","workload","funnel","breakdown"} },
        new Profile{ Name="Аналитик", Overview=new(){"throughput","bottleneck","longrunners","burning","svetofor"},
            Process=new(){"funnel","stages","hist","trend","backlog","flow","pickup","ftr","loops","risk","neg","pos","eff","rework","formal","workload","overdue","breakdown"} },
    };
    static string CfgPath => Path.Combine(AppContext.BaseDirectory, "config.json");
    static readonly JsonSerializerOptions JsonCfg = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    static void LoadConfig()
    {
        var c = new AppConfig();
        try { if (File.Exists(CfgPath)) c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(CfgPath), JsonCfg) ?? new(); }
        catch (Exception ex) { Console.WriteLine("config.json load error: " + ex.Message); }
        var p = Environment.GetEnvironmentVariable("ARMGOV_PREFIX"); if (!string.IsNullOrEmpty(p)) c.Prefix = p;
        var pw = Environment.GetEnvironmentVariable("ARMGOV_DB_PASSWORD"); if (!string.IsNullOrEmpty(pw)) c.Db.Password = pw;
        var tk = Environment.GetEnvironmentVariable("ARMGOV_LLM_TOKEN"); if (!string.IsNullOrEmpty(tk)) c.Llm.Token = tk;
        Conf = c;
        EnsureLlmPresets();
    }
    static void SaveConfig() => File.WriteAllText(CfgPath, JsonSerializer.Serialize(Conf, JsonCfg));

    // Период фильтрует процессы по дате создания задачи (t.created). Пусто/all = весь период.
    static string PeriodClause(string period) => period switch
    {
        "month" => " and t.created >= now() - interval '1 month'",
        "quarter" => " and t.created >= now() - interval '3 months'",
        "year" => " and t.created >= now() - interval '12 months'",
        _ => ""
    };
    // Настройки UI для дашборда (профили/промпты/пороги) — отдаются в составе /api/overview.
    static object UiConfig() => new
    {
        activeProfile = Conf.ActiveProfile,
        profiles = Conf.Profiles,
        chatPrompts = Conf.ChatPrompts,
        thresholds = Conf.Thresholds,
        llmName = FindPreset(Conf.Llm.Active)?.Name ?? Conf.Llm.Model,
        llmModel = Conf.Llm.Model
    };

    // Определения процессов: ключ -> (имя, SQL-условие отбора задач t.*)
    class Proc { public string Key, Name, Where; }
    static readonly List<Proc> Procs = new()
    {
        new Proc{ Key="poruchenia", Name="Поручения", Where="t.discriminator='c290b098-12c7-487d-bb38-73e2c98f9789'" },
        new Proc{ Key="appeals", Name="Обращения граждан", Where="t.discriminator='4ef03457-8b42-4239-a3c5-d4d05e61f0b6'" },
        new Proc{ Key="npa", Name="НПА (регламентирующие)", Where="(t.subject ilike '%НПА%' or t.subject ilike '%регламент%' or t.subject ilike '%правов%акт%' or t.subject ilike '%нормативн%')" },
    };
    static Proc P(string key) => Procs.FirstOrDefault(p => p.Key == key);

    // Демо-персона «руководитель» для блока «Мои задания» (в прототипе нет авторизации).
    // Учётка boss = «Босов Александр», recipient id 53.
    const long DemoUserId = 53;
    const string DemoUserName = "Босов Александр";
    static string ProcNameByDisc(string disc) =>
        disc == "c290b098-12c7-487d-bb38-73e2c98f9789" ? "Поручения" :
        disc == "4ef03457-8b42-4239-a3c5-d4d05e61f0b6" ? "Обращения граждан" : "";

    // Этап = тип задания (a.discriminator). Локализованных имён в БД нет (только .NET-типы
    // в sungero_system_entitytype), поэтому русские названия заданы вручную по типу задания.
    static readonly Dictionary<string, string> StageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["d238ef51-607e-46a5-b86a-ede4482f7f19"] = "Исполнение поручения",     // IActionItemExecutionAssignment
        ["f44faafc-cd55-4c5b-b16d-93b6fc966ffb"] = "Контроль исполнения",       // IActionItemSupervisorAssignment
        ["ab2c30c0-072f-4595-ac10-312f59223cda"] = "Контроль / приёмка",
        ["50e39d87-4fc6-4847-8bad-20847b9ba020"] = "Рассмотрение документа",    // IDocumentReviewAssignment
        ["7cca016a-80f0-4562-9042-57bb748d5b30"] = "Подготовка проекта резолюции", // IPreparingDraftResolutionAssignment
        ["018e582e-5b0e-4e4f-af57-be1e0a468efa"] = "Рассмотрение резолюции",    // IReviewResolutionAssignment
        ["1d5433e5-b285-4310-9a63-fc4e76f0a9b7"] = "Доработка резолюции",       // IReviewReworkAssignment (реальная доработка)
        ["90daecb7-5d5d-465e-95a4-3235b8c01d5b"] = "Простое задание",          // ISimpleAssignment
        ["ef79164b-2ce7-451b-9ba6-eb59dd9a4a74"] = "Уведомление",              // INotice (исключается из статистики)
        // Согласование НПА (процесс согласования проекта правового акта)
        ["28090f3e-2104-4251-a823-229ad1de0427"] = "Согласование",             // IEntityApprovalAssignment
        ["04777117-f8a5-4e68-93fe-2e9036765d1c"] = "Задание",                  // IAdvancedAssignment
        ["01dd1422-80d7-4505-9719-842ca2433647"] = "Обработка документа",      // IDocumentProcessingAssignment
        ["aa94c3bf-2eb5-4e4b-bc1e-c4b86604b889"] = "Подписание",               // ISigningAssignment
        ["14d47b91-96e5-4b5f-89c0-8f8a8db7bc4c"] = "Доработка",                // IEntityReworkAssignment
        ["8fee99ee-b3fd-49dd-9b48-e51b83597227"] = "Ознакомление",             // IAcquaintanceAssignment
        ["9ce17e8c-740f-4655-9a0c-67b59b107b50"] = "Подготовка заключения",    // IPrepareConclusionAssignment
        ["e04a433b-5b48-40c2-993a-41370b9ebb8a"] = "Завершение ознакомления",  // IAcquaintanceFinishAssignment
        ["70b4bad9-604f-46f2-b438-0cc6ec594484"] = "Регистрация и отправка",   // IRegisterAndSendTransferDocumentsAssignment
        ["d0190603-b367-4b4b-ac3c-856f3e495328"] = "Рассмотрение",             // IReviewAssignment
    };
    static string StageName(string disc, int idx) =>
        disc == null ? "—" : (StageNames.TryGetValue(disc, out var n) ? n : "Этап " + (idx + 1));

    // Состояние исполнения поручения (t.executionstate_recman_sungero).
    static readonly Dictionary<string, string> ExecStateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OnExecution"] = "На исполнении",
        ["OnControl"] = "На контроле",
        ["Executed"] = "Исполнено",
        ["Aborted"] = "Прекращено",
    };
    static string ExecStateName(string s) => string.IsNullOrEmpty(s) ? "—" : (ExecStateNames.TryGetValue(s, out var n) ? n : s);

    static void Main()
    {
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            var p = Path.Combine(Bin, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
        LoadConfig();
        LoadNoticeTypes();
        Serve();
    }

    static void Serve()
    {
        var l = new HttpListener();
        l.Prefixes.Add(Prefix);
        l.Start();
        Console.WriteLine("ARMGov dashboard -> " + Prefix);
        while (true)
        {
            HttpListenerContext ctx = null;
            try { ctx = l.GetContext(); } catch { break; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Handle(ctx); }
                catch (Exception ex) { try { Write(ctx, 500, "application/json", "{\"error\":" + JsonSerializer.Serialize(ex.Message) + "}"); } catch { } }
            });
        }
    }

    static void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
        var q = ctx.Request.QueryString;
        var ck = path + ctx.Request.Url.Query;   // ключ кэша = путь + строка запроса
        switch (path)
        {
            case "/api/refresh": Cache.Clear(); J(ctx, new { ok = true }); return;
            case "/api/processes": JCached(ctx, ck, () => BuildProcesses()); return;
            case "/api/overview": JCached(ctx, ck, () => BuildOverview(q["period"])); return;
            case "/api/my/tasks": JCached(ctx, ck, () => BuildMyTasks()); return;
            case "/api/leaders": JCached(ctx, ck, () => BuildLeaders(q["by"])); return;
            case "/api/leader/tasks": JCached(ctx, ck, () => BuildLeaderTasks(q["by"], q["id"])); return;
            case "/api/process": JCached(ctx, ck, () => BuildProcess(q["key"], q["period"])); return;
            case "/api/process/stuck": JCached(ctx, ck, () => BuildStuck(q["key"], q["period"])); return;
            case "/api/process/workload": JCached(ctx, ck, () => BuildWorkload(q["key"], q["period"])); return;
            case "/api/process/departments": JCached(ctx, ck, () => BuildDepartments(q["key"], q["period"])); return;
            case "/api/process/dept-tasks": JCached(ctx, ck, () => BuildDeptTasks(q["key"], q["dept"], q["period"])); return;
            case "/api/process/kind-tasks": JCached(ctx, ck, () => BuildKindTasks(q["key"], q["kind"], q["period"])); return;
            case "/api/process/by-kind": JCached(ctx, ck, () => BuildByKind(q["key"], q["period"])); return;
            case "/api/appeals/topics": JCached(ctx, ck, () => BuildAppealTopics()); return;
            case "/api/appeals/systemic": J(ctx, BuildAppealSystemic(q["force"] == "1")); return;
            case "/api/export":
            {
                var (fn, csv) = BuildExport(q["what"], q["key"], q["period"]);
                var payload = Encoding.UTF8.GetBytes(csv);
                var bom = new byte[] { 0xEF, 0xBB, 0xBF };  // BOM, чтобы Excel читал кириллицу
                var bytes = new byte[bom.Length + payload.Length];
                Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
                Buffer.BlockCopy(payload, 0, bytes, bom.Length, payload.Length);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/csv; charset=utf-8";
                ctx.Response.AddHeader("Content-Disposition", "attachment; filename=\"" + fn + "\"");
                ctx.Response.AddHeader("Cache-Control", "no-store");
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length); ctx.Response.OutputStream.Close();
                return;
            }
            case "/api/task":
                if (long.TryParse(q["id"], out var tid)) { J(ctx, BuildTask(tid)); return; }
                Write(ctx, 400, "application/json", "{\"error\":\"bad id\"}"); return;
            case "/api/performer":
                if (long.TryParse(q["id"], out var pid)) { J(ctx, BuildPerformer(pid, q["key"])); return; }
                Write(ctx, 400, "application/json", "{\"error\":\"bad id\"}"); return;
            case "/api/ai/summary": J(ctx, BuildAiSummary(q["force"] == "1", q["key"])); return;
            case "/api/ai/chat": J(ctx, BuildAiChat(ReadBody(ctx))); return;
            case "/api/ai/explain": J(ctx, BuildAiExplain(ReadBody(ctx))); return;
            case "/api/ai/sql/check":
            {
                var (ok, reason, eff) = SqlCheck(q["q"]);
                J(ctx, new { ok, reason, effective = eff });
                return;
            }
            case "/api/config":
                if (ctx.Request.HttpMethod == "POST") { J(ctx, SaveConfigFromBody(ReadBody(ctx))); return; }
                J(ctx, GetConfigMasked()); return;
            case "/api/config/test": J(ctx, TestConfig()); return;
        }
        if (path == "" || path == "/index.html")
        {
            var html = Path.Combine(AppContext.BaseDirectory, "index.html");
            if (File.Exists(html)) Write(ctx, 200, "text/html; charset=utf-8", File.ReadAllText(html, Encoding.UTF8));
            else Write(ctx, 404, "text/plain", "index.html not found");
            return;
        }
        // static (tokens.css / styles.css и пр.)
        var fname = path.TrimStart('/');
        if (fname.Length > 0 && !fname.Contains(".."))
        {
            var fp = Path.Combine(AppContext.BaseDirectory, fname.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fp))
            {
                if (fname.EndsWith(".woff2"))
                {
                    var bytes = File.ReadAllBytes(fp);
                    ctx.Response.StatusCode = 200; ctx.Response.ContentType = "font/woff2";
                    ctx.Response.AddHeader("Cache-Control", "public, max-age=604800");
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    ctx.Response.OutputStream.Close();
                    return;
                }
                var ct = fname.EndsWith(".css") ? "text/css"
                       : fname.EndsWith(".svg") ? "image/svg+xml"
                       : fname.EndsWith(".js") ? "application/javascript"
                       : fname.EndsWith(".woff2") ? "font/woff2"
                       : "application/octet-stream";
                Write(ctx, 200, ct + "; charset=utf-8", File.ReadAllText(fp, Encoding.UTF8)); return;
            }
        }
        Write(ctx, 404, "text/plain", "not found");
    }

    static void J(HttpListenerContext ctx, object o) => Write(ctx, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(o));

    // ---------- кэш ответов аналитических эндпоинтов ----------
    // Данные не realtime: держим готовый JSON в памяти на короткий TTL. Билдеры не трогаем.
    // Сброс — кнопкой «Обновить данные» (POST /api/refresh) или по истечении TTL.
    static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    static readonly ConcurrentDictionary<string, (DateTime at, string json)> Cache = new();
    // Отдаёт из кэша по ключу или строит, кладёт и отдаёт. Время данных — в заголовке X-Data-At.
    static void JCached(HttpListenerContext ctx, string key, Func<object> build)
    {
        var now = DateTime.Now;
        string json; DateTime at;
        if (Cache.TryGetValue(key, out var e) && now - e.at < CacheTtl) { json = e.json; at = e.at; }
        else { json = JsonSerializer.Serialize(build()); at = now; Cache[key] = (now, json); }
        ctx.Response.AddHeader("X-Data-At", at.ToString("yyyy-MM-dd HH:mm:ss"));
        Write(ctx, 200, "application/json; charset=utf-8", json);
    }
    static void Write(HttpListenerContext ctx, int code, string ct, string body)
    {
        var b = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = code; ctx.Response.ContentType = ct;
        ctx.Response.AddHeader("Cache-Control", "no-store");
        ctx.Response.ContentLength64 = b.Length;
        ctx.Response.OutputStream.Write(b, 0, b.Length); ctx.Response.OutputStream.Close();
    }

    static string Sev(int h) => h >= Conf.Thresholds.ThroughputAmber ? "green" : (h >= Conf.Thresholds.ThroughputRed ? "amber" : "red");

    // ---------- исключение уведомлений из статистики ----------
    // Уведомления (Sungero.Workflow.INotice и *Notification) лежат в той же таблице,
    // что и задания, но это информирование, а не работа — в метрики попадать не должны.
    // Список типов грузится из sungero_system_entitytype при старте (имя содержит Notice/Notif).
    // NoticeNotIn — готовый предикат " and a.discriminator not in ('..','..')" (алиас задания = a).
    static string NoticeNotIn = "";   // " and a.discriminator not in ('..',..)" — для запросов с алиасом a
    static string NoticeList = "";    // "'..','..'" — голый список для подзапросов без алиаса
    static void LoadNoticeTypes()
    {
        var guids = new List<string>();
        try
        {
            using var c = new NpgsqlConnection(Cs); c.Open();
            using var cmd = new NpgsqlCommand(
                "select typeguid::text from sungero_system_entitytype " +
                "where typename::text ilike '%notice%' or typename::text ilike '%notif%'", c);
            using var r = cmd.ExecuteReader();
            while (r.Read()) guids.Add(r.GetString(0));
        }
        catch { /* БД недоступна при старте — используем фолбэк ниже */ }
        if (guids.Count == 0)  // фолбэк: известные типы-уведомления, встречающиеся в заданиях
            guids.AddRange(new[] {
                "ef79164b-2ce7-451b-9ba6-eb59dd9a4a74", "32ce5b61-1be2-4d61-b98a-37b99aff3560",
                "e3f1702b-33e5-4cbb-9ffd-1b0c3504f748", "3dad0441-cd89-4928-b6ff-9b7dd7fc20cf" });
        NoticeList = string.Join(",", guids.Select(g => "'" + g + "'"));
        NoticeNotIn = " and a.discriminator not in (" + NoticeList + ")";
        Console.WriteLine($"Notice types excluded from stats: {guids.Count}");
    }

    // ---------- общие куски SQL ----------
    // AsgJoin уже исключает уведомления (NoticeNotIn). Для inline-запросов по заданиям
    // добавляйте {NoticeNotIn} после {p.Where}.
    static string AsgJoin(Proc p) => $"from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task where {p.Where}{NoticeNotIn}";

    // ---------- /api/processes ----------
    static object BuildProcesses()
    {
        using var c = new NpgsqlConnection(Cs); c.Open();
        var list = new List<object>();
        foreach (var p in Procs)
        {
            long total = ScalarL(c, $"select count(*) from sungero_wf_task t where {p.Where}");
            long inwork = ScalarL(c, $"select count(*) from sungero_wf_task t where {p.Where} and t.status::text='InProcess'");
            long completed = ScalarL(c, $"select count(*) from sungero_wf_task t where {p.Where} and t.status::text='Completed'");
            long activeAsg = ScalarL(c, $"select count(*) {AsgJoin(p)} and a.status::text='InProcess'");
            long overdueAsg = ScalarL(c, $"select count(*) {AsgJoin(p)} and a.status::text='InProcess' and a.deadline is not null and a.deadline<now()");
            int health = activeAsg > 0 ? (int)Math.Round(100.0 * (1.0 - (double)overdueAsg / activeAsg)) : 100;
            var tr = Trend(c, p);
            int redM = tr.AsEnumerable().Reverse().Take(4).Count(o => { var d = (dynamic)o; int t2 = (int)d.ontime + (int)d.overdue; return t2 > 0 && (100.0 * (int)d.ontime / t2) < 50; });
            list.Add(new { key = p.Key, name = p.Name, total, inwork, completed, overdue = overdueAsg, health, severity = Sev(health), chronic = redM >= 3, trend = tr });
        }
        return new { generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), processes = list };
    }

    // ---------- /api/overview (Уровень 0 — стратегический обзор + машиночитаемый агрегат) ----------
    static object BuildOverview(string period = null)
    {
        using var c = new NpgsqlConnection(Cs); c.Open();
        long regOnTime = 0, regBreached = 0, regLong = 0;
        var procs = new List<object>();
        var bottlenecks = new List<dynamic>();   // ранжир (процесс, этап) по медиане возраста активных
        var burning = new List<dynamic>();
        var regMon = new Dictionary<string, int[]>();  // месяц -> [ontime, overdue] по региону (для дельты)

        foreach (var p0 in Procs)
        {
            var p = new Proc { Key = p0.Key, Name = p0.Name, Where = p0.Where + PeriodClause(period) };
            // базовые счётчики
            long total = ScalarL(c, $"select count(*) from sungero_wf_task t where {p.Where}");
            long inwork = ScalarL(c, $"select count(*) from sungero_wf_task t where {p.Where} and t.status::text='InProcess'");
            long overdueAsg = ScalarL(c, $"select count(*) {AsgJoin(p)} and a.status::text='InProcess' and a.deadline is not null and a.deadline<now()");
            long activeAsg = ScalarL(c, $"select count(*) {AsgJoin(p)} and a.status::text='InProcess'");
            int health = activeAsg > 0 ? (int)Math.Round(100.0 * (1.0 - (double)overdueAsg / activeAsg)) : 100;

            // пропускная способность (как дисциплина в тренде): доля «в срок» среди всех заданий,
            // чей норматив уже наступил (завершено в срок ÷ (в срок + просрочено, включая активные просроченные)).
            long onTime = ScalarL(c, $"select count(*) {AsgJoin(p)} and a.status::text='Completed' and a.completed is not null and a.deadline is not null and a.completed<=a.deadline");
            long breached = ScalarL(c, $"select count(*) {AsgJoin(p)} and ((a.status::text='Completed' and a.completed is not null and a.deadline is not null and a.completed>a.deadline) or (a.status::text='InProcess' and a.deadline is not null and a.deadline<now()))");
            long rated = onTime + breached;
            int thrPct = rated > 0 ? (int)Math.Round(100.0 * onTime / rated) : 100;

            // долгострои > порога дней
            long lng = ScalarL(c, $"select count(*) {AsgJoin(p)} and a.status::text='InProcess' and a.deadline is not null and a.deadline < now() - make_interval(days => {Conf.Thresholds.LongRunnerDays})");

            // узкое горлышко процесса: этап с макс. медианой возраста активных заданий
            string bnStage = null; double bnMed = 0; long bnQueue = 0;
            using (var cmd = new NpgsqlCommand(
                "select a.discriminator::text disc, " +
                "percentile_cont(0.5) within group (order by extract(epoch from (now()-a.created))/86400.0) med, " +
                "count(*) queue " +
                $"{AsgJoin(p)} and a.status::text='InProcess' group by a.discriminator order by med desc nulls last", c))
            using (var r = cmd.ExecuteReader())
            {
                int idx = 0;
                while (r.Read())
                {
                    var disc = r.IsDBNull(0) ? null : r.GetString(0);
                    double med = r.IsDBNull(1) ? 0 : Math.Round(r.GetDouble(1), 1);
                    long queue = r.GetInt64(2);
                    string sname = StageName(disc, idx++);
                    if (bnStage == null) { bnStage = sname; bnMed = med; bnQueue = queue; }
                    bottlenecks.Add(new { processKey = p.Key, process = p.Name, stage = sname, medianDays = med, queue });
                }
            }

            var trend = Trend(c, p);
            foreach (dynamic t in trend)
            {
                var m = (string)t.month; if (!regMon.ContainsKey(m)) regMon[m] = new int[2];
                regMon[m][0] += (int)t.ontime; regMon[m][1] += (int)t.overdue;
            }
            var pd = ThrTrendDelta(trend);
            procs.Add(new
            {
                key = p.Key, name = p.Name, total, inwork, health, severity = Sev(health),
                throughputPct = thrPct, overdue = overdueAsg, longRunners = lng,
                bottleneckStage = bnStage ?? "—", bottleneckMedianDays = bnMed, trend,
                throughputDelta = pd.has ? (int?)pd.deltaPp : null
            });
            burning.Add(new { processKey = p.Key, process = p.Name, severity = Sev(health), overdue = overdueAsg, health, thrPct, bnStage = bnStage ?? "—" });

            regOnTime += onTime; regBreached += breached; regLong += lng;
        }

        long regRated = regOnTime + regBreached;
        int regThr = regRated > 0 ? (int)Math.Round(100.0 * regOnTime / regRated) : 100;
        // дельта пропускной способности региона: последний месяц vs предыдущий
        var regTrend = regMon.OrderBy(kv => kv.Key).Select(kv => (object)new { month = kv.Key, ontime = kv.Value[0], overdue = kv.Value[1] }).ToList();
        var regDelta = ThrTrendDelta(regTrend);
        var bnTop = bottlenecks.OrderByDescending(b => (double)b.medianDays).Take(5).ToList();
        var top1 = bnTop.Count > 0 ? bnTop[0] : null;

        // «что горит» — сортировка по тяжести (severity red>amber>green, затем по числу просроченных)
        Func<string, int> sevRank = s => s == "red" ? 0 : (s == "amber" ? 1 : 2);
        var whatsBurning = burning
            .OrderBy(b => sevRank((string)b.severity)).ThenByDescending(b => (long)b.overdue)
            .Select(b => (object)new
            {
                processKey = (string)b.processKey,
                process = (string)b.process,
                severity = (string)b.severity,
                headline = ((long)b.overdue > 0 ? (long)b.overdue + " просрочено · " : "") + "узкое: " + (string)b.bnStage + (((int)b.thrPct) < 50 ? " · пропускная " + (int)b.thrPct + "%" : "")
            }).ToList();

        return new
        {
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            region = new
            {
                throughput = new { pct = regThr, onTimeTotal = regOnTime, ratedTotal = regRated, color = Sev(regThr), deltaPp = regDelta.has ? (int?)regDelta.deltaPp : null, lastMonthPct = regDelta.has ? (int?)regDelta.lastPct : null },
                bottleneck = top1 == null ? null : new { process = (string)top1.process, processKey = (string)top1.processKey, stage = (string)top1.stage, medianDays = (double)top1.medianDays, queue = (long)top1.queue },
                longRunners = new { count = regLong, thresholdDays = Conf.Thresholds.LongRunnerDays }
            },
            bottlenecksTop = bnTop.Select(b => (object)new { processKey = (string)b.processKey, process = (string)b.process, stage = (string)b.stage, medianDays = (double)b.medianDays, queue = (long)b.queue }).ToList(),
            whatsBurning,
            processes = procs,
            period = string.IsNullOrEmpty(period) ? "all" : period,
            ui = UiConfig()
        };
    }

    // Дельта пропускной способности: последний месяц с данными vs предыдущий (в п.п.). has=false если данных <2 мес.
    static (int lastPct, int prevPct, int deltaPp, bool has) ThrTrendDelta(List<object> trend)
    {
        var pts = trend.Select(o => (dynamic)o).Where(d => ((int)d.ontime + (int)d.overdue) > 0).ToList();
        if (pts.Count < 2) return (0, 0, 0, false);
        var last = pts[pts.Count - 1]; var prev = pts[pts.Count - 2];
        int lp = (int)Math.Round(100.0 * (int)last.ontime / ((int)last.ontime + (int)last.overdue));
        int pp = (int)Math.Round(100.0 * (int)prev.ontime / ((int)prev.ontime + (int)prev.overdue));
        return (lp, pp, lp - pp, true);
    }

    static List<object> Trend(NpgsqlConnection c, Proc p)
    {
        var pts = new List<object>();
        using var cmd = new NpgsqlCommand(
            "select to_char(date_trunc('month',a.deadline),'YYYY-MM') m, " +
            "count(*) filter (where a.status::text='Completed' and a.completed is not null and a.completed<=a.deadline) ontime, " +
            "count(*) filter (where (a.status::text='Completed' and a.completed is not null and a.completed>a.deadline) or (a.status::text='InProcess' and a.deadline<now())) overdue " +
            // только месяцы по текущий включительно: будущие дедлайны ещё не просрочены и дают ложные 100%
            $"{AsgJoin(p)} and a.deadline is not null and a.deadline < date_trunc('month', now()) + interval '1 month' group by 1 order by 1", c);
        using var r = cmd.ExecuteReader();
        while (r.Read()) pts.Add(new { month = r.GetString(0), ontime = (int)r.GetInt64(1), overdue = (int)r.GetInt64(2) });
        return pts;
    }

    // ---------- /api/process ----------
    // Блок «Мои задания» демо-руководителя: сводка + топ-3 (просроченные, затем ближайший срок).
    static object BuildMyTasks()
    {
        using var c = new NpgsqlConnection(Cs); c.Open();
        long active = 0, overdue = 0;
        using (var cmd = new NpgsqlCommand(
            "select count(*) filter (where a.status::text='InProcess') act, " +
            "count(*) filter (where a.status::text='InProcess' and a.deadline is not null and a.deadline<now()) ovd " +
            $"from sungero_wf_assignment a where a.performer={DemoUserId} and a.discriminator not in ({NoticeList})", c))
        using (var r = cmd.ExecuteReader()) if (r.Read()) { active = r.GetInt64(0); overdue = r.GetInt64(1); }

        var all = new List<object>();
        using (var cmd = new NpgsqlCommand(
            "select a.id, coalesce(nullif(a.subject::text,''),'(без темы)') subj, a.discriminator::text disc, a.deadline, a.task, t.discriminator::text tdisc " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            $"where a.performer={DemoUserId} and a.discriminator not in ({NoticeList}) and a.status::text='InProcess' " +
            "order by case when a.deadline is not null and a.deadline<now() then 0 when a.deadline is not null then 1 else 2 end, a.deadline asc limit 50", c))
        using (var r = cmd.ExecuteReader())
        {
            var now = DateTime.Now;
            while (r.Read())
            {
                long aid = r.GetInt64(0); string subj = r.GetString(1);
                string disc = r.IsDBNull(2) ? null : r.GetString(2);
                DateTime? dl = r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3);
                long taskId = r.GetInt64(4); string tdisc = r.IsDBNull(5) ? null : r.GetString(5);
                bool ov = dl.HasValue && dl.Value < now;
                string dueKind, dueLabel;
                if (!dl.HasValue) { dueKind = "none"; dueLabel = "без срока"; }
                else { int days = (int)Math.Round(Math.Abs((dl.Value - now).TotalDays)); dueKind = ov ? "overdue" : "soon"; dueLabel = ov ? ("просрочено на " + days + " дн") : ("срок через " + days + " дн"); }
                all.Add(new { id = aid, subject = subj, stage = StageName(disc, 0), process = ProcNameByDisc(tdisc), deadline = dl?.ToString("yyyy-MM-dd"), overdue = ov, dueKind, dueLabel, rxLink = RxTaskLink(taskId, tdisc) });
            }
        }
        return new { user = DemoUserName, active, overdue, all };
    }

    // ---------- /api/leader/tasks (поручения подчинённого/подразделения для drill-модалки) ----------
    static object BuildLeaderTasks(string by, string idStr)
    {
        bool dept = by == "dept";
        bool bu = by == "bu";
        if (!long.TryParse(idStr, out long gid)) return new { name = "", items = new List<object>() };
        string procUnion = "(" + string.Join(" or ", Procs.Select(p => p.Where)) + ")";
        string joins = "join sungero_wf_task t on t.id=a.task left join sungero_core_recipient r on r.id=a.performer";
        string filter = bu
            ? "coalesce(r.emplbunit_company_sungero,0)=@id"
            : dept
            ? "coalesce(r.department_company_sungero,0)=@id"
            : "a.performer=@id";
        using var c = new NpgsqlConnection(Cs); c.Open();
        string name;
        if (gid == 0)
        {
            name = bu ? "(без организации)" : dept ? "(без подразделения)" : "(не назначен)";
        }
        else
        {
            using (var nc = new NpgsqlCommand(
                bu   ? "select coalesce(b.name::text,'(без организации)') from sungero_core_recipient b where b.id=@id"
              : dept ? "select coalesce(d.name::text,'(без подразделения)') from sungero_core_recipient d where d.id=@id"
                     : "select coalesce(name,'(не назначен)') from sungero_core_recipient where id=@id", c))
            { nc.Parameters.AddWithValue("id", gid); var o = nc.ExecuteScalar(); name = o == null || o is DBNull ? "" : o.ToString(); }
        }

        // Первый проход: читаем строки задания как есть и запоминаем головную задачу поручения
        // (maintask; если пусто — сама задача). Финальные элементы (с полем co) собираем во втором
        // проходе, когда головные id всех строк уже известны и участники получены одним запросом —
        // так поле co добавляется сразу при создании анонимного объекта, без хрупкой пересборки.
        var raw = new List<(long aid, string subj, string disc, DateTime? dl, long taskId, string tdisc, string perf, string summ, string execst, long head)>();
        var heads = new List<long>();
        var seenHeads = new HashSet<long>();
        using (var cmd = new NpgsqlCommand(
            "select a.id, coalesce(nullif(a.subject::text,''),'(без темы)') subj, a.discriminator::text disc, a.deadline, a.task, " +
            "t.discriminator::text tdisc, coalesce(r.name,'(не назначен)') perf, " +
            "coalesce(nullif(t.actionitemtai_recman_sungero::text,''),'') summary, " +
            "coalesce(t.executionstate_recman_sungero::text,'') execstate, t.maintask " +
            $"from sungero_wf_assignment a {joins} " +
            $"where {procUnion}{NoticeNotIn} and {filter} and a.status::text='InProcess' and a.performer is not null " +
            "order by case when a.deadline is not null and a.deadline<now() then 0 when a.deadline is not null then 1 else 2 end, a.deadline asc limit 50", c))
        {
            cmd.Parameters.AddWithValue("id", gid);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long aid = r.GetInt64(0); string subj = r.GetString(1);
                string disc = r.IsDBNull(2) ? null : r.GetString(2);
                DateTime? dl = r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3);
                long taskId = r.GetInt64(4); string tdisc = r.IsDBNull(5) ? null : r.GetString(5);
                string perf = r.GetString(6);
                string summ = r.GetString(7); string execst = r.GetString(8);
                long maintask = r.IsDBNull(9) ? 0 : r.GetInt64(9);
                long head = maintask != 0 ? maintask : taskId;
                raw.Add((aid, subj, disc, dl, taskId, tdisc, perf, summ, execst, head));
                if (seenHeads.Add(head)) heads.Add(head);
            }
        }

        long excludePerson = (dept || bu) ? 0L : gid;
        var coMap = CoExecutors(c, heads, excludePerson);

        var items = new List<object>();
        var now = DateTime.Now;
        foreach (var row in raw)
        {
            bool ov = row.dl.HasValue && row.dl.Value < now;
            string dueKind, dueLabel;
            if (!row.dl.HasValue) { dueKind = "none"; dueLabel = "без срока"; }
            else { int days = (int)Math.Round(Math.Abs((row.dl.Value - now).TotalDays)); dueKind = ov ? "overdue" : "soon"; dueLabel = ov ? ("просрочено на " + days + " дн") : ("срок через " + days + " дн"); }
            coMap.TryGetValue(row.head, out var coItems);
            coItems ??= new List<object>();
            int coOverdue = 0;
            foreach (var ci in coItems) if (((dynamic)ci).state == "просрочено") coOverdue++;
            items.Add(new { id = row.aid, subject = row.subj, stage = StageName(row.disc, 0), process = ProcNameByDisc(row.tdisc), deadline = row.dl?.ToString("yyyy-MM-dd"), overdue = ov, dueKind, dueLabel, rxLink = RxTaskLink(row.taskId, row.tdisc), performer = row.perf, summary = string.IsNullOrEmpty(row.summ) ? row.subj : row.summ, execState = ExecStateName(row.execst), co = new { total = coItems.Count, overdue = coOverdue, items = coItems } });
        }
        return new { name, items };
    }

    // Участники поручения кроме самого пользователя: соисполнители поручения,
    // соисполнители пунктов и исполнители других пунктов. Состояние — худшее из заданий.
    static Dictionary<long, List<object>> CoExecutors(NpgsqlConnection c, List<long> headIds, long excludePerson)
    {
        var res = new Dictionary<long, List<object>>();
        if (headIds.Count == 0) return res;
        string ids = string.Join(",", headIds);
        // Примечание: "role" — неоднозначное для парсера PostgreSQL слово в позиции неявного
        // алиаса без AS (в этом месте грамматики оно вызывает syntax error, хотя формально
        // не входит в список reserved keywords). Добавлен явный AS перед каждым использованием.
        string sql =
            "with parts as (" +
            $" select task head, assignee person, 'соисполнитель поручения' as role from sungero_recman_taicoassignees where task in ({ids})" +
            $" union all select task, coassignee, 'соисполнитель пункта' from sungero_recman_taipartscoasgs where task in ({ids})" +
            $" union all select task, assignee, 'исполнитель пункта' from sungero_recman_taiparts where task in ({ids})" +
            ") " +
            "select p.head, coalesce(rc.name,'(не назначен)') nm, min(p.role) as role, " +
            " max(case when a.status::text='InProcess' and a.deadline is not null and a.deadline<now() then 3 " +
            "          when a.status::text='InProcess' then 2 " +
            "          when a.id is null then 1 else 0 end) st, " +
            " min(a.deadline) filter (where a.status::text='InProcess') as dl " +
            "from parts p " +
            "left join sungero_core_recipient rc on rc.id=p.person " +
            // Джойн напрямую по дереву заданий поручения (a.maintask=p.head), а не через промежуточную
            // sungero_wf_task ct — иначе left join по каждой задаче дерева размножал строки до агрегации:
            // на задаче без задания у человека получалась строка a.id is null (ранг «задания нет»), которая
            // побивала max() даже когда в других задачах дерева у него было Completed-задание (ранг «закрыто»
            // недостижим). a.maintask уже указывает на головную задачу напрямую, под него есть индекс
            // idx_assignment_maintask_performer(maintask, performer).
            $"left join sungero_wf_assignment a on a.maintask=p.head and a.performer=p.person{NoticeNotIn} " +
            "where p.person is not null and p.person<>@me " +
            "group by p.head, p.person, rc.name order by p.head, 4 desc, 2";
        using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("me", excludePerson);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long head = r.GetInt64(0);
            int st = (int)r.GetInt64(3);
            string state = st == 3 ? "просрочено" : st == 2 ? "в работе" : st == 1 ? "задания нет" : "закрыто";
            if (!res.TryGetValue(head, out var list)) { list = new List<object>(); res[head] = list; }
            list.Add(new
            {
                name = r.GetString(1),
                role = r.GetString(2),
                state,
                deadline = r.IsDBNull(4) ? "" : r.GetDateTime(4).ToString("yyyy-MM-dd")
            });
        }
        return res;
    }

    static object BuildProcess(string key, string period = null)
    {
        var p0 = P(key); if (p0 == null) return new { error = "unknown process" };
        var p = new Proc { Key = p0.Key, Name = p0.Name, Where = p0.Where + PeriodClause(period) };
        using var c = new NpgsqlConnection(Cs); c.Open();

        long total = ScalarL(c, $"select count(*) from sungero_wf_task t where {p.Where}");
        long inwork = ScalarL(c, $"select count(*) from sungero_wf_task t where {p.Where} and t.status::text='InProcess'");
        long completed = ScalarL(c, $"select count(*) from sungero_wf_task t where {p.Where} and t.status::text='Completed'");
        long overdueAsg = ScalarL(c, $"select count(*) {AsgJoin(p)} and a.status::text='InProcess' and a.deadline is not null and a.deadline<now()");
        double avgCycle = ScalarD(c, $"select coalesce(avg(extract(epoch from (coalesce(a.completed,now())-a.created))/86400.0),0) {AsgJoin(p)}");
        double p95Route = ScalarD(c, $"select coalesce(percentile_cont(0.95) within group (order by extract(epoch from (coalesce(a.completed,now())-a.created))/86400.0),0) {AsgJoin(p)}");

        // Этапы: P50/P95 (по завершённым) + active/overdue/atRisk (по InProcess vs P50/P95)
        var stages = new List<dynamic>();
        using (var cmd = new NpgsqlCommand(
            "select a.discriminator::text disc, " +
            "percentile_cont(0.5) within group (order by extract(epoch from (a.completed-a.created))/86400.0) filter (where a.status::text='Completed' and a.completed is not null) p50, " +
            "percentile_cont(0.95) within group (order by extract(epoch from (a.completed-a.created))/86400.0) filter (where a.status::text='Completed' and a.completed is not null) p95, " +
            "count(*) filter (where a.status::text='InProcess') active, " +
            "count(*) filter (where a.status::text='InProcess' and a.deadline is not null and a.deadline<now()) overdue, " +
            "count(*) filter (where a.status::text='Completed') done, " +
            "count(*) total, min(a.created) seq " +
            $"{AsgJoin(p)} group by a.discriminator order by min(a.created)", c))
        using (var r = cmd.ExecuteReader())
        {
            int i = 0;
            while (r.Read())
            {
                int _act = (int)r.GetInt64(3), _done = (int)r.GetInt64(5), _tot = (int)r.GetInt64(6);
                stages.Add(new
                {
                    disc = r.IsDBNull(0) ? null : r.GetString(0),
                    name = StageName(r.IsDBNull(0) ? null : r.GetString(0), i++),
                    p50 = r.IsDBNull(1) ? 0.0 : Math.Round(r.GetDouble(1), 4),  // в днях, точнее — для адаптивного формата (ч/мин) на клиенте
                    p95 = r.IsDBNull(2) ? 0.0 : Math.Round(r.GetDouble(2), 4),
                    active = _act,
                    overdue = (int)r.GetInt64(4),
                    done = _done,
                    total = _tot,
                    aborted = Math.Max(0, _tot - _done - _act)  // прервано/снято: чтобы разбивка воронки сходилась к total
                });
            }
        }

        // Гистограмма длительности маршрута (по задачам: max(completed|now)-min(created))
        var hist = RouteHistogram(c, p);

        // Deadline-risk: активные задания по близости срока (интуитивно для руководителя):
        //   просрочено = срок прошёл; критично = срок ≤3 дн; под риском = срок ≤7 дн; в норме = дальше/без срока.
        // Считаем в SQL с now() — теми же границами, что и просрочка в KPI (иначе rOver и overdueAsg расходятся на 1 у границы).
        int rNorm = 0, rRisk = 0, rCrit = 0, rOver = 0;
        using (var cmd = new NpgsqlCommand(
            "select " +
            "count(*) filter (where a.deadline is not null and a.deadline < now()) over_, " +
            "count(*) filter (where a.deadline is not null and a.deadline >= now() and a.deadline < now()+interval '3 days') crit, " +
            "count(*) filter (where a.deadline is not null and a.deadline >= now()+interval '3 days' and a.deadline < now()+interval '7 days') risk, " +
            "count(*) filter (where a.deadline is null or a.deadline >= now()+interval '7 days') norm " +
            $"{AsgJoin(p)} and a.status::text='InProcess'", c))
        using (var r = cmd.ExecuteReader())
            if (r.Read()) { rOver = (int)r.GetInt64(0); rCrit = (int)r.GetInt64(1); rRisk = (int)r.GetInt64(2); rNorm = (int)r.GetInt64(3); }

        // Топы исполнителей: негативные (просрочка) и позитивные (вовремя завершено)
        var topNeg = TopPerformers(c, p, true);
        var topPos = TopPerformers(c, p, false);
        var topEff = TopEfficiency(c, p);

        // ----- A. Хронические отклонения: индекс состояния по месяцам + флаг хронического -----
        var trend = Trend(c, p);
        var healthTrend = trend.Select(o => {
            var d = (dynamic)o; int on = (int)d.ontime, ov = (int)d.overdue, tot = on + ov;
            int hh = tot > 0 ? (int)Math.Round(100.0 * on / tot) : 100;
            return (object)new { month = (string)d.month, health = hh, severity = Sev(hh) };
        }).ToList();
        int redMonths = healthTrend.Reverse<object>().Take(4).Count(o => ((dynamic)o).health < 50);
        bool chronic = redMonths >= 3;

        // ----- B. Возвраты + избыточность маршрута -----
        long rwTasks = 0;
        using (var cmd = new NpgsqlCommand(
            "select count(*) filter (where rep>1) rw from (" +
            "select t.id, coalesce(max(c.cnt),1) rep from sungero_wf_task t " +
            $"join (select task, discriminator, count(*) cnt from sungero_wf_assignment where discriminator not in ({NoticeList}) group by task, discriminator) c on c.task=t.id " +
            $"where {p.Where} group by t.id) z", c))
        using (var r = cmd.ExecuteReader()) { if (r.Read()) { rwTasks = r.GetInt64(0); } }
        // Знаменатель — общий total процесса (как у блока «Петли»), чтобы одна и та же метрика
        // «повторное прохождение этапа» совпадала в обоих блоках (задачи без заданий = не зациклились).
        int reworkPct = total > 0 ? (int)Math.Round(100.0 * rwTasks / total) : 0;

        var variants = new List<object>();
        using (var cmd = new NpgsqlCommand(
            "select route, count(*) cnt from (" +
            "select a.task, string_agg(a.discriminator::text,'|' order by a.created) route " +
            $"from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task where {p.Where}{NoticeNotIn} group by a.task) v " +
            "group by route order by cnt desc limit 6", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var discs = (r.IsDBNull(0) ? "" : r.GetString(0)).Split('|');
                var names = discs.Select((dv, ix) => StageName(dv, ix)).ToArray();
                variants.Add(new { route = string.Join(" → ", names), count = (int)r.GetInt64(1) });
            }
        long repeatPerf = ScalarL(c,
            "select count(*) from (select a.task, a.performer from sungero_wf_assignment a " +
            $"join sungero_wf_task t on t.id=a.task where {p.Where}{NoticeNotIn} and a.performer is not null " +
            "group by a.task, a.performer having count(distinct a.discriminator)>1) z");

        // ----- D. Содержательность согласования: время до решения + формальные (<1 дня) -----
        long fTotal = 0, fFormal = 0; double fAvgH = 0;
        using (var cmd = new NpgsqlCommand(
            "select count(*) total, count(*) filter (where extract(epoch from (a.completed-a.created))/3600.0 < 24) formal, " +
            "coalesce(avg(extract(epoch from (a.completed-a.created))/3600.0),0) avgh " +
            $"from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task where {p.Where}{NoticeNotIn} and a.status::text='Completed' and a.completed is not null", c))
        using (var r = cmd.ExecuteReader()) { if (r.Read()) { fTotal = r.GetInt64(0); fFormal = r.GetInt64(1); fAvgH = r.GetDouble(2); } }
        int formalPct = fTotal > 0 ? (int)Math.Round(100.0 * fFormal / fTotal) : 0;

        // ----- B. Карта возвратов («битва правок»): кто инициирует Доработку -----
        // Реальная «доработка» — IReviewReworkAssignment. Раньше тут по ошибке стоял GUID
        // уведомления (INotice ef79164b), из-за чего возвраты/FTR считались по уведомлениям.
        const string DorabotkaDisc = "1d5433e5-b285-4310-9a63-fc4e76f0a9b7";
        var returnAuthors = new List<object>();
        using (var cmd = new NpgsqlCommand(
            "select a.author, coalesce(r.name::text,'(неизвестно)'), count(*) cnt " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            "left join sungero_core_recipient r on r.id=a.author " +
            $"where {p.Where} and a.discriminator='{DorabotkaDisc}' and a.author is not null " +
            "group by a.author, r.name order by cnt desc limit 8", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) returnAuthors.Add(new { id = r.GetInt64(0), name = r.GetString(1), value = (int)r.GetInt64(2) });
        long returnsTotal = ScalarL(c, $"select count(*) {AsgJoin(p)} and a.discriminator='{DorabotkaDisc}'");

        // ----- B. Время и качество решения: медиана цикла + open→decide (MarkRead) + «пустые» -----
        double decMedian = ScalarD(c, $"select coalesce(percentile_cont(0.5) within group (order by extract(epoch from (a.completed-a.created))/3600.0),0) from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task where {p.Where}{NoticeNotIn} and a.status::text='Completed' and a.completed is not null");
        double openDecMedian = 0; long openDecN = 0, instant = 0;
        using (var cmd = new NpgsqlCommand(
            "select coalesce(percentile_cont(0.5) within group (order by mins),0) med, count(*) n, count(*) filter (where mins < 5) inst from (" +
            "select extract(epoch from (a.completed - mr.first_read))/60.0 mins " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            "join (select entityid, min(historydate) first_read from sungero_wf_workflowhistory where operation='MarkRead' group by entityid) mr on mr.entityid=a.id " +
            $"where {p.Where}{NoticeNotIn} and a.status::text='Completed' and a.completed is not null and mr.first_read<=a.completed) z", c))
        using (var r = cmd.ExecuteReader()) if (r.Read()) { openDecMedian = Math.Round(r.GetDouble(0), 1); openDecN = r.GetInt64(1); instant = r.GetInt64(2); }

        // #5 С первого раза (first-time-right) = задача прошла БЕЗ переделок: ни возврата на Доработку,
        // ни повторного прохождения этапа. Единое определение «возврата» с блоком «Петли» —
        // так FTR% и loop% не конфликтуют: непрошедшие с первого раза = петли (+ явная доработка, если тип есть).
        long ftrClean = ScalarL(c, $"select count(*) from sungero_wf_task t where {p.Where} " +
            $"and not exists (select 1 from sungero_wf_assignment a where a.task=t.id and a.discriminator='{DorabotkaDisc}') " +
            $"and not exists (select 1 from sungero_wf_assignment a where a.task=t.id and a.discriminator not in ({NoticeList}) group by a.discriminator having count(*)>1)");
        int ftrPct = total > 0 ? (int)Math.Round(100.0 * ftrClean / total) : 100;

        // #6 Петли/пинг-понг: задачи с повторным прохождением этапа + топ переходов между этапами
        long loopTasks = ScalarL(c, $"select count(distinct task) from (select a.task task from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task where {p.Where}{NoticeNotIn} group by a.task, a.discriminator having count(*)>1) z");
        int loopPct = total > 0 ? (int)Math.Round(100.0 * loopTasks / total) : 0;
        var transitions = new List<object>();
        using (var cmd = new NpgsqlCommand(
            "select prev, d, count(*) c from (select a.discriminator::text d, lag(a.discriminator::text) over (partition by a.task order by a.created, a.id) prev " +
            $"from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task where {p.Where}{NoticeNotIn}) s where prev is not null group by prev, d order by c desc limit 7", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                string fromDisc = r.IsDBNull(0) ? null : r.GetString(0), toDisc = r.IsDBNull(1) ? null : r.GetString(1);
                string from = StageName(fromDisc, 0), to = StageName(toDisc, 0);
                // «возврат» = переход на этап доработки (по GUID типа, а не по локализованной подписи)
                transitions.Add(new { from, to, count = (int)r.GetInt64(2), back = fromDisc == DorabotkaDisc || toDisc == DorabotkaDisc });
            }

        // #3 Приток vs отток по месяцам (создано против завершено)
        var backlog = new List<object>();
        using (var cmd = new NpgsqlCommand(
            "with m as (select generate_series(date_trunc('month',now())-interval '7 months', date_trunc('month',now()), interval '1 month') mo), " +
            $"arr as (select date_trunc('month',t.created) mo, count(*) n from sungero_wf_task t where {p.Where} group by 1), " +
            $"comp as (select date_trunc('month',cd) mo, count(*) n from (select t.id, (select max(a.completed) from sungero_wf_assignment a where a.task=t.id and a.completed is not null{NoticeNotIn}) cd " +
            $"from sungero_wf_task t where {p.Where} and t.status::text='Completed') d where cd is not null group by 1) " +
            "select to_char(m.mo,'YYYY-MM') mon, coalesce(arr.n,0) arrived, coalesce(comp.n,0) completed from m left join arr on arr.mo=m.mo left join comp on comp.mo=m.mo order by m.mo", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) backlog.Add(new { month = r.GetString(0), arrived = (int)r.GetInt64(1), completed = (int)r.GetInt64(2) });

        // #1 Поток-эффективность: ожидание (создано→открыто) vs работа (открыто→решено) по завершённым заданиям с событием открытия
        long flowN = 0; double flowWaitAvgH = 0, flowWorkAvgH = 0, flowEffPct = 0;
        using (var cmd = new NpgsqlCommand(
            "select count(*) n, " +
            "coalesce(sum(extract(epoch from (a.completed - mr.first_read))),0) work_sec, " +
            "coalesce(sum(extract(epoch from (a.completed - a.created))),0) total_sec " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            "join (select entityid, min(historydate) first_read from sungero_wf_workflowhistory where operation='MarkRead' group by entityid) mr on mr.entityid=a.id " +
            $"where {p.Where}{NoticeNotIn} and a.status::text='Completed' and a.completed is not null and mr.first_read>=a.created and mr.first_read<=a.completed", c))
        using (var r = cmd.ExecuteReader())
            if (r.Read())
            {
                flowN = r.GetInt64(0);
                double ws = r.GetDouble(1), ts = r.GetDouble(2);
                flowEffPct = ts > 0 ? Math.Round(100.0 * ws / ts, 1) : 0;
                flowWorkAvgH = flowN > 0 ? Math.Round(ws / flowN / 3600.0, 1) : 0;
                flowWaitAvgH = flowN > 0 ? Math.Round((ts - ws) / flowN / 3600.0, 1) : 0;
            }

        // #2 Скорость реакции: создано задание → первое открытие (сколько лежит непринятым). Где задачи залёживаются.
        string pkCte =
            "with pk as (select a.discriminator::text d, extract(epoch from (mr.first_read - a.created))/3600.0 ph " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            "join (select entityid, min(historydate) first_read from sungero_wf_workflowhistory where operation='MarkRead' group by entityid) mr on mr.entityid=a.id " +
            $"where {p.Where}{NoticeNotIn} and mr.first_read >= a.created) ";
        // Медиана робастна (среднее задрано выбросами в годы). Добавляем p90 и доли «быстрых»/«хвоста».
        long pkN = 0, pkFast = 0, pkSlow = 0; double pkAvgH = 0, pkMedH = 0, pkP90 = 0;
        using (var cmd = new NpgsqlCommand(pkCte + "select count(*) n, coalesce(avg(ph),0) a, coalesce(percentile_cont(0.5) within group (order by ph),0) m, coalesce(percentile_cont(0.9) within group (order by ph),0) p90, count(*) filter (where ph<1) fast, count(*) filter (where ph>=24) slow from pk", c))
        using (var r = cmd.ExecuteReader())
            if (r.Read()) { pkN = r.GetInt64(0); pkAvgH = Math.Round(r.GetDouble(1), 1); pkMedH = Math.Round(r.GetDouble(2), 2); pkP90 = Math.Round(r.GetDouble(3), 1); pkFast = r.GetInt64(4); pkSlow = r.GetInt64(5); }
        var pkByStage = new List<object>();
        using (var cmd = new NpgsqlCommand(pkCte + "select d, count(*) c, coalesce(avg(ph),0) ag from pk group by d order by ag desc limit 6", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                pkByStage.Add(new { stage = StageName(r.IsDBNull(0) ? null : r.GetString(0), 0), count = (int)r.GetInt64(1), avgHours = Math.Round(r.GetDouble(2), 1) });

        // Фильтр «Переделки»: кто дольше держит документ — медиана «получил задание → завершил» по исполнителю.
        var holders = new List<object>();
        using (var cmd = new NpgsqlCommand(
            "select a.performer, coalesce(r.name::text,'(неизвестно)') nm, count(*) n, " +
            "percentile_cont(0.5) within group (order by extract(epoch from (a.completed-a.created))/3600.0) med " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task left join sungero_core_recipient r on r.id=a.performer " +
            $"where {p.Where}{NoticeNotIn} and a.status::text='Completed' and a.completed is not null and a.performer is not null " +
            "group by a.performer, r.name having count(*)>=3 order by med desc limit 8", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) holders.Add(new { id = r.GetInt64(0), name = r.GetString(1), count = (int)r.GetInt64(2), medianHours = Math.Round(r.GetDouble(3), 1) });

        return new
        {
            key = p.Key, name = p.Name, chronic,
            kpi = new { total, inwork, completed, overdue = overdueAsg, avgCycleDays = Math.Round(avgCycle, 1), p95RouteDays = Math.Round(p95Route, 1) },
            stages = stages.Select(s => new { s.name, s.p50, s.p95, s.active, s.overdue, s.done, s.total, s.aborted }).ToList(),
            routeHist = hist,
            trend, healthTrend,
            risk = new { normal = rNorm, atRisk = rRisk, critical = rCrit, overdue = rOver },
            rework = new { tasksTotal = total, tasksRework = rwTasks, pct = reworkPct },
            variants, repeatPerformer = repeatPerf, holders,
            formal = new { completedTotal = fTotal, formalCount = fFormal, formalPct, avgDecisionHours = Math.Round(fAvgH, 1) },
            returnsTotal, returnAuthors,
            decision = new { medianHours = Math.Round(decMedian, 1), openToDecideMedianMin = openDecMedian, sample = openDecN, instantCount = instant, instantThresholdMin = 5 },
            firstTimeRight = new { total, clean = ftrClean, pct = ftrPct },
            loops = new { loopTasks, loopPct, transitions },
            backlog,
            flow = new { sample = flowN, waitAvgHours = flowWaitAvgH, workAvgHours = flowWorkAvgH, effPct = flowEffPct },
            pickup = new { sample = pkN, avgHours = pkAvgH, medianHours = pkMedH, p90Hours = pkP90, fastCount = pkFast, slowCount = pkSlow, byStage = pkByStage },
            topNeg, topPos, topEff
        };
    }

    static List<object> RouteHistogram(NpgsqlConnection c, Proc p)
    {
        // длительность задачи = max(completed|now)-min(created) по её заданиям, в днях; корзины
        var buckets = new (string label, int lo, int hi)[]
        { ("0–7", 0, 7), ("8–30", 8, 30), ("31–90", 31, 90), ("91–180", 91, 180), ("180+", 181, int.MaxValue) };
        var counts = new int[buckets.Length];
        using var cmd = new NpgsqlCommand(
            "select extract(epoch from (max(coalesce(a.completed,now()))-min(a.created)))/86400.0 dur " +
            $"from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task where {p.Where}{NoticeNotIn} group by a.task", c);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            double d = r.IsDBNull(0) ? 0 : r.GetDouble(0);
            for (int i = 0; i < buckets.Length; i++) if (d >= buckets[i].lo && d <= buckets[i].hi) { counts[i]++; break; }
        }
        var res = new List<object>();
        for (int i = 0; i < buckets.Length; i++) res.Add(new { bucket = buckets[i].label + " дн", count = counts[i] });
        return res;
    }

    static List<object> TopPerformers(NpgsqlConnection c, Proc p, bool negative)
    {
        var list = new List<object>();
        string metric = negative
            ? "count(*) filter (where a.status::text='InProcess' and a.deadline is not null and a.deadline<now())"
            : "count(*) filter (where a.status::text='Completed' and a.completed is not null and a.completed<=a.deadline)";
        using var cmd = new NpgsqlCommand(
            $"select a.performer, coalesce(r.name,'(не назначен)'), {metric} val " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            $"left join sungero_core_recipient r on r.id=a.performer where {p.Where}{NoticeNotIn} " +
            "group by a.performer, r.name having " + (negative
                ? "count(*) filter (where a.status::text='InProcess' and a.deadline is not null and a.deadline<now())>0"
                : "count(*) filter (where a.status::text='Completed' and a.completed is not null and a.completed<=a.deadline)>0") +
            " order by val desc limit 5", c);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new { id = r.IsDBNull(0) ? 0 : r.GetInt64(0), name = r.GetString(1), value = (int)r.GetInt64(2) });
        return list;
    }

    // Сотрудники с наивысшим КПД: доля заданий, закрытых в срок, из «оценённых»
    // (в срок + сорвано). Отделяет эффективность от объёма — в отличие от топов «в срок»/«просрочка»,
    // где лидируют одни и те же загруженные люди. Порог по объёму — чтобы 3/3=100% не всплывали.
    static List<object> TopEfficiency(NpgsqlConnection c, Proc p)
    {
        var list = new List<object>();
        const int minRated = 10;   // минимум «оценённых» заданий, чтобы КПД был осмысленным
        using var cmd = new NpgsqlCommand(
            "select a.performer, coalesce(r.name,'(не назначен)'), " +
            "count(*) filter (where a.status::text='Completed' and a.completed is not null and a.deadline is not null and a.completed<=a.deadline) ontime, " +
            "count(*) filter (where (a.status::text='Completed' and a.completed is not null and a.deadline is not null and a.completed>a.deadline) " +
            "or (a.status::text='InProcess' and a.deadline is not null and a.deadline<now())) breached " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            $"left join sungero_core_recipient r on r.id=a.performer where {p.Where}{NoticeNotIn} and a.performer is not null " +
            "group by a.performer, r.name " +
            $"having (count(*) filter (where a.status::text='Completed' and a.completed is not null and a.deadline is not null and a.completed<=a.deadline) " +
            "+ count(*) filter (where (a.status::text='Completed' and a.completed is not null and a.deadline is not null and a.completed>a.deadline) " +
            $"or (a.status::text='InProcess' and a.deadline is not null and a.deadline<now()))) >= {minRated} " +
            "order by (count(*) filter (where a.status::text='Completed' and a.completed is not null and a.deadline is not null and a.completed<=a.deadline))::float " +
            "/ nullif((count(*) filter (where a.status::text='Completed' and a.completed is not null and a.deadline is not null and a.completed<=a.deadline) " +
            "+ count(*) filter (where (a.status::text='Completed' and a.completed is not null and a.deadline is not null and a.completed>a.deadline) " +
            "or (a.status::text='InProcess' and a.deadline is not null and a.deadline<now()))),0) desc, ontime desc limit 5", c);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            int ontime = (int)r.GetInt64(2), breached = (int)r.GetInt64(3), rated = ontime + breached;
            list.Add(new { id = r.IsDBNull(0) ? 0 : r.GetInt64(0), name = r.GetString(1), ontime, breached, rated, kpd = rated > 0 ? (int)Math.Round(100.0 * ontime / rated) : 0 });
        }
        return list;
    }

    // ---------- /api/process/stuck ----------
    static object BuildStuck(string key, string period = null)
    {
        var p0 = P(key); if (p0 == null) return new { error = "unknown process" };
        var p = new Proc { Key = p0.Key, Name = p0.Name, Where = p0.Where + PeriodClause(period) };
        using var c = new NpgsqlConnection(Cs); c.Open();
        var items = new List<object>();
        // зависшие: InProcess, просрочено ИЛИ возраст велик; сортируем по возрасту
        using var cmd = new NpgsqlCommand(
            "select t.id, coalesce(t.subject,'(без темы)'), a.discriminator::text, coalesce(r.name,'(не назначен)'), " +
            "a.deadline, extract(epoch from (now()-a.created))/86400.0 age " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            $"left join sungero_core_recipient r on r.id=a.performer where {p.Where}{NoticeNotIn} and a.status::text='InProcess' " +
            "order by (a.deadline is not null and a.deadline<now()) desc, age desc limit 100", c);
        using var r = cmd.ExecuteReader();
        int i = 0;
        while (r.Read())
        {
            DateTime? dl = r.IsDBNull(4) ? (DateTime?)null : r.GetDateTime(4);
            double age = r.IsDBNull(5) ? 0 : r.GetDouble(5);
            bool overdue = dl.HasValue && dl.Value < DateTime.Now;
            items.Add(new
            {
                id = r.GetInt64(0),
                subject = r.GetString(1),
                stage = StageName(r.IsDBNull(2) ? null : r.GetString(2), i++),
                performer = r.GetString(3),
                deadline = dl?.ToString("yyyy-MM-dd"),
                ageDays = (int)Math.Round(age),
                overdueDays = overdue ? (int)Math.Round((DateTime.Now - dl.Value).TotalDays) : 0,
                risk = overdue ? "overdue" : "inwork"
            });
        }
        return new { count = items.Count, items };
    }

    // ---------- /api/leaders (сквозные агрегаты по исполнителю/подразделению для лид-блока) ----------
    static object BuildLeaders(string by)
    {
        bool dept = by == "dept";
        bool bu = by == "bu";
        using var c = new NpgsqlConnection(Cs); c.Open();
        // группа -> агрегаты; processes[key] -> (inwork, overdue)
        var groups = new Dictionary<long, (string name, string pos, int inwork, int overdue, int exp7,
            Dictionary<string, (int inwork, int overdue)> procs)>();
        foreach (var p0 in Procs)
        {
            string groupId = bu ? "coalesce(e.emplbunit_company_sungero,0)"
                           : dept ? "coalesce(e.department_company_sungero,0)"
                           : "a.performer";
            string groupNm = bu ? "coalesce(b.name::text,'(без организации)')"
                           : dept ? "coalesce(d.name::text,'(без подразделения)')"
                           : "coalesce(r.name,'(не назначен)')";
            string groupPos = (dept || bu) ? "''::text" : "coalesce(jt.name::text,'')";
            string joins = "join sungero_wf_task t on t.id=a.task left join sungero_core_recipient r on r.id=a.performer";
            if (dept) joins += " left join sungero_core_recipient e on e.id=a.performer left join sungero_core_recipient d on d.id=e.department_company_sungero";
            else if (bu) joins += " left join sungero_core_recipient e on e.id=a.performer left join sungero_core_recipient b on b.id=e.emplbunit_company_sungero";
            else joins += " left join sungero_company_jobtitle jt on jt.id=r.jobtitle_company_sungero";
            string sql =
                $"select {groupId} gid, {groupNm} gname, {groupPos} gpos, " +
                "count(*) filter (where a.status::text='InProcess') inwork, " +
                "count(*) filter (where a.status::text='InProcess' and a.deadline is not null and a.deadline<now()) overdue, " +
                "count(*) filter (where a.status::text='InProcess' and a.deadline is not null and a.deadline>=now() and a.deadline<now()+interval '7 days') exp7 " +
                $"from sungero_wf_assignment a {joins} " +
                $"where {p0.Where}{NoticeNotIn} and a.performer is not null group by {groupId}, {groupNm}, {groupPos}";
            using var cmd = new NpgsqlCommand(sql, c);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long gid = r.GetInt64(0); string gname = r.GetString(1); string gpos = r.GetString(2);
                int iw = (int)r.GetInt64(3), ov = (int)r.GetInt64(4), e7 = (int)r.GetInt64(5);
                if (!groups.TryGetValue(gid, out var g))
                    g = (gname, gpos, 0, 0, 0, new Dictionary<string, (int, int)>());
                g.inwork += iw; g.overdue += ov; g.exp7 += e7;
                g.procs[p0.Key] = (iw, ov);
                groups[gid] = g;
            }
        }
        // Просрочка у соисполнителей по поручениям, где участвует сам сотрудник.
        // Считается только для разреза по людям. Связь заданий с поручением — через
        // sungero_wf_assignment.maintask (колонка заполнена всегда), без join по задачам.
        var coOverdue = new Dictionary<long, int>();
        if (!dept && !bu)
        {
            // parts — маленькая таблица (сотни строк), поднята выше my и используется как
            // фильтр maintask in (...): без него my сканирует всю sungero_wf_assignment
            // (Parallel Seq Scan, ~355 тыс. строк) вместо использования индекса по maintask.
            // my ограничен активными заданиями сотрудника (status='InProcess') — иначе набор
            // головных поручений для бейджа шире того, что показывает drill-down (там только
            // активные задания), и числа расходятся.
            string sql2 =
                "with parts as (select task as head, assignee as person from sungero_recman_taicoassignees " +
                " union all select task as head, coassignee as person from sungero_recman_taipartscoasgs " +
                " union all select task as head, assignee as person from sungero_recman_taiparts), " +
                "my as (select distinct a.performer as me, a.maintask as head " +
                " from sungero_wf_assignment a " +
                $" where a.performer is not null and a.maintask is not null and a.status::text='InProcess' and a.maintask in (select head from parts){NoticeNotIn}), " +
                "co as (select m.me as me, p.head as head, p.person as person, " +
                "  max(case when a.status::text='InProcess' and a.deadline is not null and a.deadline<now() then 1 else 0 end) as ov " +
                " from my m " +
                " join parts p on p.head=m.head and p.person is not null and p.person<>m.me " +
                $" left join sungero_wf_assignment a on a.maintask=p.head and a.performer=p.person{NoticeNotIn} " +
                " group by m.me, p.head, p.person) " +
                // Считаем уникальных людей с просрочкой, а не пары «поручение-человек» —
                // иначе один и тот же просроченный соисполнитель множится на число поручений,
                // где он участвует вместе с этим сотрудником.
                "select me, count(distinct person)::bigint as total from co where ov=1 group by me";
            using var cmd2 = new NpgsqlCommand(sql2, c);
            cmd2.CommandTimeout = 120;
            using var r2 = cmd2.ExecuteReader();
            while (r2.Read()) coOverdue[r2.GetInt64(0)] = (int)r2.GetInt64(1);
        }
        var items = groups.Select(kv => new
        {
            id = kv.Key, name = kv.Value.name, position = kv.Value.pos, kind = bu ? "bu" : dept ? "dept" : "performer",
            inwork = kv.Value.inwork, overdue = kv.Value.overdue, exp7 = kv.Value.exp7,
            coOverdue = coOverdue.TryGetValue(kv.Key, out var cov) ? cov : 0,
            risk = kv.Value.overdue > 0 || (coOverdue.TryGetValue(kv.Key, out var cov2) ? cov2 : 0) > 0,
            processes = Procs.Select(p => new {
                key = p.Key, name = p.Name,
                inwork = kv.Value.procs.TryGetValue(p.Key, out var x) ? x.inwork : 0,
                overdue = kv.Value.procs.TryGetValue(p.Key, out var y) ? y.overdue : 0
            }).ToList()
        })
        .Where(x => x.inwork > 0 || x.overdue > 0 || x.coOverdue > 0)   // не показываем пустые группы; coOverdue тоже повод показать
        .OrderByDescending(x => x.overdue).ThenByDescending(x => x.inwork)
        .ToList();
        return new { by = bu ? "bu" : dept ? "dept" : "performer", items };
    }

    // ---------- /api/process/workload (C. Загрузка исполнителей) ----------
    static object BuildWorkload(string key, string period = null)
    {
        var p0 = P(key); if (p0 == null) return new { error = "unknown process" };
        var p = new Proc { Key = p0.Key, Name = p0.Name, Where = p0.Where + PeriodClause(period) };
        using var c = new NpgsqlConnection(Cs); c.Open();
        var list = new List<dynamic>();
        using (var cmd = new NpgsqlCommand(
            "select a.performer, coalesce(r.name,'(не назначен)') as pname, " +
            "count(*) filter (where a.status::text='InProcess') active, " +
            "count(*) filter (where a.status::text='InProcess' and a.deadline is not null and a.deadline<now()) overdue, " +
            "count(*) filter (where a.status::text='Completed') completed, " +
            "count(*) filter (where a.status::text='Completed' and a.completed is not null and a.completed<=a.deadline) ontime, " +
            "coalesce(avg(extract(epoch from (coalesce(a.completed,now())-a.created))/86400.0),0) avghold " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            "left join sungero_core_recipient r on r.id=a.performer " +
            $"where {p.Where}{NoticeNotIn} and a.performer is not null group by a.performer, r.name", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                list.Add(new { id = r.GetInt64(0), name = r.GetString(1), active = (int)r.GetInt64(2), overdue = (int)r.GetInt64(3), completed = (int)r.GetInt64(4), ontime = (int)r.GetInt64(5), avgHold = Math.Round(r.GetDouble(6), 1) });
        double avgActive = list.Count > 0 ? list.Average(x => (double)(int)x.active) : 0;
        var items = list.Select(x => (object)new
        {
            id = (long)x.id, name = (string)x.name, active = (int)x.active, overdue = (int)x.overdue, completed = (int)x.completed,
            ontime = (int)x.ontime,
            onTimePct = ((int)x.completed > 0) ? (int)Math.Round(100.0 * (int)x.ontime / (int)x.completed) : -1,
            avgHold = (double)x.avgHold,
            overloaded = avgActive > 0 && (int)x.active > avgActive * 1.5
        }).OrderByDescending(x => ((dynamic)x).active).Take(25).ToList();
        return new { teamAvgActive = Math.Round(avgActive, 1), overloadedCount = items.Count(x => (bool)((dynamic)x).overloaded), items };
    }

    // ---------- /api/process/departments (распределение по подразделениям) ----------
    // Задача относится к подразделению ПОСЛЕДНЕГО исполнителя (кто ведёт её сейчас).
    static object BuildDepartments(string key, string period = null)
    {
        var p0 = P(key); if (p0 == null) return new { error = "unknown process" };
        var p = new Proc { Key = p0.Key, Name = p0.Name, Where = p0.Where + PeriodClause(period) };
        using var c = new NpgsqlConnection(Cs); c.Open();
        var items = new List<object>();
        string sql =
            "with td as (select distinct on (a.task) a.task tid, coalesce(e.department_company_sungero,0) deptid, d.name::text dept " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            "left join sungero_core_recipient e on e.id=a.performer " +
            "left join sungero_core_recipient d on d.id=e.department_company_sungero " +
            $"where {p.Where}{NoticeNotIn} order by a.task, a.created desc), " +
            "ov as (select a.task tid, " +
            "max((a.status::text='InProcess' and a.deadline is not null and a.deadline<now())::int) isover, " +
            "max((a.status::text='InProcess')::int) isactive " +
            $"from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task where {p.Where}{NoticeNotIn} group by a.task) " +
            "select coalesce(td.dept,'(без подразделения)') dept, td.deptid, count(*) total, " +
            "coalesce(sum(ov.isactive),0) inwork, coalesce(sum(ov.isover),0) overdue " +
            "from td left join ov on ov.tid=td.tid group by td.dept, td.deptid order by total desc";
        using var cmd = new NpgsqlCommand(sql, c);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            items.Add(new
            {
                dept = r.GetString(0),
                deptId = r.GetInt64(1),
                total = (int)r.GetInt64(2),
                inwork = (int)r.GetInt64(3),
                overdue = (int)r.GetInt64(4)
            });
        return new { items };
    }

    // ---------- /api/process/dept-tasks (поручения подразделения) ----------
    static object BuildDeptTasks(string key, string deptStr, string period = null)
    {
        var p0 = P(key); if (p0 == null) return new { error = "unknown process" };
        var p = new Proc { Key = p0.Key, Name = p0.Name, Where = p0.Where + PeriodClause(period) };
        long.TryParse(deptStr, out long deptId);
        using var c = new NpgsqlConnection(Cs); c.Open();
        var items = new List<object>();
        string sql =
            "with td as (select distinct on (a.task) a.task tid, coalesce(e.department_company_sungero,0) deptid, " +
            "coalesce(r.name,'(не назначен)') performer, a.deadline dl, a.status::text st, a.created cr " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            "left join sungero_core_recipient r on r.id=a.performer " +
            "left join sungero_core_recipient e on e.id=a.performer " +
            $"where {p.Where}{NoticeNotIn} order by a.task, a.created desc) " +
            "select t.id, coalesce(t.subject,'(без темы)'), td.performer, td.dl, td.st, " +
            "extract(epoch from (now()-td.cr))/86400.0 age, t.discriminator::text " +
            "from td join sungero_wf_task t on t.id=td.tid where td.deptid=@d " +
            "order by case when td.st='InProcess' then 0 else 1 end, td.dl nulls last";
        using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("d", deptId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            DateTime? dl = r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3);
            string st = r.GetString(4);
            double age = r.IsDBNull(5) ? 0 : r.GetDouble(5);
            bool over = st == "InProcess" && dl.HasValue && dl.Value < DateTime.Now;
            items.Add(new
            {
                id = r.GetInt64(0),
                subject = r.GetString(1),
                performer = r.GetString(2),
                deadline = dl?.ToString("yyyy-MM-dd"),
                status = st == "Completed" ? "done" : (over ? "overdue" : "inwork"),
                ageDays = (int)Math.Round(age),
                rxLink = RxTaskLink(r.GetInt64(0), r.IsDBNull(6) ? null : r.GetString(6))
            });
        }
        return new { count = items.Count, items };
    }

    // ---------- /api/process/kind-tasks (поручения по виду рассмотрения) ----------
    // Зеркало BuildDeptTasks, но фильтр по виду (t.processkind). kindId=0 → вид не указан (null).
    static object BuildKindTasks(string key, string kindStr, string period = null)
    {
        var p0 = P(key); if (p0 == null) return new { error = "unknown process" };
        var p = new Proc { Key = p0.Key, Name = p0.Name, Where = p0.Where + PeriodClause(period) };
        long.TryParse(kindStr, out long kindId);
        using var c = new NpgsqlConnection(Cs); c.Open();
        var items = new List<object>();
        // Считаем от задач (как бар «вида»), последнее задание подтягиваем боковым join'ом —
        // тогда список совпадает по числу с баром, включая задачи без заданий.
        string sql =
            "select t.id, coalesce(t.subject,'(без темы)'), " +
            "coalesce(la.performer,'(не назначен)') performer, la.dl, coalesce(la.st, t.status::text) st, " +
            "extract(epoch from (now()-coalesce(la.cr,t.created)))/86400.0 age, t.discriminator::text " +
            "from sungero_wf_task t " +
            "left join lateral (select coalesce(r.name,'(не назначен)') performer, a.deadline dl, a.status::text st, a.created cr " +
            "from sungero_wf_assignment a left join sungero_core_recipient r on r.id=a.performer " +
            $"where a.task=t.id{NoticeNotIn} order by a.created desc limit 1) la on true " +
            $"where {p.Where} and ((@k=0 and t.processkind is null) or t.processkind=@k) " +
            "order by case when coalesce(la.st, t.status::text)='InProcess' then 0 else 1 end, la.dl nulls last";
        using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("k", kindId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            DateTime? dl = r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3);
            string st = r.GetString(4);
            double age = r.IsDBNull(5) ? 0 : r.GetDouble(5);
            bool over = st == "InProcess" && dl.HasValue && dl.Value < DateTime.Now;
            items.Add(new
            {
                id = r.GetInt64(0),
                subject = r.GetString(1),
                performer = r.GetString(2),
                deadline = dl?.ToString("yyyy-MM-dd"),
                status = st == "Completed" ? "done" : (over ? "overdue" : "inwork"),
                ageDays = (int)Math.Round(age),
                rxLink = RxTaskLink(r.GetInt64(0), r.IsDBNull(6) ? null : r.GetString(6))
            });
        }
        return new { count = items.Count, items };
    }

    // ---------- /api/process/by-kind (разрез по виду процесса рассмотрения) ----------
    static object BuildByKind(string key, string period = null)
    {
        var p0 = P(key); if (p0 == null) return new { error = "unknown process" };
        var p = new Proc { Key = p0.Key, Name = p0.Name, Where = p0.Where + PeriodClause(period) };
        using var c = new NpgsqlConnection(Cs); c.Open();
        var items = new List<object>();
        using var cmd = new NpgsqlCommand(
            "select coalesce(pk.id,0) kind_id, coalesce(pk.name::text,'(не указан)') kind, count(*) total, " +
            "count(*) filter (where t.status::text='InProcess') inwork, " +
            $"count(*) filter (where exists (select 1 from sungero_wf_assignment a where a.task=t.id{NoticeNotIn} and a.status::text='InProcess' and a.deadline is not null and a.deadline<now())) overdue " +
            "from sungero_wf_task t left join sungero_wf_processkind pk on pk.id=t.processkind " +
            $"where {p.Where} group by pk.id, pk.name order by total desc", c);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            items.Add(new { kindId = r.GetInt64(0), label = r.GetString(1), total = (int)r.GetInt64(2), inwork = (int)r.GetInt64(3), overdue = (int)r.GetInt64(4) });
        return new { items };
    }

    // ---------- /api/appeals/topics (тематики обращений граждан, классификатор ТОТКОГ) ----------
    // Источник — модуль gd_citizen: reqquestions (вопросы обращений) + classifierbase (раздел→тема→вопрос).
    // Это НЕ задания процесса, поэтому фильтр уведомлений тут не применяется.
    static object BuildAppealTopics()
    {
        using var c = new NpgsqlConnection(Cs); c.Open();

        long requests = 0, questions = 0; double avgPer = 0, multiPct = 0;
        using (var cmd = new NpgsqlCommand(
            "select count(distinct edoc), count(*), round(count(*)::numeric/nullif(count(distinct edoc),0),2), " +
            "(select round(100.0*count(*) filter(where cc>1)/nullif(count(*),0),1) from " +
            "(select edoc,count(*) cc from gd_citizen_reqquestions where question is not null group by edoc) z) " +
            "from gd_citizen_reqquestions where question is not null", c))
        using (var r = cmd.ExecuteReader())
            if (r.Read()) { requests = r.GetInt64(0); questions = r.GetInt64(1); avgPer = r.IsDBNull(2) ? 0 : (double)r.GetDecimal(2); multiPct = r.IsDBNull(3) ? 0 : (double)r.GetDecimal(3); }
        double tot = questions > 0 ? questions : 1;

        // разбивка по разделам
        var sections = new List<object>();
        using (var cmd = new NpgsqlCommand(
            "with q as (select cb.section sid from gd_citizen_reqquestions rq join gd_citizen_classifierbase cb on cb.id=rq.question) " +
            "select coalesce(s.displayname::text,s.name::text,'(не указан)') nm, count(*) n from q " +
            "left join gd_citizen_classifierbase s on s.id=q.sid group by 1 order by n desc", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) { long n = r.GetInt64(1); sections.Add(new { name = r.GetString(0), n = (int)n, pct = Math.Round(100.0 * n / tot, 1) }); }

        // тепловая карта: раздел -> тема
        var trMap = new Dictionary<string, (long n, List<object> topics)>();
        var trOrder = new List<string>();
        using (var cmd = new NpgsqlCommand(
            // код темы выводим из fullcode самого вопроса (первые 2 сегмента = раздел.тема),
            // т.к. у тема-уровневых записей классификатора fullcode пустой
            "with q as (select cb.section sid, cb.topic tid, substring(cb.fullcode::text from '^[0-9]+\\.[0-9]+') tcode from gd_citizen_reqquestions rq join gd_citizen_classifierbase cb on cb.id=rq.question) " +
            "select coalesce(s.displayname::text,s.name::text,'(не указан)') section, coalesce(t.displayname::text,t.name::text,'(не указана)') topic, coalesce(max(q.tcode),'') tcode, count(*) n " +
            "from q left join gd_citizen_classifierbase s on s.id=q.sid left join gd_citizen_classifierbase t on t.id=q.tid " +
            "group by 1,2 order by 4 desc", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                string sec = r.GetString(0), top = r.GetString(1), tcode = r.GetString(2); long n = r.GetInt64(3);
                if (!trMap.ContainsKey(sec)) { trMap[sec] = (0, new List<object>()); trOrder.Add(sec); }
                var e = trMap[sec]; e.n += n; e.topics.Add(new { name = top, code = tcode, n = (int)n, pct = Math.Round(100.0 * n / tot, 1) }); trMap[sec] = e;
            }
        var treemap = trOrder.OrderByDescending(s => trMap[s].n).Select(s => (object)new {
            section = s, n = (int)trMap[s].n, pct = Math.Round(100.0 * trMap[s].n / tot, 1), topics = trMap[s].topics
        }).ToList();

        // топ вопросов (возвращаем все — фронт покажет топ + «показать все»)
        var topQuestions = new List<object>();
        using (var cmd = new NpgsqlCommand(
            "with q as (select rq.question qid, rq.edoc from gd_citizen_reqquestions rq where rq.question is not null) " +
            "select coalesce(cb.fullcode::text,'') code, coalesce(cb.displayname::text,cb.name::text,'(вопрос)') nm, count(*) n, count(distinct q.edoc) reqs " +
            "from q join gd_citizen_classifierbase cb on cb.id=q.qid group by cb.id, cb.fullcode, cb.displayname, cb.name order by n desc", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) { long n = r.GetInt64(2); topQuestions.Add(new { code = r.GetString(0), name = r.GetString(1), n = (int)n, pct = Math.Round(100.0 * n / tot, 1), requests = (int)r.GetInt64(3) }); }

        // куда направлено (transferredto -> counterparty); % считаем от направленных
        var rt = new List<(string name, int n)>(); long routed = 0;
        using (var cmd = new NpgsqlCommand(
            "select coalesce(cp.name::text,'(орган)') org, count(*) n from gd_citizen_reqquestions rq " +
            "join sungero_parties_counterparty cp on cp.id=rq.transferredto where rq.transferredto is not null " +
            "group by cp.id, cp.name order by n desc", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) { string nm = r.GetString(0); int n = (int)r.GetInt64(1); routed += n; rt.Add((nm, n)); }
        var routedTo = rt.Select(x => (object)new { name = x.name, n = x.n, pct = routed > 0 ? Math.Round(100.0 * x.n / routed, 1) : 0 }).ToList();

        return new {
            kpi = new { requests, questions, avgPerRequest = avgPer, multiPct },
            sections, treemap, topQuestions, routedTo,
            routedCoverage = new { routed, total = questions, pct = questions > 0 ? Math.Round(100.0 * routed / questions, 1) : 0 }
        };
    }

    // ---------- /api/export (CSV для Excel: просроченные / загрузка / подразделения) ----------
    static string CsvCell(string s)
    {
        s = s ?? "";
        if (s.IndexOf(';') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0)
            s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
    static (string fname, string csv) BuildExport(string what, string key, string period)
    {
        var p0 = P(key); if (p0 == null) return ("export.csv", "");
        var p = new Proc { Key = p0.Key, Name = p0.Name, Where = p0.Where + PeriodClause(period) };
        using var c = new NpgsqlConnection(Cs); c.Open();
        var sb = new StringBuilder();
        void Row(params string[] cells) { sb.Append(string.Join(";", cells.Select(CsvCell))); sb.Append("\r\n"); }

        if (what == "workload")
        {
            Row("Исполнитель", "Активных", "Просрочено", "Завершено", "В срок", "Удержание, дн");
            using var cmd = new NpgsqlCommand(
                "select coalesce(r.name,'(не назначен)') n, " +
                "count(*) filter (where a.status::text='InProcess') active, " +
                "count(*) filter (where a.status::text='InProcess' and a.deadline is not null and a.deadline<now()) overdue, " +
                "count(*) filter (where a.status::text='Completed') completed, " +
                "count(*) filter (where a.status::text='Completed' and a.completed is not null and a.completed<=a.deadline) ontime, " +
                "coalesce(avg(extract(epoch from (coalesce(a.completed,now())-a.created))/86400.0),0) avghold " +
                "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task left join sungero_core_recipient r on r.id=a.performer " +
                $"where {p.Where}{NoticeNotIn} and a.performer is not null group by r.name order by active desc", c);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long comp = r.GetInt64(3), ont = r.GetInt64(4);
                Row(r.GetString(0), r.GetInt64(1).ToString(), r.GetInt64(2).ToString(), comp.ToString(),
                    comp > 0 ? (int)Math.Round(100.0 * ont / comp) + "%" : "—", Math.Round(r.GetDouble(5), 1).ToString());
            }
            return ($"workload_{key}.csv", sb.ToString());
        }
        if (what == "departments")
        {
            Row("Подразделение", "Всего", "В работе", "Просрочено");
            string sql =
                "with td as (select distinct on (a.task) a.task tid, d.name::text dept from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
                "left join sungero_core_recipient e on e.id=a.performer left join sungero_core_recipient d on d.id=e.department_company_sungero " +
                $"where {p.Where}{NoticeNotIn} order by a.task, a.created desc), " +
                "ov as (select a.task tid, max((a.status::text='InProcess' and a.deadline is not null and a.deadline<now())::int) isover, max((a.status::text='InProcess')::int) isactive " +
                $"from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task where {p.Where}{NoticeNotIn} group by a.task) " +
                "select coalesce(td.dept,'(без подразделения)') dept, count(*) total, coalesce(sum(ov.isactive),0) inwork, coalesce(sum(ov.isover),0) overdue " +
                "from td left join ov on ov.tid=td.tid group by td.dept order by total desc";
            using var cmd = new NpgsqlCommand(sql, c); using var r = cmd.ExecuteReader();
            while (r.Read()) Row(r.GetString(0), r.GetInt64(1).ToString(), r.GetInt64(2).ToString(), r.GetInt64(3).ToString());
            return ($"departments_{key}.csv", sb.ToString());
        }
        // по умолчанию — просроченные задания
        Row("Тема", "Этап", "Исполнитель", "Срок", "Дней просрочки", "Ссылка RX");
        using (var cmd = new NpgsqlCommand(
            "select coalesce(t.subject,'(без темы)'), a.discriminator::text, coalesce(r.name,'(не назначен)'), a.deadline, " +
            "extract(epoch from (now()-a.deadline))/86400.0 od, t.id, t.discriminator::text " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task left join sungero_core_recipient r on r.id=a.performer " +
            $"where {p.Where}{NoticeNotIn} and a.status::text='InProcess' and a.deadline is not null and a.deadline<now() order by a.deadline asc", c))
        using (var r = cmd.ExecuteReader())
        {
            int i = 0;
            while (r.Read())
                Row(r.GetString(0), StageName(r.IsDBNull(1) ? null : r.GetString(1), i++), r.GetString(2),
                    r.IsDBNull(3) ? "" : r.GetDateTime(3).ToString("yyyy-MM-dd"),
                    ((int)Math.Round(r.GetDouble(4))).ToString(), RxTaskLink(r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6)));
        }
        return ($"overdue_{key}.csv", sb.ToString());
    }

    // ---------- /api/task ----------
    static object BuildTask(long id)
    {
        using var c = new NpgsqlConnection(Cs); c.Open();
        object header = null;
        using (var cmd = new NpgsqlCommand("select id, coalesce(subject,'(без темы)'), status::text, importance::text, created, discriminator::text from sungero_wf_task where id=" + id, c))
        using (var r = cmd.ExecuteReader())
            if (r.Read()) header = new { id = r.GetInt64(0), subject = r.GetString(1), status = r.GetString(2), importance = r.IsDBNull(3) ? "" : r.GetString(3), created = r.IsDBNull(4) ? "" : r.GetDateTime(4).ToString("yyyy-MM-dd"), rxLink = RxTaskLink(r.GetInt64(0), r.IsDBNull(5) ? null : r.GetString(5)) };
        if (header == null) return new { error = "не найдено" };
        var stages = new List<object>();
        using (var cmd = new NpgsqlCommand(
            "select a.discriminator::text, coalesce(r.name,'(не назначен)'), a.status::text, a.created, a.deadline, a.completed " +
            "from sungero_wf_assignment a left join sungero_core_recipient r on r.id=a.performer where a.task=" + id + NoticeNotIn + " order by a.created asc", c))
        using (var r = cmd.ExecuteReader())
        {
            int i = 0;
            while (r.Read())
            {
                var st = r.GetString(2);
                DateTime? cr = r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3);
                DateTime? dl = r.IsDBNull(4) ? (DateTime?)null : r.GetDateTime(4);
                DateTime? cp = r.IsDBNull(5) ? (DateTime?)null : r.GetDateTime(5);
                bool cur = st == "InProcess";
                int dur = cr.HasValue ? (int)Math.Round(((cp ?? DateTime.Now) - cr.Value).TotalDays) : 0;
                bool over = cur && dl.HasValue && dl.Value < DateTime.Now;
                stages.Add(new { name = StageName(r.IsDBNull(0) ? null : r.GetString(0), i++), performer = r.GetString(1), status = st, started = cr?.ToString("yyyy-MM-dd"), deadline = dl?.ToString("yyyy-MM-dd"), completed = cp?.ToString("yyyy-MM-dd"), durationDays = dur, isCurrent = cur, overdue = over });
            }
        }
        return new { header, stages };
    }

    // ---------- /api/performer ----------
    static object BuildPerformer(long pid, string key)
    {
        var p = P(key);
        using var c = new NpgsqlConnection(Cs); c.Open();
        string name = "(исполнитель)";
        using (var cmd = new NpgsqlCommand("select name from sungero_core_recipient where id=" + pid, c)) { var o = cmd.ExecuteScalar(); if (o != null && !(o is DBNull)) name = o.ToString(); }
        string where = p != null ? p.Where : "true";
        var points = new List<object>(); int tOn = 0, tOver = 0;
        using (var cmd = new NpgsqlCommand(
            "select to_char(date_trunc('month',a.deadline),'YYYY-MM') m, " +
            "count(*) filter (where a.status::text='Completed' and a.completed is not null and a.completed<=a.deadline) ontime, " +
            "count(*) filter (where (a.status::text='Completed' and a.completed is not null and a.completed>a.deadline) or (a.status::text='InProcess' and a.deadline<now())) overdue " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            $"where {where}{NoticeNotIn} and a.performer={pid} and a.deadline is not null and a.deadline < date_trunc('month', now()) + interval '1 month' group by 1 order by 1", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) { int on = (int)r.GetInt64(1), ov = (int)r.GetInt64(2); tOn += on; tOver += ov; points.Add(new { month = r.GetString(0), ontime = on, overdue = ov }); }

        // активные поручения исполнителя: что дольше всего в работе / просрочено (со ссылкой в RX)
        var tasks = new List<object>();
        using (var cmd = new NpgsqlCommand(
            "select t.id, coalesce(t.subject,'(без темы)'), a.discriminator::text, a.deadline, " +
            "extract(epoch from (now()-a.created))/86400.0 age, t.discriminator::text " +
            "from sungero_wf_assignment a join sungero_wf_task t on t.id=a.task " +
            $"where {where}{NoticeNotIn} and a.performer={pid} and a.status::text='InProcess' " +
            "order by (a.deadline is not null and a.deadline<now()) desc, age desc limit 50", c))
        using (var r = cmd.ExecuteReader())
        {
            int i = 0;
            while (r.Read())
            {
                DateTime? dl = r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3);
                double age = r.IsDBNull(4) ? 0 : r.GetDouble(4);
                bool over = dl.HasValue && dl.Value < DateTime.Now;
                tasks.Add(new
                {
                    id = r.GetInt64(0),
                    subject = r.GetString(1),
                    stage = StageName(r.IsDBNull(2) ? null : r.GetString(2), i++),
                    deadline = dl?.ToString("yyyy-MM-dd"),
                    ageDays = (int)Math.Round(age),
                    overdueDays = over ? (int)Math.Round((DateTime.Now - dl.Value).TotalDays) : 0,
                    overdue = over,
                    rxLink = RxTaskLink(r.GetInt64(0), r.IsDBNull(5) ? null : r.GetString(5))
                });
            }
        }
        return new { name, totalOntime = tOn, totalOverdue = tOver, points, tasks };
    }

    // ---------- Бэк-офис: конфигурация (адрес/пароль БД, адрес/токен модели) ----------
    static object GetConfigMasked() => new
    {
        prefix = Conf.Prefix,
        rxBase = Conf.RxBase,
        db = new { Conf.Db.Host, Conf.Db.Port, Conf.Db.Database, Conf.Db.Username, hasPassword = !string.IsNullOrEmpty(Conf.Db.Password) },
        llm = new
        {
            active = Conf.Llm.Active,
            url = Conf.Llm.Url,
            model = Conf.Llm.Model,
            scope = Conf.Llm.Scope,
            hasToken = !string.IsNullOrEmpty(Conf.Llm.Token),
            presets = (Conf.Llm.Presets ?? new List<LlmPreset>()).Select(p => new
            {
                id = p.Id,
                name = p.Name,
                url = p.Url,
                model = p.Model,
                scope = p.Scope,
                hasToken = !string.IsNullOrEmpty(p.Token)
            }).ToList()
        },
        thresholds = Conf.Thresholds,
        chatPrompts = Conf.ChatPrompts,
        activeProfile = Conf.ActiveProfile,
        profiles = Conf.Profiles
    };
    static object SaveConfigFromBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body); var r = doc.RootElement;
            string S(JsonElement e, string name, string cur) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : cur;
            string Keep(JsonElement e, string name, string cur)
            {
                var s = S(e, name, null);
                return string.IsNullOrWhiteSpace(s) ? cur : s;
            }
            Conf.Prefix = Keep(r, "prefix", Conf.Prefix);
            Conf.RxBase = Keep(r, "rxBase", Conf.RxBase);
            if (r.TryGetProperty("db", out var db))
            {
                Conf.Db.Host = Keep(db, "host", Conf.Db.Host); Conf.Db.Port = Keep(db, "port", Conf.Db.Port);
                Conf.Db.Database = Keep(db, "database", Conf.Db.Database); Conf.Db.Username = Keep(db, "username", Conf.Db.Username);
                var np = S(db, "password", ""); if (!string.IsNullOrEmpty(np)) Conf.Db.Password = np;   // пусто = не менять
            }
            if (r.TryGetProperty("llm", out var llm))
            {
                EnsureLlmPresets();
                var prevActive = string.IsNullOrWhiteSpace(Conf.Llm.Active) ? LlmQwenId : Conf.Llm.Active;
                Conf.Llm.Url = Keep(llm, "url", Conf.Llm.Url);
                Conf.Llm.Model = Keep(llm, "model", Conf.Llm.Model);
                Conf.Llm.Scope = Keep(llm, "scope", Conf.Llm.Scope);
                var nt = S(llm, "token", ""); if (!string.IsNullOrEmpty(nt)) Conf.Llm.Token = nt;
                SyncLlmIntoPreset(prevActive);
                var nextActive = Keep(llm, "active", prevActive);
                if (!string.Equals(nextActive, prevActive, StringComparison.OrdinalIgnoreCase))
                    ApplyLlmPreset(nextActive);
                else
                    SyncLlmIntoPreset(Conf.Llm.Active);
                InvalidateGigaChatToken();
            }
            if (r.TryGetProperty("activeProfile", out var ap) && ap.ValueKind == JsonValueKind.String) Conf.ActiveProfile = ap.GetString();
            if (r.TryGetProperty("thresholds", out var th) && th.ValueKind == JsonValueKind.Object) Conf.Thresholds = th.Deserialize<Thresholds>(JsonCfg) ?? Conf.Thresholds;
            if (r.TryGetProperty("chatPrompts", out var cp) && cp.ValueKind == JsonValueKind.Array) Conf.ChatPrompts = cp.Deserialize<List<string>>(JsonCfg) ?? Conf.ChatPrompts;
            if (r.TryGetProperty("profiles", out var pr) && pr.ValueKind == JsonValueKind.Array) Conf.Profiles = pr.Deserialize<List<Profile>>(JsonCfg) ?? Conf.Profiles;
            SaveConfig();
            _sumCache.Clear(); _bundleCache = null;   // сбросить кэши, чтобы подхватились новые настройки
            return new { ok = true, note = "Сохранено в config.json. Смена адреса прослушивания (Prefix) применится после перезапуска; БД/модель — сразу.", config = GetConfigMasked() };
        }
        catch (Exception ex) { return new { ok = false, error = ex.Message }; }
    }
    static object TestConfig()
    {
        object dbres, llmres;
        try { using var c = new NpgsqlConnection(Cs); c.Open(); using var cmd = new NpgsqlCommand("select 1", c); cmd.ExecuteScalar(); dbres = new { ok = true }; }
        catch (Exception ex) { dbres = new { ok = false, error = ex.Message.Split('\n')[0] }; }
        try { LlmChat(new object[] { new { role = "user", content = "ping" } }, 16, 0); llmres = new { ok = true }; }
        catch (Exception ex) { llmres = new { ok = false, error = ex.Message.Split('\n')[0] }; }
        return new { db = dbres, llm = llmres };
    }

    // ---------- C. LLM-аналитика + чат ----------
    static string ReadBody(HttpListenerContext ctx)
    {
        using var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
        return sr.ReadToEnd();
    }

    static bool IsGigaChat()
    {
        var u = LlmUrl ?? "";
        var m = LlmModel ?? "";
        return u.IndexOf("giga.chat", StringComparison.OrdinalIgnoreCase) >= 0
            || u.IndexOf("gigachat", StringComparison.OrdinalIgnoreCase) >= 0
            || m.StartsWith("GigaChat", StringComparison.OrdinalIgnoreCase);
    }
    static readonly object _gcTokLock = new();
    static string _gcAccess;
    static DateTime _gcAccessUntil = DateTime.MinValue;
    static void InvalidateGigaChatToken()
    {
        lock (_gcTokLock) { _gcAccess = null; _gcAccessUntil = DateTime.MinValue; }
    }
    static string LlmGigaAccess()
    {
        lock (_gcTokLock)
        {
            if (!string.IsNullOrEmpty(_gcAccess) && DateTime.UtcNow < _gcAccessUntil) return _gcAccess;
            Console.WriteLine("GigaChat: requesting access token...");
            var key = (LlmToken ?? "").Trim();
            if (key.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) key = key.Substring(6).Trim();
            var scopes = new List<string>();
            var cfg = string.IsNullOrWhiteSpace(Conf.Llm.Scope) ? "GIGACHAT_API_CORP" : Conf.Llm.Scope.Trim();
            scopes.Add(cfg);
            foreach (var s in new[] { "GIGACHAT_API_CORP", "GIGACHAT_API_B2B", "GIGACHAT_API_PERS" })
                if (!scopes.Exists(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase))) scopes.Add(s);
            Exception last = null;
            foreach (var scope in scopes)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://ngw.devices.sberbank.ru:9443/api/v2/oauth");
                    req.Headers.TryAddWithoutValidation("Authorization", "Basic " + key);
                    req.Headers.TryAddWithoutValidation("RqUID", Guid.NewGuid().ToString());
                    req.Headers.TryAddWithoutValidation("Accept", "application/json");
                    req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["scope"] = scope });
                    using var resp = Http.Send(req);
                    var txt = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    Console.WriteLine("GigaChat OAuth " + scope + " -> " + (int)resp.StatusCode);
                    if (!resp.IsSuccessStatusCode) { last = new Exception("GigaChat OAuth HTTP " + (int)resp.StatusCode + ": " + Trunc(txt, 200)); continue; }
                    using var doc = JsonDocument.Parse(txt);
                    var token = doc.RootElement.GetProperty("access_token").GetString();
                    var exp = doc.RootElement.TryGetProperty("expires_at", out var expEl) && expEl.ValueKind == JsonValueKind.Number ? expEl.GetInt64() : 0;
                    DateTime until;
                    if (exp > 10_000_000_000L) until = DateTimeOffset.FromUnixTimeMilliseconds(exp).UtcDateTime;
                    else if (exp > 0) until = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
                    else until = DateTime.UtcNow.AddMinutes(25);
                    _gcAccess = token;
                    _gcAccessUntil = until.AddMinutes(-1);
                    return token;
                }
                catch (Exception ex) { last = ex; }
            }
            throw last ?? new Exception("GigaChat OAuth failed");
        }
    }

    // Вызов LLM (OpenAI chat/completions). messages: массив {role,content}. Бросает при ошибке.
    // Для Qwen 3.x выключаем thinking. Для GigaChat ключ в config — Authorization key: сначала OAuth, потом Bearer.
    static string LlmChat(object[] messages, int maxTokens, double temperature) => LlmChatCore(messages, maxTokens, temperature, true);
    static string LlmChatCore(object[] messages, int maxTokens, double temperature, bool retryOn401)
    {
        object body = IsGigaChat()
            ? new { model = LlmModel, messages, max_tokens = maxTokens, temperature }
            : (object)new { model = LlmModel, messages, max_tokens = maxTokens, temperature, chat_template_kwargs = new { enable_thinking = false } };
        var bearer = IsGigaChat() ? LlmGigaAccess() : LlmToken;
        using var req = new HttpRequestMessage(HttpMethod.Post, LlmUrl);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = Http.Send(req);
        var txt = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if ((int)resp.StatusCode == 401 && retryOn401 && IsGigaChat())
        {
            InvalidateGigaChatToken();
            return LlmChatCore(messages, maxTokens, temperature, false);
        }
        if (!resp.IsSuccessStatusCode) throw new Exception("LLM HTTP " + (int)resp.StatusCode + ": " + Trunc(txt, 300));
        using var doc = JsonDocument.Parse(txt);
        var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = msg.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(content) && msg.TryGetProperty("reasoning_content", out var rEl) && rEl.ValueKind == JsonValueKind.String)
            content = rEl.GetString();
        return content ?? "";
    }
    static string Trunc(string s, int n) => s != null && s.Length > n ? s.Substring(0, n) + "…" : s;

    // ИИ-разбор системных проблем по топ-3 темам обращений.
    // Источник текста — тема документа-обращения (sungero_content_edoc.name): «Обращение от {ФИО} "{суть}"».
    // Полное тело письма во вложении (бинарь) — недоступно; берём суть (до ~250–300 симв), этого хватает на выводы.
    static readonly Dictionary<string, (object data, DateTime at)> _sysCache = new();
    static object BuildAppealSystemic(bool force)
    {
        if (!force && _sysCache.TryGetValue("appeals", out var cc) && (DateTime.Now - cc.at).TotalMinutes < 30) return cc.data;
        using var c = new NpgsqlConnection(Cs); c.Open();

        // Фильтр «настоящее письмо гражданина»: тема начинается с «Обращение…», содержит суть (длина ≥80),
        // и это не служебная пересылка/тест. Отсекаем маршрутные записи, которые классифицированы как обращения.
        const string realAppeal =
            " and e.name::text ilike 'Обращение%'" +
            " and e.name::text not ilike '%аправление на рассмотрение%'" +
            " and e.name::text not ilike '%еренаправл%'" +
            " and e.name::text not ilike '%Тест%'" +
            " and length(e.name::text)>=80";

        // топ-3 темы по числу РЕАЛЬНЫХ обращений граждан (служебные пересылки/тесты исключены фильтром realAppeal).
        // Осознанно отличается от «Топ вопросов» на странице (там все записи классификатора) — в UI даём оговорку.
        var topics = new List<(long id, string name, int n)>();
        using (var cmd = new NpgsqlCommand(
            "select cb.id, coalesce(nullif(cb.displayname::text,''),cb.name::text) nm, count(*) n " +
            "from gd_citizen_reqquestions rq join gd_citizen_classifierbase cb on cb.id=rq.question " +
            "join sungero_content_edoc e on e.id=rq.edoc " +
            "where rq.question is not null" + realAppeal + " " +
            "group by cb.id, cb.displayname, cb.name order by n desc limit 3", c))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) topics.Add((r.GetInt64(0), r.GetString(1), (int)r.GetInt64(2)));

        var sb = new StringBuilder();
        var topicMeta = new List<object>();
        foreach (var (id, name, n) in topics)
        {
            var texts = new List<string>();
            using (var cmd = new NpgsqlCommand(
                "select e.name::text t from gd_citizen_reqquestions rq join sungero_content_edoc e on e.id=rq.edoc " +
                "where rq.question=@id and e.name is not null" + realAppeal + " " +
                "order by length(e.name::text) desc limit 40", c))
            {
                cmd.Parameters.AddWithValue("id", id);
                using var r = cmd.ExecuteReader();
                while (r.Read()) texts.Add(Trunc(r.GetString(0), 300).Replace("\r", " ").Replace("\n", " "));
            }
            topicMeta.Add(new { name, n, used = texts.Count });
            sb.Append("\n\n=== ТЕМА: ").Append(name).Append(" (обращений по теме: ").Append(n)
              .Append(", текстов в выборке: ").Append(texts.Count).Append(") ===\n");
            for (int i = 0; i < texts.Count; i++) sb.Append(i + 1).Append(". ").Append(texts[i]).Append('\n');
        }
        if (topicMeta.Count == 0) return new { error = "нет данных по обращениям" };

        string sysPrompt =
            "Ты — старший аналитик аппарата руководителя региона. Тебе даны реальные тексты обращений граждан, " +
            "сгруппированные по 3 самым частым темам. Для КАЖДОЙ темы: прочитай тексты, выяви СИСТЕМНЫЕ проблемы " +
            "(повторяющиеся у многих заявителей, а не единичные случаи) и сделай практический вывод для первого лица. " +
            "Формат строго: для каждой темы — строка-заголовок вида '1. <название темы>' (нумеруй 1, 2, 3), " +
            "далее маркированные пункты '- <системная проблема, опираясь на тексты>', " +
            "и в конце темы отдельная строка '- Вывод: <что предпринять руководителю>'. " +
            "Только по существу, с опорой на тексты, без воды и без выдумок. " +
            "Если по теме обращения разрозненные и системности не видно — прямо напиши это. По-русски.";
        var messages = new object[]
        {
            new { role = "system", content = sysPrompt },
            new { role = "user", content = "Тексты обращений по темам:" + sb.ToString() }
        };
        try
        {
            var text = LlmChat(messages, 1800, 0.3);
            var data = new { text, topics = topicMeta, generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), model = LlmModel };
            _sysCache["appeals"] = (data, DateTime.Now);
            return data;
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    // Машиночитаемый агрегат всех метрик — общий контекст для сводки и чата.
    // Компактный срез тематик обращений для контекста LLM (без всех 334 вопросов):
    // KPI + разделы + темы + топ-10 вопросов + топ-10 органов. Через JSON-проекцию полного ответа.
    static object AppealTopicsAiSlice()
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(BuildAppealTopics()));
            var root = doc.RootElement;
            object Arr(JsonElement el, int take) => el.EnumerateArray().Take(take)
                .Select(o => (object)new { name = o.GetProperty("name").GetString(), n = o.GetProperty("n").GetInt32(), pct = o.GetProperty("pct").GetDouble() }).ToList();
            var темы = new List<object>();
            foreach (var s in root.GetProperty("treemap").EnumerateArray())
                foreach (var t in s.GetProperty("topics").EnumerateArray())
                    темы.Add(new { раздел = s.GetProperty("section").GetString(), тема = t.GetProperty("name").GetString(), n = t.GetProperty("n").GetInt32(), pct = t.GetProperty("pct").GetDouble() });
            var k = root.GetProperty("kpi"); var rc = root.GetProperty("routedCoverage");
            return new
            {
                обращений = k.GetProperty("requests").GetInt64(),
                вопросов = k.GetProperty("questions").GetInt64(),
                вопросов_на_обращение = k.GetProperty("avgPerRequest").GetDouble(),
                разделы = Arr(root.GetProperty("sections"), 10),
                темы,
                топ_вопросов = root.GetProperty("topQuestions").EnumerateArray().Take(10)
                    .Select(q => (object)new { вопрос = q.GetProperty("name").GetString(), n = q.GetProperty("n").GetInt32(), pct = q.GetProperty("pct").GetDouble() }).ToList(),
                куда_направлено = Arr(root.GetProperty("routedTo"), 10),
                покрытие_направлено_pct = rc.GetProperty("pct").GetDouble()
            };
        }
        catch { return null; }
    }

    static object BuildAiBundle()
    {
        var detail = new Dictionary<string, object>();
        foreach (var p in Procs)
            detail[p.Key] = new { process = BuildProcess(p.Key), workload = BuildWorkload(p.Key) };
        return new { generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), overview = BuildOverview(), detail, тематика_обращений = AppealTopicsAiSlice() };
    }
    static object _bundleCache; static DateTime _bundleAt;
    static object GetBundleCached()
    {
        if (_bundleCache != null && (DateTime.Now - _bundleAt).TotalMinutes < 2) return _bundleCache;
        _bundleCache = BuildAiBundle(); _bundleAt = DateTime.Now; return _bundleCache;
    }

    static readonly Dictionary<string, (object data, DateTime at)> _sumCache = new();
    static object BuildAiSummary(bool force, string key = null)
    {
        string ck = string.IsNullOrEmpty(key) ? "region" : key;
        if (!force && _sumCache.TryGetValue(ck, out var cc) && (DateTime.Now - cc.at).TotalMinutes < 10) return cc.data;
        var pr = key != null ? P(key) : null;
        string sys, userJson;
        if (pr != null)
        {
            userJson = JsonSerializer.Serialize(new { process = BuildProcess(key), workload = BuildWorkload(key) });
            sys = "Ты — старший аналитик аппарата руководителя. На основе JSON с метриками ОДНОГО процесса («" + pr.Name + "») " +
                "дай краткую сводку для первого лица. Верни ровно три раздела с заголовками в отдельных строках: " +
                "'1. На что обратить внимание', '2. Тренды', '3. Кто хуже всех справляется и вероятные причины'. " +
                "Маркированные пункты (через '- '), с реальными цифрами из данных, без воды. Связывай причины: возвраты " +
                "на доработку, формальные решения, узкие горлышки, перегруз, скорость взятия в работу. По-русски.";
        }
        else
        {
            userJson = JsonSerializer.Serialize(GetBundleCached());
            sys = "Ты — старший аналитик аппарата руководителя региона. На основе JSON с метриками процессов " +
                "(поручения, обращения граждан, НПА) дай краткую управленческую сводку для первого лица. " +
                "Верни ровно три раздела с заголовками в отдельных строках: '1. На что обратить внимание', " +
                "'2. Тренды', '3. Кто хуже всех справляется и вероятные причины'. В каждом разделе — маркированные " +
                "пункты (через '- '), конкретно, с реальными цифрами из данных, без воды и без выдумок. " +
                "Связывай причины: возвраты на доработку, формальные решения (закрыто за минуты), узкие горлышки, перегруз. " +
                "В данных есть и структура тематик обращений граждан (поле 'тематика_обращений': разделы/темы, топ вопросов, куда направлено) — " +
                "используй её для выводов о СОДЕРЖАНИИ обращений (о чём чаще пишут, куда чаще направляют), а не только о сроках. " +
                "Если данных мало — скажи об этом прямо. Отвечай по-русски.";
        }
        var messages = new object[]
        {
            new { role = "system", content = sys },
            new { role = "user", content = "Данные (JSON):\n" + userJson }
        };
        try
        {
            var text = LlmChat(messages, 1500, 0.3);
            var data = new { text, scope = pr != null ? pr.Name : "регион", generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), model = LlmModel };
            _sumCache[ck] = (data, DateTime.Now);
            return data;
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    static object BuildAiChat(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new { error = "пустой запрос" };
        var msgs = new List<object>();
        const string sys =
            "Ты — ассистент-аналитик по процессам региона для руководителя. Отвечай ТОЛЬКО на основе " +
            "предоставленного JSON с метриками (поручения, обращения граждан, НПА). В данных есть и структура тематик " +
            "обращений граждан (поле 'тематика_обращений': разделы/темы, топ вопросов, куда направлено) — используй её " +
            "для вопросов о содержании обращений. Если в данных нет ответа — " +
            "честно скажи, что данных нет. По-русски, кратко и по делу, с конкретными цифрами. Не выдумывай факты.";
        var bundleJson = JsonSerializer.Serialize(GetBundleCached());
        msgs.Add(new { role = "system", content = sys + "\n\nДанные (JSON):\n" + bundleJson });
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var m in arr.EnumerateArray())
                {
                    var role = m.TryGetProperty("role", out var rr) ? rr.GetString() : null;
                    var content = m.TryGetProperty("content", out var cc) ? cc.GetString() : null;
                    if ((role == "user" || role == "assistant") && !string.IsNullOrEmpty(content))
                        msgs.Add(new { role, content });
                }
        }
        catch (Exception ex) { return new { error = "bad request: " + ex.Message }; }
        try { return new { reply = LlmChat(msgs.ToArray(), 1000, 0.4) }; }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    // Пояснение конкретного блока на текущих данных: body {block, title, key?}
    static object BuildAiExplain(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body); var r = doc.RootElement;
            string title = r.TryGetProperty("title", out var t) ? t.GetString() : "блок";
            string key = r.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            // контекст: данные конкретного процесса (если задан) либо общий обзор
            object data = key != null && P(key) != null ? BuildProcess(key) : BuildOverview();
            var dataJson = JsonSerializer.Serialize(data);
            string sys = "Ты — аналитик процессов для руководителя. Объясни КОРОТКО (3–5 предложений) " +
                "блок дашборда по имени, опираясь ТОЛЬКО на JSON: 1) что показывает метрика, " +
                "2) что говорят текущие цифры, 3) тревожно это или нет и на что обратить внимание. " +
                "По-русски, с конкретными числами из данных, без воды.";
            var messages = new object[]
            {
                new { role = "system", content = sys },
                new { role = "user", content = "Блок: «" + title + "».\nДанные (JSON):\n" + dataJson }
            };
            return new { reply = LlmChat(messages, 500, 0.3) };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    // ---------- ИИ-харнесс: валидатор SQL ----------
    // Первый из двух рубежей (второй — read-only транзакция в SqlRun, задача 5).
    // Проверяется и исполняется ОДИН И ТОТ ЖЕ текст: комментарии вырезаются до проверки,
    // иначе валидатор смотрел бы на одно, а база получала другое.
    //
    // Раунд исправлений 1 (ревью нашло рабочие обходы, включая запись в боевую базу):
    //   1) деней-лист был словом-в-слово ("\bdblink_exec\b" не matчил себя из-за "\b" перед
    //      подчёркиванием) — теперь два списка: ключевые слова (граница с обеих сторон)
    //      и семейства опасных функций (граница только слева, справа — любой суффикс);
    //   2) "select ... into t" создавала таблицу — "into"/"merge" добавлены в ключевые слова;
    //   3) обёртка "limit 200" накладывается теперь БЕЗУСЛОВНО, проверка на своё "limit" убрана;
    //   4) вырезание комментариев/поиск ';' и запрещённых слов делается ТОЛЬКО вне строковых
    //      и идентификаторных литералов — вместо регулярки теперь посимвольный разбор;
    //   5) U&"..."/U&'...' (юникод-экранирование) отклоняется как отдельный класс конструкций,
    //      т.к. декодированное имя функции регуляркой не ловится;
    //   7) сравнения строк переведены на StringComparison.Ordinal, слова деней-листа
    //      экранированы Regex.Escape и скомпилированы один раз в static readonly, у всех
    //      регулярок выставлен matchTimeout (защита от катастрофического бэктрекинга).

    // Ключевые слова — отклоняются только как самостоятельное слово (граница \b с обеих сторон).
    // "into"/"merge" — сюда же: единственный способ SELECT-ом создать/изменить данные.
    static readonly string[] SqlDenyKeywords = {
        "insert","update","delete","drop","alter","create","truncate","grant","revoke",
        "copy","vacuum","call","do","set","reset","begin","commit","rollback",
        "into","merge"
    };

    // Семейства опасных функций — отклоняются по совпадению с началом имени (граница \b
    // только слева), т.к. в PostgreSQL это именно СЕМЕЙСТВА: dblink/dblink_exec/dblink_connect,
    // pg_read_file/pg_read_binary_file, pg_ls_dir/pg_ls_logdir, lo_import/lo_export/lo_get и т.д.
    // Через них шёл обход второго рубежа (read-only транзакции): dblink_exec открывает СВОЁ
    // соединение, pg_terminate_backend/pg_advisory_lock/set_config действуют на уровне сервера
    // или сессии и не являются просто чтением данных.
    static readonly string[] SqlDenyFamilies = {
        "dblink","pg_read","pg_ls","pg_stat_file","pg_sleep","pg_terminate","pg_cancel",
        "pg_advisory","pg_import","lo_","set_config","query_to_xml","table_to_xml","xmlparse"
    };

    static readonly TimeSpan SqlRegexTimeout = TimeSpan.FromMilliseconds(200);

    // Компилируются один раз при старте процесса, а не на каждый запрос.
    static readonly (Regex re, string word)[] SqlDenyKeywordRe =
        SqlDenyKeywords.Select(w => (new Regex(@"\b" + Regex.Escape(w) + @"\b",
            RegexOptions.Compiled, SqlRegexTimeout), w)).ToArray();
    static readonly (Regex re, string word)[] SqlDenyFamilyRe =
        SqlDenyFamilies.Select(w => (new Regex(@"\b" + Regex.Escape(w),
            RegexOptions.Compiled, SqlRegexTimeout), w)).ToArray();

    // Посимвольный разбор SQL-текста: одновременно вырезает комментарии и отслеживает
    // границы литералов, чтобы дальше искать ';' и запрещённые слова только вне них.
    // На выходе:
    //   cleaned      — исходный текст без комментариев, литералы сохранены дословно
    //                  (это база для итогового "effective");
    //   codeOnlyLow  — тот же текст в нижнем регистре, но СОДЕРЖИМОЕ ЛИТЕРАЛОВ заменено
    //                  на пробел (это база для поиска ';' и запрещённых слов — то, что
    //                  реально является SQL-кодом, а не данными внутри строки/идентификатора).
    static bool TrySplitSqlIntoCleanAndCode(string sql, out string cleaned, out string codeOnlyLow, out string error)
    {
        cleaned = null; codeOnlyLow = null; error = null;
        var cleanedSb = new StringBuilder(sql.Length);
        var codeSb = new StringBuilder(sql.Length);
        int i = 0, n = sql.Length;

        while (i < n)
        {
            char c = sql[i];

            // U&"..." / U&'...' — юникод-экранированные идентификаторы и строки. PostgreSQL
            // декодирует \XXXX внутри них ДО того, как имя попадёт в парсер, поэтому обычный
            // поиск подстрок ("pg_sleep" и т.п.) такую конструкцию не видит в принципе.
            // Проще и надёжнее запретить саму конструкцию целиком, чем декодировать escape.
            if ((c == 'U' || c == 'u') && i + 2 < n && sql[i + 1] == '&' && (sql[i + 2] == '"' || sql[i + 2] == '\''))
            {
                error = "юникод-экранирование идентификаторов/строк (U&\"...\" / U&'...') запрещено";
                return false;
            }

            // Строчный комментарий -- ... до конца строки.
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                i += 2;
                while (i < n && sql[i] != '\n') i++;
                codeSb.Append(' ');
                // перевод строки (если есть) оставляем в cleaned как разделитель токенов
                if (i < n) { cleanedSb.Append('\n'); i++; }
                continue;
            }

            // Блочный комментарий /* ... */ — с поддержкой вложенности, как у самого PostgreSQL.
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                int depth = 1; i += 2;
                while (i < n && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < n && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (sql[i] == '*' && i + 1 < n && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                if (depth > 0) { error = "незакрытый комментарий /* ... */"; return false; }
                codeSb.Append(' ');
                continue;
            }

            // Одинарные кавычки — строковый литерал. '' внутри — экранированная кавычка.
            // Литерал вида E'...' (или e'...') дополнительно понимает обратный слэш как
            // экранирование следующего символа — иначе '\' + закрывающая кавычка внутри
            // такого литерала выглядела бы для нас как настоящее закрытие строки раньше,
            // чем это увидит PostgreSQL, и часть "текста после литерала" на самом деле
            // ещё была бы данными — здесь это и есть тот самый неучтённый случай.
            if (c == '\'')
            {
                bool isEscapeString = IsStandaloneELetterBefore(sql, i);
                int start = i;
                cleanedSb.Append(c); codeSb.Append(' ');
                i++;
                bool closed = false;
                while (i < n)
                {
                    if (isEscapeString && sql[i] == '\\')
                    {
                        cleanedSb.Append(sql[i]);
                        i++;
                        if (i >= n) break;
                        cleanedSb.Append(sql[i]);
                        i++;
                        continue;
                    }
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < n && sql[i + 1] == '\'') { cleanedSb.Append("''"); i += 2; continue; }
                        cleanedSb.Append('\''); i++; closed = true; break;
                    }
                    cleanedSb.Append(sql[i]); i++;
                }
                if (!closed) { error = "незакрытый строковый литерал, начатый в позиции " + start; return false; }
                continue;
            }

            // Двойные кавычки — идентификатор в кавычках. "" внутри — экранированная кавычка.
            // Обратный слэш здесь ничего не экранирует (это правило только для строк).
            if (c == '"')
            {
                int start = i;
                cleanedSb.Append(c); codeSb.Append(' ');
                i++;
                bool closed = false;
                while (i < n)
                {
                    if (sql[i] == '"')
                    {
                        if (i + 1 < n && sql[i + 1] == '"') { cleanedSb.Append("\"\""); i += 2; continue; }
                        cleanedSb.Append('"'); i++; closed = true; break;
                    }
                    cleanedSb.Append(sql[i]); i++;
                }
                if (!closed) { error = "незакрытый идентификатор в двойных кавычках, начатый в позиции " + start; return false; }
                continue;
            }

            // Долларовая кавычка $$...$$ или $tag$...$tag$.
            if (c == '$' && TryMatchDollarTag(sql, i, out var tag))
            {
                int contentStart = i + tag.Length;
                int closeIdx = sql.IndexOf(tag, contentStart, StringComparison.Ordinal);
                if (closeIdx < 0) { error = "незакрытая долларовая кавычка " + tag + " ... " + tag; return false; }
                int literalEnd = closeIdx + tag.Length;
                cleanedSb.Append(sql, i, literalEnd - i);
                codeSb.Append(' ');
                i = literalEnd;
                continue;
            }

            cleanedSb.Append(c);
            codeSb.Append(char.ToLowerInvariant(c));
            i++;
        }

        cleaned = cleanedSb.ToString();
        codeOnlyLow = codeSb.ToString();
        return true;
    }

    // "Голая" буква E/e непосредственно перед открывающей кавычкой (без пробела) и без
    // предшествующих буквенно-цифровых символов — признак escape-строки E'...'.
    static bool IsStandaloneELetterBefore(string sql, int quoteIndex)
    {
        int p = quoteIndex - 1;
        if (p < 0) return false;
        char c = sql[p];
        if (c != 'e' && c != 'E') return false;
        if (p - 1 >= 0)
        {
            char prev = sql[p - 1];
            if (char.IsLetterOrDigit(prev) || prev == '_') return false;
        }
        return true;
    }

    // Проверяет, начинается ли в позиции i долларовая кавычка ($$ или $tag$), и возвращает
    // саму метку целиком (включая оба знака доллара). Метка не может начинаться с цифры —
    // иначе параметры подготовленных запросов вида $1, $2 ошибочно принимались бы за кавычку.
    static bool TryMatchDollarTag(string sql, int i, out string tag)
    {
        tag = null;
        int n = sql.Length;
        if (i >= n || sql[i] != '$') return false;
        int j = i + 1;
        if (j < n && sql[j] == '$') { tag = "$$"; return true; }
        if (j < n && (char.IsLetter(sql[j]) || sql[j] == '_'))
        {
            int k = j + 1;
            while (k < n && (char.IsLetterOrDigit(sql[k]) || sql[k] == '_')) k++;
            if (k < n && sql[k] == '$') { tag = sql.Substring(i, k - i + 1); return true; }
        }
        return false;
    }

    static (bool ok, string reason, string effective) SqlCheck(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return (false, "пустой запрос", null);

        if (!TrySplitSqlIntoCleanAndCode(sql, out var cleaned, out var codeLow, out var parseError))
            return (false, parseError, null);

        cleaned = cleaned.Trim();
        codeLow = codeLow.Trim();

        // ';' ищем только в "коде" (вне литералов) — иначе string_agg(x, '; ') отклонялся бы
        // как "два оператора", хотя ';' там — данные, а не разделитель операторов.
        // Допускается ровно один ';' и только в самом конце (после него — не более чем
        // пробелы/остатки комментариев); всё прочее — несколько операторов.
        int firstSemi = codeLow.IndexOf(';');
        if (firstSemi >= 0)
        {
            bool onlyOneAtEnd = codeLow.IndexOf(';', firstSemi + 1) < 0
                                 && codeLow.Substring(firstSemi + 1).Trim().Length == 0;
            if (!onlyOneAtEnd) return (false, "разрешён только один оператор", null);
            // тот же завершающий ';' убираем и из исполняемого текста
            int cutAt = cleaned.LastIndexOf(';');
            cleaned = (cutAt >= 0 ? cleaned.Substring(0, cutAt) : cleaned).TrimEnd();
        }

        // Ключевые слова — по границе с обеих сторон (см. объявление SqlDenyKeywordRe).
        foreach (var (re, word) in SqlDenyKeywordRe)
            if (re.IsMatch(codeLow))
                return (false, "запрещённая конструкция: " + word, null);

        // Семейства опасных функций — по границе только слева (см. SqlDenyFamilyRe).
        foreach (var (re, word) in SqlDenyFamilyRe)
            if (re.IsMatch(codeLow))
                return (false, "запрещённая конструкция: " + word, null);

        // Запрещённые слова проверяем раньше требования "начинается с SELECT/WITH":
        // иначе "drop table x" отклонялся бы с общей причиной "должен начинаться с SELECT",
        // маскируя настоящий повод отказа — попытку изменить данные.
        var codeLowTrimStart = codeLow.TrimStart();
        if (!(codeLowTrimStart.StartsWith("select", StringComparison.Ordinal)
              || codeLowTrimStart.StartsWith("with", StringComparison.Ordinal)))
            return (false, "запрос должен начинаться с SELECT или WITH", null);

        // Обёртка накладывается БЕЗУСЛОВНО, вне зависимости от того, есть ли в запросе
        // собственный LIMIT: собственный LIMIT может относиться к вложенному подзапросу
        // и не ограничивать внешний результат, а может быть намеренно завышен моделью/
        // пользователем. select * from (...) t limit 200 корректен и для ORDER BY,
        // и для UNION, и для WITH — все они являются валидным подзапросом в скобках.
        var eff = "select * from (" + cleaned + ") t limit 200";
        return (true, null, eff);
    }

    // ---------- helpers ----------
    static long ScalarL(NpgsqlConnection c, string sql) { using var cmd = new NpgsqlCommand(sql, c); var o = cmd.ExecuteScalar(); return (o == null || o is DBNull) ? 0 : Convert.ToInt64(o); }
    static double ScalarD(NpgsqlConnection c, string sql) { using var cmd = new NpgsqlCommand(sql, c); var o = cmd.ExecuteScalar(); return (o == null || o is DBNull) ? 0 : Convert.ToDouble(o); }
}
