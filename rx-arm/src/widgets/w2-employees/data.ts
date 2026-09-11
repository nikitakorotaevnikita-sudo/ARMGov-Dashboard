// ============================================================
// data.ts — демо-состав сотрудников блока «Статус исполнения поручений».
// Цифры и состав — из макета 2026-09-04-mvp-screen-rx.html, не из стенда.
// По умолчанию выбраны все: лимита по количеству сотрудников в блоке нет.
// ============================================================
import { Employee, EmployeesData } from './types';
import { DEMO_PHOTOS } from './photos';

export const EMPLOYEES: Employee[] = [
  { id: 'sokolov', name: 'Соколов И.П.', position: 'Начальник отдела инфраструктуры', photo: DEMO_PHOTOS.sokolov, inWork: 84, dueToday: 3, overdue: 9 },
  { id: 'morozova', name: 'Морозова Е.А.', position: 'Главный специалист', photo: DEMO_PHOTOS.morozova, inWork: 38, dueToday: 2, overdue: 5 },
  { id: 'gusev', name: 'Гусев В.В.', position: 'Заместитель руководителя', photo: DEMO_PHOTOS.gusev, inWork: 47, dueToday: 1, overdue: 4 },
  { id: 'kuznecova', name: 'Кузнецова Э.Э.', position: 'Начальник отдела ГИС', photo: DEMO_PHOTOS.kuznecova, inWork: 31, dueToday: 0, overdue: 2 },
  { id: 'nikitin', name: 'Никитин А.К.', position: 'Советник', photo: DEMO_PHOTOS.nikitin, inWork: 22, dueToday: 0, overdue: 0 },
  { id: 'volkov', name: 'Волков Н.Ф.', position: 'Специалист', photo: DEMO_PHOTOS.volkov, inWork: 19, dueToday: 0, overdue: 0 },
  { id: 'lebedeva', name: 'Лебедева О.С.', position: 'Начальник правового отдела', photo: DEMO_PHOTOS.lebedeva, inWork: 44, dueToday: 2, overdue: 6 },
  { id: 'kovalev', name: 'Ковалёв Д.А.', position: 'Заместитель начальника управления', photo: DEMO_PHOTOS.kovalev, inWork: 36, dueToday: 1, overdue: 3 },
  { id: 'titova', name: 'Титова М.В.', position: 'Главный специалист', photo: DEMO_PHOTOS.titova, inWork: 28, dueToday: 0, overdue: 1 },
  { id: 'ershov', name: 'Ершов П.Н.', position: 'Ведущий специалист', photo: DEMO_PHOTOS.ershov, inWork: 25, dueToday: 1, overdue: 0 },
  { id: 'belova', name: 'Белова А.И.', position: 'Начальник отдела кадров', photo: DEMO_PHOTOS.belova, inWork: 17, dueToday: 0, overdue: 0 },
  { id: 'zaiceva', name: 'Зайцева Н.П.', position: 'Специалист 1 категории', photo: DEMO_PHOTOS.zaiceva, inWork: 14, dueToday: 0, overdue: 0 },
];

export const PRESET: EmployeesData = {
  employees: EMPLOYEES,
  selected: EMPLOYEES.map((e) => e.id),
};

/** Сотрудник «под риском»: есть просроченные поручения. */
export function isAtRisk(e: Employee): boolean {
  return e.overdue > 0;
}

/** Фильтр диалога: поиск по ФИО без учёта регистра. */
export function matches(e: Employee, query: string): boolean {
  const q = query.trim().toLowerCase();
  return !q || e.name.toLowerCase().includes(q);
}
