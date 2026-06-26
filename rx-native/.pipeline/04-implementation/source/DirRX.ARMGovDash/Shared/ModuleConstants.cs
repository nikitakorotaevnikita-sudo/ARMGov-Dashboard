using System;

namespace DirRX.ARMGovDash.Constants
{
  /// <summary>
  /// Константы модуля АРМ Руководителя (ARMGovDash).
  /// Hardcoded-значения (GUID процессов) держим здесь, не в бизнес-логике (canon #6).
  /// </summary>
  public static class Module
  {
    /// <summary>Sid предопределённых ролей (поиск ролей — по Sid, не IncludedIn(Guid), canon #5).</summary>
    public static class RoleSid
    {
      public const string Manager = "DirRX.ARMGovDash.Manager";
      public const string Admin   = "DirRX.ARMGovDash.Admin";
    }

    /// <summary>Ключи серверного кэша (Sungero.Core.Cache).</summary>
    public static class Cache
    {
      public const string RegionKpi = "DirRX.ARMGovDash.RegionKpi";
    }

    /// <summary>
    /// Discriminator-ы процессов в sungero_wf_task (из отлаженного прототипа).
    /// Поручения и Обращения граждан — для регионального KPI скелета.
    /// </summary>
    public static class Process
    {
      public const string Poruchenia = "c290b098-12c7-487d-bb38-73e2c98f9789";
      public const string Appeals    = "4ef03457-8b42-4239-a3c5-d4d05e61f0b6";
    }
  }
}
