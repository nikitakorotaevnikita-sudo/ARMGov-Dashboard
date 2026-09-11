// ============================================================
// types.ts — контракт блока «Мои контрольные поручения»: поручения, где руководитель
// контролёр, с разбивкой по срокам. Вкладки — фильтр по тому же списку.
// ============================================================

export type OrderStatus = 'inWork' | 'dueToday' | 'overdue';
export type ControlTab = 'all' | 'overdue' | 'dueToday';

export interface ControlOrder {
  id: string;
  /** Тема поручения. */
  title: string;
  /** Регистрационный номер и ответственный — вторая строка. */
  note: string;
  /** Текст срока: «просрочено 42 дн», «срок сегодня», «через 3 дня». */
  due: string;
  status: OrderStatus;
}

export interface MyControlData {
  orders: ControlOrder[];
  /** Сколько строк показывать в блоке; остальные — за ссылкой «Показать все». */
  pageSize: number;
}
