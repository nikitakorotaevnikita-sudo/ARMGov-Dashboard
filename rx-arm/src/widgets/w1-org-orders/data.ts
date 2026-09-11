// ============================================================
// data.ts — демо-датасет блока «Поручения организации» + раскладка в плитки.
// Цифры — из макета 2026-09-04-mvp-screen-rx.html, не из стенда.
// ============================================================
import { OrgOrdersData, OrgTile } from './types';

export const PRESET: OrgOrdersData = {
  inWork: 142,
  dueToday: 7,
  overdue: 18,
  dueIn7: 23,
};

/** Порядок плиток в макете: в работе → срок сегодня → просрочено → истекает 7 дней. */
export function orgTiles(d: OrgOrdersData): OrgTile[] {
  return [
    { value: d.inWork, label: 'В работе', tone: 'normal' },
    { value: d.dueToday, label: 'Срок сегодня', tone: 'orange' },
    { value: d.overdue, label: 'Просрочено', tone: 'red' },
    { value: d.dueIn7, label: 'Истекает 7 дней', tone: 'normal' },
  ];
}
