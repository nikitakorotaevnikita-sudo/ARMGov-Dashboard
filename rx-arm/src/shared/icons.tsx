// ============================================================
// icons.tsx — иконки Tabler как шрифт (сабсет из мокапа рабочего стола RX, @font-face в arm.css).
// В отличие от rx-cover, где иконки — инлайновый SVG: здесь важна дословная передача макета,
// а в макете используются те же глифы Tabler, что и в референсе RX.
// Доступные глифы: settings, search, x, check, arrow-narrow-right, users, clipboard-list,
// eye-check, heartbeat. Новый глиф = пересборка сабсета (см. README § Иконки).
// ============================================================
import React from 'react';

export type TiName =
  | 'settings'
  | 'search'
  | 'x'
  | 'check'
  | 'arrow-narrow-right'
  | 'users'
  | 'clipboard-list'
  | 'eye-check'
  | 'heartbeat';

export interface TiProps {
  name: TiName;
  className?: string;
  title?: string;
  style?: React.CSSProperties;
  onClick?: () => void;
}

export const Ti: React.FC<TiProps> = ({ name, className, title, style, onClick }) => (
  <i
    className={className ? `ti ti-${name} ${className}` : `ti ti-${name}`}
    title={title}
    style={style}
    onClick={onClick}
    aria-hidden={title ? undefined : true}
  />
);
