// ============================================================
// CardShell.tsx — карточка виджета: рамка, радиус, шапка с глифом и заголовком,
// опциональная шестерёнка «Параметры виджета» справа. Разметка и классы — дословно
// из макета (.card / .card-head / .wico / .ct / .ci / .card-body), стили в arm.css.
//
// Как в rx-cover, карточку рисует сам виджет, а не группа хоста. Открытый пункт тот же:
// проверить на стенде RX-веб, не рисует ли хост-группа поверх свою рамку/тень (будет дубль).
// ============================================================
import React from 'react';
import { Ti, TiName } from './icons';

export interface CardProps {
  icon: TiName;
  iconColor: string;
  title: string;
  /** Показывать шестерёнку в шапке. */
  onSettings?: () => void;
  /** Ширина в 12-колоночной сетке экрана. По макету все блоки — во всю ширину. */
  span?: number;
  children: React.ReactNode;
}

export const Card: React.FC<CardProps> = ({
  icon,
  iconColor,
  title,
  onSettings,
  span = 12,
  children,
}) => (
  <section className='rx-arm-card' style={{ gridColumn: `span ${span}` }}>
    <div className='rx-arm-card-head'>
      <Ti name={icon} className='rx-arm-wico' style={{ color: iconColor }} />
      <span className='rx-arm-ct'>{title}</span>
      {onSettings ? (
        <Ti name='settings' className='rx-arm-ci' title='Параметры виджета' onClick={onSettings} />
      ) : null}
    </div>
    <div className='rx-arm-card-body'>{children}</div>
  </section>
);
