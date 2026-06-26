using System;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.ARMGovDash.Server
{
  public partial class ModuleFunctions
  {
    /// <summary>
    /// Региональный KPI для обложки (скелет): всего задач / в работе / просрочено
    /// по процессам «Поручения» и «Обращения граждан».
    /// </summary>
    /// <remarks>
    /// Антипаттерны (соблюдено):
    ///   - данные через платформенный raw-SQL API <c>ExecuteScalarSQLCommand</c> (НЕ System.Data; паттерн прежнего ARMGov, ADR-003);
    ///   - НЕ <c>GetAll().Count()</c> без Where (canon #4);
    ///   - тяжёлый агрегат — за <c>Cache</c> (TTL 60с);
    ///   - ошибки — <c>AppliedCodeException</c> (canon #2);
    ///   - <c>Calendar.Now</c> (не DateTime.Now);
    ///   - возврат — Structure (не анонимный тип).
    /// Несколько значений возвращаем одной строкой через <c>||';'||</c> и парсим (ExecuteScalarSQLCommand = скаляр).
    /// </remarks>
    [Public]
    public virtual Structures.Module.IRegionKpi GetRegionKpi()
    {
      Structures.Module.IRegionKpi cached;
      if (Cache.TryGetValue(Constants.Module.Cache.RegionKpi, out cached))
        return cached;

      try
      {
        var sql =
          "select count(*)::text||';'||" +
          "count(*) filter (where t.status::text='InProcess')::text||';'||" +
          "count(*) filter (where exists (select 1 from sungero_wf_assignment a where a.task=t.id " +
            "and a.status::text='InProcess' and a.deadline is not null and a.deadline<now()))::text " +
          "from sungero_wf_task t " +
          "where t.discriminator in ('" + Constants.Module.Process.Poruchenia + "','" +
          Constants.Module.Process.Appeals + "')";

        var raw = Sungero.Docflow.PublicFunctions.Module.ExecuteScalarSQLCommand(sql);
        var parts = (raw ?? "0;0;0").Split(';');

        var res = Structures.Module.RegionKpi.Create();
        res.Total   = int.Parse(parts[0]);
        res.InWork  = int.Parse(parts[1]);
        res.Overdue = int.Parse(parts[2]);

        Cache.AddOrUpdate(Constants.Module.Cache.RegionKpi, res, Calendar.Now.AddMinutes(1));
        return res;
      }
      catch (Exception ex)
      {
        throw AppliedCodeException.Create(ARMGovDash.Resources.KpiQueryFailed_Error, ex);
      }
    }
  }
}
