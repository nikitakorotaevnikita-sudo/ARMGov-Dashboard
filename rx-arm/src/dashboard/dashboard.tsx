// ============================================================
// Dashboard — экран АРМ на обложку RX одним Cover-контролом.
// Модалка через createPortal (employee-picker) — вне container-type .wrap.
// ============================================================
import React, { useState } from 'react';
import { OrgOrders } from '../widgets/w1-org-orders/org-orders';
import { PRESET as ORG_PRESET } from '../widgets/w1-org-orders/data';
import { Employees } from '../widgets/w2-employees/employees';
import { EmployeePicker } from '../widgets/w2-employees/employee-picker';
import { PRESET as EMP_PRESET } from '../widgets/w2-employees/data';
import { MyControl } from '../widgets/w3-my-control/my-control';
import { PRESET as CONTROL_PRESET } from '../widgets/w3-my-control/data';
import { ProcessHealth } from '../widgets/w4-process-health/process-health';
import { PRESET as HEALTH_PRESET } from '../widgets/w4-process-health/data';
import { OrgOrdersData } from '../widgets/w1-org-orders/types';
import { EmployeesData } from '../widgets/w2-employees/types';
import { MyControlData } from '../widgets/w3-my-control/types';
import { ProcessHealthData } from '../widgets/w4-process-health/types';
import { cx } from '../shared/cx';

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
      <div className={cx('rx-arm-wrap', 'rx-arm-cfgw')}>
        <div className={cx('rx-arm-cfgw-grid')}>
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
