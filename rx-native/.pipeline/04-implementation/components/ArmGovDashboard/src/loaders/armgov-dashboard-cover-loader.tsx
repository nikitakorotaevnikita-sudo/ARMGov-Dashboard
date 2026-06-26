import React from 'react';
import { createRoot, Root } from 'react-dom/client';
import { ArmGovDashboard } from '../controls/armgov-dashboard/armgov-dashboard';

/**
 * Загрузчик RC для обложки модуля (scope Cover).
 * Монтирует React-компонент в переданный хостом DOM-контейнер и возвращает cleanup.
 * Сигнатура — по SDK @directum/sungero-remote-component-types (ILoaderArgs).
 */
export default {
  scope: 'Cover',
  load: (args: { container: HTMLElement }) => {
    const root: Root = createRoot(args.container);
    root.render(<ArmGovDashboard />);
    return () => root.unmount(); // ControlCleanupCallback
  },
};
