// ============================================================
// arm-root.tsx — корень контрола: .rx-arm-root + data-theme / lang.
// Глобальные tokens + Tabler; компоненты тянут arm.module.css через cx().
// ============================================================
import React from 'react';
import { Theme } from '@directum/sungero-remote-component-types';
import './styles/tokens.css';
import './styles/tabler-icons.css';

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
