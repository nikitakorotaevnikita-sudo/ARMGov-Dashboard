// ============================================================
// EmployeePicker.tsx — диалог «Параметры виджета — контрольные сотрудники».
// Разметка .dcp-* из референса RX. Рендер рядом с .wrap (не внутри): у .wrap
// container-type, иначе position:fixed схлопнется. Esc / фокус / overflow body —
// с cleanup (WORKAROUND-03 платформы). z-index 10000 — WORKAROUND-01.
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

export const EmployeePicker: React.FC<EmployeePickerProps> = ({
  employees,
  selected,
  onApply,
  onClose,
}) => {
  const [draft, setDraft] = useState<string[]>(selected);
  const [query, setQuery] = useState('');
  const searchRef = useRef<HTMLInputElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    previousFocusRef.current = document.activeElement as HTMLElement | null;
    searchRef.current?.focus();

    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    const prevOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = prevOverflow;
      previousFocusRef.current?.focus();
    };
  }, [onClose]);

  const toggle = (id: string) =>
    setDraft(prev => (prev.includes(id) ? prev.filter(x => x !== id) : [...prev, id]));

  const visible = employees.filter(e => matches(e, query));

  return (
    <div
      className='dcp-ov'
      role='dialog'
      aria-modal='true'
      aria-label='Параметры виджета — контрольные сотрудники'
      onClick={e => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div className='dcp' onClick={e => e.stopPropagation()}>
        <div className='dcp-h'>
          <span className='dcp-ht'>Параметры виджета — контрольные сотрудники</span>
          <Ti name='x' className='rx-arm-x' title='Закрыть' onClick={onClose} />
        </div>
        <div className='dcp-s'>
          <Ti name='search' />
          <input
            ref={searchRef}
            type='search'
            placeholder='Поиск по ФИО или должности'
            value={query}
            onChange={e => setQuery(e.target.value)}
          />
        </div>
        <div className='dcp-list'>
          {visible.length === 0 ? (
            <div className='dcp-none'>Никого не найдено</div>
          ) : (
            visible.map(e => {
              const on = draft.includes(e.id);
              return (
                <div
                  key={e.id}
                  className={on ? 'dcp-it rx-arm-on' : 'dcp-it'}
                  onClick={() => toggle(e.id)}
                >
                  <span className='dcp-cb'>{on ? <Ti name='check' /> : null}</span>
                  {e.photo ? (
                    <img className='armp-ph' src={e.photo} alt='' />
                  ) : (
                    <span className='armp-ph' />
                  )}
                  <span className='dcp-txt'>
                    <span className='dcp-n'>{e.name}</span>
                    <span className='dcp-d'>{e.position}</span>
                  </span>
                  <span className='dcp-t'>{e.inWork} в работе</span>
                </div>
              );
            })
          )}
        </div>
        <div className='dcp-f'>
          <span className='dcp-cnt'>
            Выбрано: {draft.length} из {employees.length}
          </span>
          <button type='button' className='rx-arm-tbtn' onClick={onClose}>
            Отменить
          </button>
          <button
            type='button'
            className='rx-arm-tbtn rx-arm-primary'
            onClick={() => onApply(draft)}
          >
            <Ti name='check' />
            Применить
          </button>
        </div>
      </div>
    </div>
  );
};
