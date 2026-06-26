using System;
using System.Linq;
using Sungero.Core;
using Sungero.CoreEntities;

namespace DirRX.ARMGovDash.Server
{
  public partial class ModuleInitializer
  {
    /// <summary>
    /// Идемпотентная инициализация модуля: предопределённые роли Manager/Admin.
    /// Поиск ролей — по Sid (canon #5), создание идемпотентно (повторный init не дублирует).
    /// </summary>
    public override void Initializing(Sungero.Domain.ModuleInitializingEventArgs e)
    {
      InitializeRole(Constants.Module.RoleSid.Manager, ARMGovDash.Resources.Role_Manager);
      InitializeRole(Constants.Module.RoleSid.Admin,   ARMGovDash.Resources.Role_Admin);
    }

    private static void InitializeRole(string sid, string name)
    {
      var role = Roles.GetAll(r => r.Sid == sid).FirstOrDefault();
      if (role == null)
      {
        role = Roles.Create();
        role.Sid = sid;
      }
      role.Name = name;
      role.Save();
    }
  }
}
