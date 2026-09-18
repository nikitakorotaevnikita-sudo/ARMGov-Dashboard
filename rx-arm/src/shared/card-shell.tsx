// ============================================================
// card-shell.tsx — карточка виджета (макет). Стили — CSS Modules через cx().
// ============================================================
import React from 'react';
import { Ti, TiName } from './icons';
import { SPAN_STYLE, cx } from './cx';

export interface CardProps {
  icon: TiName;
  iconColor: string;
  title: string;
  onSettings?: () => void;
  span?: number;
  children: React.ReactNode;
}

const ICON_COLORS: Record<string, React.CSSProperties> = {};
function iconStyle(color: string): React.CSSProperties {
  let s = ICON_COLORS[color];
  if (!s) {
    s = { color };
    ICON_COLORS[color] = s;
  }
  return s;
}

export const Card: React.FC<CardProps> = ({
  icon,
  iconColor,
  title,
  onSettings,
  span = 12,
  children,
}) => (
  <section className={cx('rx-arm-card')} style={SPAN_STYLE[span] ?? SPAN_STYLE[12]}>
    <div className={cx('rx-arm-card-head')}>
      <Ti name={icon} className={cx('rx-arm-wico')} style={iconStyle(iconColor)} />
      <span className={cx('rx-arm-ct')}>{title}</span>
      {onSettings ? (
        <Ti
          name='settings'
          className={cx('rx-arm-ci')}
          title='Параметры виджета'
          onClick={onSettings}
        />
      ) : null}
    </div>
    <div className={cx('rx-arm-card-body')}>{children}</div>
  </section>
);
