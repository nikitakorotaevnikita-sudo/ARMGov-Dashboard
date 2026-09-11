// ============================================================
// OrgOrders.tsx — блок 1 «Поручения организации»: четыре KPI-плитки.
// Разметка дословно из макета: .armk > .armk-t[.orange|.red] > .armk-v + .armk-l.
// ============================================================
import React from 'react';
import { Card } from '../../shared/CardShell';
import { ARM } from '../../shared/tokens';
import { OrgOrdersData } from './types';
import { orgTiles } from './data';

export interface OrgOrdersProps {
  data: OrgOrdersData;
}

export const OrgOrders: React.FC<OrgOrdersProps> = ({ data }) => (
  <Card icon="clipboard-list" iconColor={ARM.navy} title="Поручения организации">
    <div className="armk">
      {orgTiles(data).map((t) => (
        <div key={t.label} className={t.tone === 'normal' ? 'armk-t' : `armk-t ${t.tone}`}>
          <div className="armk-v">{t.value}</div>
          <div className="armk-l">{t.label}</div>
        </div>
      ))}
    </div>
  </Card>
);
