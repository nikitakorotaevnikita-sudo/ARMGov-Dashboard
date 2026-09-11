// ============================================================
// EmployeePicker.tsx — диалог «Параметры виджета — контрольные сотрудники».
// Разметка и поведение — из референса рабочего стола RX (.dcp-*): поиск, строки с фото,
// чип «N в работе» справа, счётчик «Выбрано: N из M», кнопки «Применить»/«Отменить».
//
// Отличие от референса — осознанное: лимита в шесть сотрудников нет (решение заказчика
// на макете 04.09), поэтому нет и заблокированных строк .armp-dis, и поясняющей подписи
// .armp-note про «не более шести». Класс .armp-dis в arm.css оставлен на случай возврата лимита.
//
// Черновик выбора живёт внутри диалога: «Применить» отдаёт его наружу, «Отменить»/Esc/×/
// клик по фону просто закрывают — при следующем открытии диалог снова показывает применённое.
// ============================================================
import React, { useEffect, useRef, useState } from 'react';
import { Ti } from '../../shared/icons';
import { Employee } from './types';
import { matches } from './data';

export interface EmployeePickerProps {
  employees: Employee[];
  /** Применённый состав блока — с него начинается черновик при каждом открытии. */
  selected: string[];
  onApply: (selected: string[]) => void;
  onClose: () => void;
}

export const EmployeePicker: React.FC<EmployeePickerProps> = ({ employees, selected, onApply, onClose }) => {
  const [draft, setDraft] = useState<string[]>(selected);
  const [query, setQuery] = useState('');
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    searchRef.current?.focus();
  }, []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  const toggle = (id: string) =>
    setDraft((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id]));

  const visible = employees.filter((e) => matches(e, query));

  return (
    <div className="dcp-ov" onClick={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="dcp">
        <div className="dcp-h">
          <h3>Параметры виджета — контрольные сотрудники</h3>
          <Ti name="x" className="x" title="Закрыть" onClick={onClose} />
        </div>

        <div className="dcp-s">
          <Ti name="search" />
          <input
            ref={searchRef}
            placeholder="Укажите сотрудника..."
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        </div>

        <div className="dcp-list">
          <div className="dcp-g">Сотрудники моей организации</div>
          {visible.map((e) => {
            const on = draft.includes(e.id);
            return (
              <div
                key={e.id}
                className={on ? 'dcp-it armp-it on' : 'dcp-it armp-it'}
                onClick={() => toggle(e.id)}
              >
                <span className="dcp-cb">{on ? <Ti name="check" /> : null}</span>
                <img className="armp-ph" src={e.photo} alt="" />
                <span className="dcp-txt">
                  <span className="dcp-n">{e.name}</span>
                  <span className="dcp-d">{e.position}</span>
                </span>
                <span className="dcp-t">{e.inWork} в работе</span>
              </div>
            );
          })}
          {visible.length === 0 ? <div className="dcp-none">Ничего не найдено</div> : null}
        </div>

        <div className="dcp-f">
          <span className="dcp-cnt">
            Выбрано: {draft.length} из {employees.length}
          </span>
          <button type="button" className="tbtn primary" onClick={() => onApply(draft)}>
            <Ti name="check" />
            Применить
          </button>
          <button type="button" className="tbtn ghost" onClick={onClose}>
            Отменить
          </button>
        </div>
      </div>
    </div>
  );
};
