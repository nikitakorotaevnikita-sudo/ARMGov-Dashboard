// ============================================================
// ProcessHealth.tsx — блок 4 «Здоровье процесса „Поручения“»: таблица по видам поручений.
// Разметка дословно из макета: .armtl с шапкой, ячейка просрочки .over,
// полоса здоровья .armtl-p > .armtl-b > i (ширина = % здоровья) + <b>NN%</b>.
// ============================================================
import React from 'react';
import { Card } from '../../shared/CardShell';
import { ARM } from '../../shared/tokens';
import { ProcessHealthData } from './types';
import { healthColor } from './data';

export interface ProcessHealthProps {
  data: ProcessHealthData;
}

export const ProcessHealth: React.FC<ProcessHealthProps> = ({ data }) => (
  <Card icon="heartbeat" iconColor={ARM.green} title="Здоровье процесса «Поручения»">
    <table className="armtl">
      <thead>
        <tr>
          <th>Вид поручений</th>
          <th>Всего</th>
          <th>Просрочено</th>
          <th>Здоровье</th>
        </tr>
      </thead>
      <tbody>
        {data.rows.map((r) => (
          <tr key={r.kind}>
            <td>{r.kind}</td>
            <td>{r.total}</td>
            <td className="over">{r.overdue}</td>
            <td>
              <span className="armtl-p">
                <span className="armtl-b">
                  <i style={{ width: `${r.health}%`, background: healthColor(r, data.healthThreshold) }} />
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
