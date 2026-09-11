// ============================================================
// MyControl.tsx — блок 3 «Мои контрольные поручения»: вкладки-фильтры + список строк
// + ссылка «Показать все». Разметка дословно из макета: .armt-tabs > .armt-tab[.on] > .n,
// строки .armr (.armr-m > .armr-t + .armr-n, .armr-d, .armr-s[.over]), подвал .w-footer-link.
//
// Правило 5 дизайн-гайда: в виджете не более пяти строк, остальное — за ссылкой.
// Счётчики вкладок считаются по данным, а не проставлены руками (см. data.ts).
// ============================================================
import React, { useState } from 'react';
import { Card } from '../../shared/CardShell';
import { Ti } from '../../shared/icons';
import { ARM } from '../../shared/tokens';
import { ControlTab, MyControlData } from './types';
import { byTab, dueTone, TABS } from './data';

export interface MyControlProps {
  data: MyControlData;
}

export const MyControl: React.FC<MyControlProps> = ({ data }) => {
  const [tab, setTab] = useState<ControlTab>('all');

  const filtered = byTab(data.orders, tab);
  const shown = filtered.slice(0, data.pageSize);
  const rest = filtered.length - shown.length;

  return (
    <Card icon="eye-check" iconColor={ARM.orange} title="Мои контрольные поручения">
      <div className="armt-tabs">
        {TABS.map((t) => (
          <span
            key={t.id}
            className={t.id === tab ? 'armt-tab on' : 'armt-tab'}
            onClick={() => setTab(t.id)}
          >
            {t.label}
            <span className="n">{byTab(data.orders, t.id).length}</span>
          </span>
        ))}
      </div>

      {shown.map((o) => (
        <div key={o.id} className="armr">
          <span className="armr-m">
            <span className="armr-t">{o.title}</span>
            <span className="armr-n">{o.note}</span>
          </span>
          <span className={dueTone(o.status)}>{o.due}</span>
          <span className={o.status === 'overdue' ? 'armr-s over' : 'armr-s'}>
            <i />
            {o.status === 'overdue' ? 'Просрочено' : 'В работе'}
          </span>
        </div>
      ))}

      {rest > 0 ? (
        <div className="w-footer-link">
          <Ti name="arrow-narrow-right" />
          <a href="#0">Показать все — ещё {rest}</a>
        </div>
      ) : null}
    </Card>
  );
};
