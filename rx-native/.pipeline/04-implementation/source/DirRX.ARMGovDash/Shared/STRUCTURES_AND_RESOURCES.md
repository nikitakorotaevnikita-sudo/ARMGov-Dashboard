# DDS-узлы для ARMGovDash (создать через directum-scaffold / DDS, НЕ руками .mtd)

## Structure (узел «Структуры и константы»)
**`RegionKpi`** — `Structures.Module.RegionKpi` (доступ `.Create()`, интерфейс `IRegionKpi`):
| Поле | Тип |
|------|-----|
| `Total`   | int |
| `InWork`  | int |
| `Overdue` | int |

Используется `GetRegionKpi()` (Server/ModuleServerFunctions.cs).

## Resource-ключи (Module*.resx; валидатор Check9 требует DisplayName)
| Ключ | ru |
|------|----|
| (DisplayName модуля) | **АРМ Руководителя** |
| `Role_Manager` | Руководитель (АРМ) |
| `Role_Admin` | Администратор АРМ |
| `KpiQueryFailed_Error` | Не удалось получить KPI региона |

Префиксы ключей — по схеме (`Error_`, и т.п., см. валидатор/architect-kb). `KpiQueryFailed_Error` — суффикс `_Error` допустим.

## Идентичность модуля (Task 1, через scaffold)
- Имя: `ARMGovDash`, код компании `DirRX`, DBName `ARMGD`, свежий GUID, **IsSolution=true**, Work-слой, **без enum**.
