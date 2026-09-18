// ============================================================
// Preview.tsx — standalone-превью без хоста RX: тот же экран, что монтирует loader,
// плюс переключатель ширины и темы (проверка data-theme / паритет с onControlUpdate).
// Обвязка превью — инлайн-стили, без классов контрола.
// ============================================================
import React, { useState } from 'react';
import { Theme } from '@directum/sungero-remote-component-types';
import { ArmRoot } from '../shared/ArmRoot';
import { Dashboard } from '../dashboard/Dashboard';

const WIDTHS: { label: string; value: string }[] = [
  { label: 'Вся ширина', value: '100%' },
  { label: '1280 px', value: '1280px' },
  { label: '1000 px', value: '1000px' },
  { label: '760 px', value: '760px' },
];

const Preview: React.FC = () => {
  const [width, setWidth] = useState('100%');
  const [theme, setTheme] = useState<Theme>(Theme.Default);
  const night = theme === Theme.Night;

  return (
    <div
      style={{
        minHeight: '100vh',
        background: night ? '#1e1e1e' : '#fff',
        fontFamily: '"Segoe UI", Tahoma, sans-serif',
      }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          padding: '10px 14px',
          background: night ? '#353535' : '#fff',
          borderBottom: '1px solid #dfe0e3',
          fontSize: 13,
          color: night ? '#f4f4f4' : '#121416',
        }}
      >
        <strong style={{ marginRight: 8 }}>АРМ руководителя · превью контрола</strong>
        {WIDTHS.map(w => (
          <button
            key={w.value}
            type='button'
            onClick={() => setWidth(w.value)}
            style={{
              border: '1px solid #dfe0e3',
              borderRadius: 4,
              padding: '5px 10px',
              cursor: 'pointer',
              fontFamily: 'inherit',
              fontSize: 12.5,
              background: width === w.value ? '#e0f0fc' : '#fff',
              color: '#121416',
            }}
          >
            {w.label}
          </button>
        ))}
        <button
          type='button'
          onClick={() => setTheme(t => (t === Theme.Default ? Theme.Night : Theme.Default))}
          style={{
            marginLeft: 'auto',
            border: '1px solid #dfe0e3',
            borderRadius: 4,
            padding: '5px 10px',
            cursor: 'pointer',
            fontFamily: 'inherit',
            fontSize: 12.5,
            background: '#fff',
            color: '#121416',
          }}
          title='Проверка data-theme (как onControlUpdate у хоста)'
        >
          Тема: {night ? 'Night' : 'Default'}
        </button>
      </div>

      <div style={{ width, margin: '0 auto', transition: 'width .15s' }}>
        <ArmRoot theme={theme}>
          <Dashboard />
        </ArmRoot>
      </div>
    </div>
  );
};

export default Preview;
