import { RegionKpi } from './controls/armgov-dashboard/armgov-dashboard-view';
import { stubRegionKpi } from './host-api-stub';

// Standalone-режим (webpack --env mode=standalone) → отдаём заглушку, без сети.
const isStandalone =
  typeof process !== 'undefined' && (process as any).env && (process as any).env.RC_MODE === 'standalone';

/**
 * Получить RegionKpi от модуля DirRX.ARMGovDash.
 *
 * ОТКРЫТЫЙ ПУНКТ (резолв при интеграции в HANDOFF-сессии, см. design §7):
 * точный транспорт RC → server. Кандидат — OData-действие над PublicFunction
 * `GetRegionKpi` модуля: GET {IntegrationOdataBase}/ARMGovDash/GetRegionKpi.
 * Сверить по эталону CRM RC, как он зовёт серверные функции в сессии хоста.
 */
export async function fetchRegionKpi(): Promise<RegionKpi> {
  if (isStandalone) return stubRegionKpi;

  // TODO(integration): заменить на проверенный путь RC→server (OData/PublicApi) по эталону CRM RC.
  const resp = await fetch('/Integration/odata/ARMGovDash/GetRegionKpi', {
    headers: { Accept: 'application/json' },
    credentials: 'include', // сессия RX
  });
  if (!resp.ok) throw new Error('HTTP ' + resp.status);
  const json = await resp.json();
  const v = json.value ?? json;
  return { total: v.Total ?? 0, inWork: v.InWork ?? 0, overdue: v.Overdue ?? 0 };
}
