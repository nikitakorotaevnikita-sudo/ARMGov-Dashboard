// cx.ts — склейка локальных классов CSS Modules (имена как в макете).
import type { CSSProperties } from 'react';
import styles from './arm.module.css';

export type ArmClass = keyof typeof styles | string;

/** Резолвит одно или несколько имён классов макета в hashed CSS Modules. */
export function cx(...parts: Array<ArmClass | false | null | undefined>): string {
  return parts
    .filter(Boolean)
    .map(p => {
      const key = String(p);
      return (styles as Record<string, string>)[key] ?? key;
    })
    .join(' ');
}

export { styles as armStyles };

/** Стабильные инлайн-стили (аудит: не создавать объект на каждый рендер). */
export const SPAN_STYLE: Record<number, CSSProperties> = {
  12: { gridColumn: 'span 12' },
  6: { gridColumn: 'span 6' },
};
