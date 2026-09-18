import React from 'react';
import { Card } from '../../shared/card-shell';
import { ARM } from '../../shared/tokens';
import { cx } from '../../shared/cx';
import { ProcessHealthData } from './types';
import { healthColor } from './data';

export interface ProcessHealthProps {
  data: ProcessHealthData;
}

/** Стабильные стили полосы здоровья по цвету (аудит: не new object каждый рендер). */
const BAR_BY_COLOR: Record<string, React.CSSProperties> = {};
function barStyle(widthPct: number, color: string): React.CSSProperties {
  const key = `${widthPct}|${color}`;
  let s = BAR_BY_COLOR[key];
  if (!s) {
    s = { width: `${widthPct}%`, background: color };
    BAR_BY_COLOR[key] = s;
  }
  return s;
}

export const ProcessHealth: React.FC<ProcessHealthProps> = ({ data }) => (
  <Card icon='heartbeat' iconColor={ARM.green} title='Здоровье процесса «Поручения»'>
    <table className={cx('armtl')}>
      <thead>
        <tr>
          <th>Вид поручений</th>
          <th>Всего</th>
          <th>Просрочено</th>
          <th>Здоровье</th>
        </tr>
      </thead>
      <tbody>
        {data.rows.map(r => (
          <tr key={r.kind}>
            <td>{r.kind}</td>
            <td>{r.total}</td>
            <td className={cx('rx-arm-over')}>{r.overdue}</td>
            <td>
              <span className={cx('armtl-p')}>
                <span className={cx('armtl-b')}>
                  <i style={barStyle(r.health, healthColor(r, data.healthThreshold))} />
                </span>
                <b>{r.health}%</b>
              </span>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  </Card>
);
