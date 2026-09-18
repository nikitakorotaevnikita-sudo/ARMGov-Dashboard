import React from 'react';
import { Card } from '../../shared/card-shell';
import { ARM } from '../../shared/tokens';
import { cx } from '../../shared/cx';
import { OrgOrdersData } from './types';
import { orgTiles } from './data';

export interface OrgOrdersProps {
  data: OrgOrdersData;
}

export const OrgOrders: React.FC<OrgOrdersProps> = ({ data }) => (
  <Card icon='clipboard-list' iconColor={ARM.navy} title='Поручения организации'>
    <div className={cx('armk')}>
      {orgTiles(data).map(t => (
        <div key={t.label} className={cx('armk-t', t.tone !== 'normal' && `rx-arm-${t.tone}`)}>
          <div className={cx('armk-v')}>{t.value}</div>
          <div className={cx('armk-l')}>{t.label}</div>
        </div>
      ))}
    </div>
  </Card>
);
