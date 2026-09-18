// ============================================================
// ArmRoot.tsx — корневая обёртка контрола: .rx-arm-root + data-theme / lang.
// Под ней живут все стили (arm.css отскоплен этим классом). Тема и культура
// приходят из контекста хоста (initialContext / onControlUpdate).
// ============================================================
import React from 'react';
import { Theme } from '@directum/sungero-remote-component-types';

export interface ArmRootProps {
  theme?: Theme;
  culture?: string | null;
  children: React.ReactNode;
}

export const ArmRoot: React.FC<ArmRootProps> = ({
  theme = Theme.Default,
  culture = null,
  children,
}) => (
  <div className='rx-arm-root' data-theme={theme} lang={culture || undefined}>
    {children}
  </div>
);
