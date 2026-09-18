// ============================================================
// Dashboard.tsx — экран АРМ руководителя целиком: то, что ставится на обложку RX
// одним спец-контролом. Порядок и ширины блоков — из макета 2026-09-04-mvp-screen-rx.html:
// 12-колоночная сетка .cfgw-grid, все четыре блока во всю ширину (span 12).
//
//   1. Поручения организации        — KPI по срокам
//   2. Статус исполнения по сотрудникам — состав задаёт руководитель (диалог)
//   3. Мои контрольные поручения    — список с вкладками
//   4. Здоровье процесса «Поручения»
//
// Диалог выбора сотрудников — сосед .wrap, а не потомок: у .wrap включён container-type
// (контейнерные запросы вместо медиа-запросов макета), а он делает элемент containing block
// для position:fixed — оверлей внутри .wrap схлопнулся бы до ширины контейнера.
// ============================================================
import React, { useState } from 'react';
import { OrgOrders } from '../widgets/w1-org-orders/OrgOrders';
import { PRESET as ORG_PRESET } from '../widgets/w1-org-orders/data';
import { Employees } from '../widgets/w2-employees/Employees';
import { EmployeePicker } from '../widgets/w2-employees/EmployeePicker';
import { PRESET as EMP_PRESET } from '../widgets/w2-employees/data';
import { MyControl } from '../widgets/w3-my-control/MyControl';
import { PRESET as CONTROL_PRESET } from '../widgets/w3-my-control/data';
import { ProcessHealth } from '../widgets/w4-process-health/ProcessHealth';
import { PRESET as HEALTH_PRESET } from '../widgets/w4-process-health/data';
import { OrgOrdersData } from '../widgets/w1-org-orders/types';
import { EmployeesData } from '../widgets/w2-employees/types';
import { MyControlData } from '../widgets/w3-my-control/types';
import { ProcessHealthData } from '../widgets/w4-process-health/types';
import '../shared/arm.css';

export interface DashboardProps {
  org?: OrgOrdersData;
  employees?: EmployeesData;
  control?: MyControlData;
  health?: ProcessHealthData;
}

export const Dashboard: React.FC<DashboardProps> = ({
  org = ORG_PRESET,
  employees = EMP_PRESET,
  control = CONTROL_PRESET,
  health = HEALTH_PRESET,
}) => {
  const [selected, setSelected] = useState<string[]>(employees.selected);
  const [pickerOpen, setPickerOpen] = useState(false);

  return (
    <>
      {/* Класс cfgw обязателен: он включает вариант шапки виджета из собранного рабочего
          стола (заголовок 15.5px / 600 / --text, паддинги 13-16) — именно он в макете.
          Без него срабатывает базовое правило .card-head .ct (13px / 600). */}
      <div className='rx-arm-wrap rx-arm-cfgw'>
        <div className='rx-arm-cfgw-grid'>
          <OrgOrders data={org} />
          <Employees
            employees={employees.employees}
            selected={selected}
            onOpenPicker={() => setPickerOpen(true)}
          />
          <MyControl data={control} />
          <ProcessHealth data={health} />
        </div>
      </div>

      {pickerOpen ? (
        <EmployeePicker
          employees={employees.employees}
          selected={selected}
          onApply={next => {
            setSelected(next);
            setPickerOpen(false);
          }}
          onClose={() => setPickerOpen(false)}
        />
      ) : null}
    </>
  );
};
