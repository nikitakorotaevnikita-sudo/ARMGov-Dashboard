// ============================================================
// widget-loader.tsx — фабрика cover-загрузчиков. Канон:
// createRoot → render(ArmRoot + виджет) → cleanup = unmount + сброс onControlUpdate.
//
// api.onControlUpdate синхронизирует тему/культуру с хостом (смена темы RX-веб).
// Логгер хоста храним в ref на будущее; шумных логов нет.
// Данные виджетов пока из пресетов — Cover API (executeAction и т.п.) не зовём.
// ============================================================
import React, { useEffect, useRef, useState } from 'react';
import { createRoot, Root } from 'react-dom/client';
import type {
  ControlCleanupCallback,
  ILoaderArgs,
  IRemoteComponentContext,
  ILogger,
} from '@directum/sungero-remote-component-types';
import { ArmRoot } from '../shared/arm-root';
// styles: tokens+tabler via ArmRoot; layout via arm.module.css (cx)

const ControlApp: React.FC<{ args: ILoaderArgs; Widget: React.ComponentType }> = ({
  args,
  Widget,
}) => {
  const [ctx, setCtx] = useState<IRemoteComponentContext>(args.initialContext);
  const loggerRef = useRef<ILogger>(args.initialContext.logger);

  useEffect(() => {
    loggerRef.current = ctx.logger;
  }, [ctx.logger]);

  useEffect(() => {
    const { api } = args;
    api.onControlUpdate = next => {
      loggerRef.current = next.logger;
      setCtx(next);
    };
    return () => {
      api.onControlUpdate = undefined;
    };
  }, [args]);

  return (
    <ArmRoot theme={ctx.theme} culture={ctx.currentCulture}>
      <Widget />
    </ArmRoot>
  );
};

export function makeWidgetLoader(
  Widget: React.ComponentType,
): (args: ILoaderArgs) => Promise<ControlCleanupCallback> {
  return (args: ILoaderArgs) => {
    const root: Root = createRoot(args.container);
    root.render(<ControlApp args={args} Widget={Widget} />);
    return Promise.resolve(() => root.unmount());
  };
}
