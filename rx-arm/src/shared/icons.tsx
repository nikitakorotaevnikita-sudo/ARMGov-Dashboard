// ============================================================
// icons.tsx — глифы UI.
// Шапочные иконки виджетов — цветные SVG из UI kit Directum RX («Обложка»).
// Системные (settings, search, x, check, arrow) — сабсет Tabler Icons 3.11.0
// (шрифт в shared/styles/tabler-icons.css): монохром, красятся через currentColor.
// ============================================================
import React from 'react';
import clipboardListSvg from './rx-icons/clipboard-list.svg';
import eyeCheckSvg from './rx-icons/eye-check.svg';
import heartbeatSvg from './rx-icons/heartbeat.svg';
import settingsSvg from './rx-icons/settings.svg';
import usersSvg from './rx-icons/users.svg';

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

/** Цветные платформенные иконки (не красятся через CSS color). */
const RX_PLATFORM: Partial<Record<TiName, string>> = {
  'users': usersSvg,
  'clipboard-list': clipboardListSvg,
  'eye-check': eyeCheckSvg,
  'heartbeat': heartbeatSvg,
  // settings оставляем Tabler в шапке карточки (монохром + hover);
  // SVG платформы доступен, если понадобится отдельно.
};

export interface TiProps {
  name: TiName;
  className?: string;
  title?: string;
  style?: React.CSSProperties;
  onClick?: () => void;
}

export const Ti: React.FC<TiProps> = ({ name, className, title, style, onClick }) => {
  const rx = RX_PLATFORM[name];
  if (rx) {
    const cls = className ? `rx-ico ${className}` : 'rx-ico';
    // color из style игнорируем — у платформенных иконок свои заливки
    const { color: _c, ...rest } = style || {};
    return (
      <img
        src={rx}
        className={cls}
        title={title}
        alt=''
        draggable={false}
        style={rest}
        onClick={onClick}
        aria-hidden={title ? undefined : true}
      />
    );
  }
  return (
    <i
      className={
        className ? `rx-arm-ti rx-arm-ti-${name} ${className}` : `rx-arm-ti rx-arm-ti-${name}`
      }
      title={title}
      style={style}
      onClick={onClick}
      aria-hidden={title ? undefined : true}
    />
  );
};

/** Платформенная шестерёнка (если понадобится вне Tabler-хрома). */
export const RxSettingsIcon: React.FC<{
  className?: string;
  title?: string;
  onClick?: () => void;
}> = ({ className, title, onClick }) => (
  <img
    src={settingsSvg}
    className={className ? `rx-ico ${className}` : 'rx-ico'}
    title={title}
    alt=''
    draggable={false}
    onClick={onClick}
    aria-hidden={title ? undefined : true}
  />
);
