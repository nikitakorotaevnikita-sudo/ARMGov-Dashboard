// ============================================================
// Employees.tsx — блок 2 «Статус исполнения поручений по сотрудникам»: сетка карточек
// по выбранным сотрудникам. Разметка дословно из макета: .arme > .arme-c[.risk] >
// .arme-h (фото + ФИО + должность), три строки .arme-r, подвал .arme-f.
//
// Состав блока приходит пропсом: выбором владеет экран (Dashboard), потому что диалог
// выбора рендерится вне контейнера .wrap — иначе его position:fixed схлопнулся бы
// до размеров контейнера (см. комментарий в arm.css про container-type).
// ============================================================
import React from 'react';
import { Card } from '../../shared/CardShell';
import { ARM } from '../../shared/tokens';
import { Employee } from './types';
import { isAtRisk } from './data';

export interface EmployeesProps {
  employees: Employee[];
  selected: string[];
  onOpenPicker: () => void;
}

export const Employees: React.FC<EmployeesProps> = ({ employees, selected, onOpenPicker }) => {
  const shown = employees.filter((e) => selected.includes(e.id));

  return (
    <Card
      icon="users"
      iconColor={ARM.link}
      title="Статус исполнения поручений по сотрудникам"
      onSettings={onOpenPicker}
    >
      <div className="arme">
        {shown.map((e) => (
          <EmployeeCard key={e.id} employee={e} />
        ))}
      </div>
      {shown.length === 0 ? <div className="arme-none">Сотрудники не выбраны</div> : null}
    </Card>
  );
};

const EmployeeCard: React.FC<{ employee: Employee }> = ({ employee: e }) => {
  const risk = isAtRisk(e);
  return (
    <article className={risk ? 'arme-c risk' : 'arme-c'}>
      <div className="arme-h">
        <img className="arme-ph" src={e.photo} alt="" />
        <div>
          <div className="arme-nm">{e.name}</div>
          <div className="arme-pos">{e.position}</div>
        </div>
      </div>
      <div className="arme-r">
        <span className="l">В работе</span>
        <span className="c">{e.inWork}</span>
      </div>
      <div className="arme-r">
        <span className="l">Срок сегодня</span>
        <span className={e.dueToday > 0 ? 'c a' : 'c'}>{e.dueToday}</span>
      </div>
      <div className="arme-r">
        <span className="l">Просрочено</span>
        <span className={risk ? 'c r' : 'c'}>{e.overdue}</span>
      </div>
      {/* Ссылки «Поручения в RX» в подвале нет: в список поручений будем проваливаться
          по клику на саму карточку — обработчик появится вместе с переходом. */}
      <div className="arme-f">
        <span className={risk ? 'arme-tag bad' : 'arme-tag ok'}>{risk ? 'Просрочки' : 'В норме'}</span>
      </div>
    </article>
  );
};
