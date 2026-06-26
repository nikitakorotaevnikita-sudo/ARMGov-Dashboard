import React, { useEffect, useState } from 'react';
import { ArmGovDashboardView, RegionKpi } from './armgov-dashboard-view';
import { fetchRegionKpi } from '../../host-api';

/**
 * Контейнерный компонент обложки: грузит RegionKpi из server-функции модуля
 * (через host-api) и отдаёт во view. Логика загрузки отделена от презентации.
 */
export const ArmGovDashboard: React.FC = () => {
  const [kpi, setKpi] = useState<RegionKpi | undefined>(undefined);
  const [error, setError] = useState<string | undefined>(undefined);

  useEffect(() => {
    let alive = true;
    fetchRegionKpi()
      .then((d) => { if (alive) setKpi(d); })
      .catch((e) => { if (alive) setError(String(e)); });
    return () => { alive = false; };
  }, []);

  return <ArmGovDashboardView kpi={kpi} error={error} />;
};
