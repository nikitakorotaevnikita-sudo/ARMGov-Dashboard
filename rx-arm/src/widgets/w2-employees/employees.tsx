import React from 'react';
import { Card } from '../../shared/card-shell';
import { ARM } from '../../shared/tokens';
import { cx } from '../../shared/cx';
import { Employee } from './types';
import { isAtRisk } from './data';

export interface EmployeesProps {
  employees: Employee[];
  selected: string[];
  onOpenPicker: () => void;
}

export const Employees: React.FC<EmployeesProps> = ({ employees, selected, onOpenPicker }) => {
  const shown = employees.filter(e => selected.includes(e.id));

  return (
    <Card
      icon='users'
      iconColor={ARM.link}
      title='Статус исполнения поручений по сотрудникам'
      onSettings={onOpenPicker}
    >
      <div className={cx('arme')}>
        {shown.map(e => (
          <EmployeeCard key={e.id} employee={e} />
        ))}
      </div>
      {shown.length === 0 ? <div className={cx('arme-none')}>Сотрудники не выбраны</div> : null}
    </Card>
  );
};

const EmployeeCard: React.FC<{ employee: Employee }> = ({ employee: e }) => {
  const risk = isAtRisk(e);
  return (
    <article className={cx('arme-c', risk && 'rx-arm-risk')}>
      <div className={cx('arme-h')}>
        <img className={cx('arme-ph')} src={e.photo} alt='' />
        <div>
          <div className={cx('arme-nm')}>{e.name}</div>
          <div className={cx('arme-pos')}>{e.position}</div>
        </div>
      </div>
      <div className={cx('arme-r')}>
        <span className={cx('rx-arm-l')}>В работе</span>
        <span className={cx('rx-arm-c')}>{e.inWork}</span>
      </div>
      <div className={cx('arme-r')}>
        <span className={cx('rx-arm-l')}>Срок сегодня</span>
        <span className={cx('rx-arm-c', e.dueToday > 0 && 'rx-arm-a')}>{e.dueToday}</span>
      </div>
      <div className={cx('arme-r')}>
        <span className={cx('rx-arm-l')}>Просрочено</span>
        <span className={cx('rx-arm-c', risk && 'rx-arm-r')}>{e.overdue}</span>
      </div>
      <div className={cx('arme-f')}>
        <span className={cx('arme-tag', risk ? 'rx-arm-bad' : 'rx-arm-ok')}>
          {risk ? 'Просрочки' : 'В норме'}
        </span>
      </div>
    </article>
  );
};
