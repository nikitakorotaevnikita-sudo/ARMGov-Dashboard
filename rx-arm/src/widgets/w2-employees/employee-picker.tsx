// ============================================================
// EmployeePicker — диалог выбора сотрудников.
// createPortal → document.body (CSS Modules + --theme_* / :root токены).
// WORKAROUND-01: z-index 10000. WORKAROUND-03: Esc / focus / body overflow.
// ============================================================
import React, { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Ti } from '../../shared/icons';
import { cx } from '../../shared/cx';
import { Employee } from './types';
import { matches } from './data';

export interface EmployeePickerProps {
  employees: Employee[];
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

  const ui = (
    <div
      className={cx('dcp-ov')}
      role='dialog'
      aria-modal='true'
      aria-label='Параметры виджета — контрольные сотрудники'
      onClick={e => {
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div className={cx('dcp')} onClick={e => e.stopPropagation()}>
        <div className={cx('dcp-h')}>
          <span className={cx('dcp-ht')}>Параметры виджета — контрольные сотрудники</span>
          <Ti name='x' className={cx('rx-arm-x')} title='Закрыть' onClick={onClose} />
        </div>
        <div className={cx('dcp-s')}>
          <Ti name='search' />
          <input
            ref={searchRef}
            type='search'
            placeholder='Поиск по ФИО или должности'
            value={query}
            onChange={e => setQuery(e.target.value)}
          />
        </div>
        <div className={cx('dcp-list')}>
          {visible.length === 0 ? (
            <div className={cx('dcp-none')}>Никого не найдено</div>
          ) : (
            visible.map(e => {
              const on = draft.includes(e.id);
              return (
                <div
                  key={e.id}
                  className={cx('dcp-it', on && 'rx-arm-on')}
                  onClick={() => toggle(e.id)}
                >
                  <span className={cx('dcp-cb')}>{on ? <Ti name='check' /> : null}</span>
                  {e.photo ? (
                    <img className={cx('armp-ph')} src={e.photo} alt='' />
                  ) : (
                    <span className={cx('armp-ph')} />
                  )}
                  <span className={cx('dcp-txt')}>
                    <span className={cx('dcp-n')}>{e.name}</span>
                    <span className={cx('dcp-d')}>{e.position}</span>
                  </span>
                  <span className={cx('dcp-t')}>{e.inWork} в работе</span>
                </div>
              );
            })
          )}
        </div>
        <div className={cx('dcp-f')}>
          <span className={cx('dcp-cnt')}>
            Выбрано: {draft.length} из {employees.length}
          </span>
          <button type='button' className={cx('rx-arm-tbtn')} onClick={onClose}>
            Отменить
          </button>
          <button
            type='button'
            className={cx('rx-arm-tbtn', 'rx-arm-primary')}
            onClick={() => onApply(draft)}
          >
            <Ti name='check' />
            Применить
          </button>
        </div>
      </div>
    </div>
  );

  return createPortal(ui, document.body);
};
