import React, { useState } from 'react';
import { Card } from '../../shared/card-shell';
import { Ti } from '../../shared/icons';
import { ARM } from '../../shared/tokens';
import { cx } from '../../shared/cx';
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
    <Card icon='eye-check' iconColor={ARM.orange} title='Мои контрольные поручения'>
      <div className={cx('armt-tabs')}>
        {TABS.map(t => (
          <span
            key={t.id}
            className={cx('armt-tab', t.id === tab && 'rx-arm-on')}
            onClick={() => setTab(t.id)}
          >
            {t.label}
            <span className={cx('rx-arm-n')}>{byTab(data.orders, t.id).length}</span>
          </span>
        ))}
      </div>

      {shown.map(o => (
        <div key={o.id} className={cx('armr')}>
          <span className={cx('armr-m')}>
            <span className={cx('armr-t', o.status === 'overdue' && 'rx-arm-over')}>{o.title}</span>
            <span className={cx('armr-n')}>{o.note}</span>
          </span>
          <span className={cx(...dueTone(o.status).split(/\s+/))}>{o.due}</span>
          <span className={cx('armr-s', o.status === 'overdue' && 'rx-arm-over')}>
            <i />
            {o.status === 'overdue' ? 'Просрочено' : 'В работе'}
          </span>
        </div>
      ))}

      {rest > 0 ? (
        <div className={cx('rx-arm-footer-link')}>
          <Ti name='arrow-narrow-right' />
          <a href='#' onClick={e => e.preventDefault()}>
            Показать все — ещё {rest}
          </a>
        </div>
      ) : null}
    </Card>
  );
};
