// ============================================================
// data.ts — демо-датасет блока «Здоровье процесса „Поручения“».
// Цифры — из макета 2026-09-04-mvp-screen-rx.html, не из стенда.
// ============================================================
import { ProcessHealthData, ProcessRow } from './types';

export const PRESET: ProcessHealthData = {
  healthThreshold: 80,
  rows: [
    { kind: 'На контроле', total: 486, overdue: 61, health: 64 },
    { kind: 'Без контроля', total: 1083, overdue: 28, health: 91 },
  ],
};

/** Цвет полосы здоровья: ниже порога — красная, иначе зелёная. Других цветов в гайде нет. */
export function healthColor(row: ProcessRow, threshold: number): string {
  return row.health < threshold ? 'var(--red)' : 'var(--green)';
}
