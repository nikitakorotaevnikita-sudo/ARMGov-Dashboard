import React from 'react';

export interface RegionKpi {
  total: number;
  inWork: number;
  overdue: number;
}

const tile: React.CSSProperties = {
  border: '1px solid var(--rndx-theme_border-color, #d7dde7)',
  borderRadius: 8,
  padding: '12px 18px',
  minWidth: 120,
  background: 'var(--rndx-theme_background-color, #fff)',
};

const Kpi: React.FC<{ label: string; value: number; accent?: string }> = ({ label, value, accent }) => (
  <div style={tile}>
    <div style={{ fontSize: 26, fontWeight: 700, color: accent || 'var(--rndx-theme_text-color, #15202b)' }}>
      {value}
    </div>
    <div style={{ fontSize: 13, opacity: 0.7, color: 'var(--rndx-theme_text-color, #15202b)' }}>{label}</div>
  </div>
);

/** Презентационный компонент обложки: 3 KPI-плитки региона. */
export const ArmGovDashboardView: React.FC<{ kpi?: RegionKpi; error?: string }> = ({ kpi, error }) => {
  if (error) return <div style={{ color: 'var(--rndx-theme_error-color, #b23a2c)' }}>Ошибка: {error}</div>;
  if (!kpi) return <div style={{ opacity: 0.7 }}>Загрузка…</div>;
  return (
    <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', fontFamily: 'inherit' }}>
      <Kpi label="Всего" value={kpi.total} />
      <Kpi label="В работе" value={kpi.inWork} />
      <Kpi label="Просрочено" value={kpi.overdue} accent="var(--rndx-theme_error-color, #b23a2c)" />
    </div>
  );
};
