// ============================================================
// widget-loader.tsx — фабрика cover-загрузчиков. Канон (как в rx-cover):
// createRoot → render(корневая обёртка + виджет) → cleanup = root.unmount().
//
// Корневая обёртка .rx-arm-root обязательна: под ней живут все стили контрола
// (arm.css целиком отскоплен этим классом), без неё виджет отрисуется голым HTML.
// api (IRemoteComponentCoverApi) на этом этапе не используется — данные из пресетов.
// ============================================================
import React from 'react';
import { createRoot, Root } from 'react-dom/client';
import type { ControlCleanupCallback, ILoaderArgs } from '@directum/sungero-remote-component-types';
import '../shared/arm.css';

export function makeWidgetLoader(Widget: React.ComponentType): (args: ILoaderArgs) => Promise<ControlCleanupCallback> {
  return (args: ILoaderArgs) => {
    const root: Root = createRoot(args.container);
    root.render(
      <div className="rx-arm-root">
        <Widget />
      </div>,
    );
    return Promise.resolve(() => root.unmount());
  };
}
