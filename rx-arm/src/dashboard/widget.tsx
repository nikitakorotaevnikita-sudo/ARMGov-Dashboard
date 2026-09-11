// ============================================================
// widget.tsx — контрол-обёртка без пропсов: то, что регистрируется в реестре загрузчиков
// и ставится на обложку модуля «специальным контролом» (scope Cover).
// Данные — из демо-пресетов виджетов (см. data.ts каждого блока).
// ============================================================
import React from 'react';
import { Dashboard } from './Dashboard';

export const ArmDashboardWidget: React.FC = () => <Dashboard />;

export default ArmDashboardWidget;
