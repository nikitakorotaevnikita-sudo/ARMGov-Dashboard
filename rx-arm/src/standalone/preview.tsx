// ============================================================
// Preview.tsx — standalone без хоста RX: тот же экран, что loader,
// плюс ширина и тема. Для Night инжектим --theme_* как rx-cover /
// стенд RX-веб (паритет с onControlUpdate на хосте).
// ============================================================
import React, { useEffect, useState } from 'react';
import { Theme } from '@directum/sungero-remote-component-types';
import { ArmRoot } from '../shared/arm-root';
import { Dashboard } from '../dashboard/dashboard';

const WIDTHS: { label: string; value: string }[] = [
  { label: 'Вся ширина', value: '100%' },
  { label: '1280 px', value: '1280px' },
  { label: '1000 px', value: '1000px' },
  { label: '760 px', value: '760px' },
];

/** Значения --theme_* с живого стенда RX-веб (см. rx-cover Preview). */
const DARK_THEME_VARS: Record<string, string> = {
  '--theme_background': '#1e1e1e',
  '--theme_widget-background': '#353535',
  '--theme_widget-border-color': '#4a4a4a',
  '--theme_widget-box-shadow-color': 'rgba(49,67,82,.1)',
  '--theme_hover': '#3f3f3f',
  '--theme_text-primary': '#f4f4f4',
  '--theme_text-secondary': '#999999',
  '--theme_text-tertiary': '#8a8a8a',
  '--theme_text-placeholder': '#bdbdbd',
  '--theme_text-link': '#6fb3f2',
  '--theme_text-link-hover': '#ff9900',
  '--theme_green-entity-color': '#227709',
  '--theme_warning-text-brush': '#ff8600',
  '--theme_high-importance-text-brush': '#ff7c7c',
  '--theme_grid-row-splitter-color': '#464646',
  '--theme_border-color-dark': '#5f5e5e',
  '--theme_icon-color': '#9199a0',
  '--theme_primary-selected-light': '#2e3b4a',
};

const Preview: React.FC = () => {
  const [width, setWidth] = useState('100%');
  const [theme, setTheme] = useState<Theme>(Theme.Default);
  const night = theme === Theme.Night;

  useEffect(() => {
    const root = document.documentElement;
    if (night) {
      Object.entries(DARK_THEME_VARS).forEach(([name, value]) =>
        root.style.setProperty(name, value),
      );
    } else {
      Object.keys(DARK_THEME_VARS).forEach(name => root.style.removeProperty(name));
    }
    return () => {
      Object.keys(DARK_THEME_VARS).forEach(name => root.style.removeProperty(name));
    };
  }, [night]);

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
          borderBottom: `1px solid ${night ? '#4a4a4a' : '#dfe0e3'}`,
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
              border: `1px solid ${night ? '#5f5e5e' : '#dfe0e3'}`,
              borderRadius: 4,
              padding: '5px 10px',
              cursor: 'pointer',
              fontFamily: 'inherit',
              fontSize: 12.5,
              background:
                width === w.value ? (night ? '#2e3b4a' : '#e0f0fc') : night ? '#353535' : '#fff',
              color: night ? '#f4f4f4' : '#121416',
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
            border: `1px solid ${night ? '#5f5e5e' : '#dfe0e3'}`,
            borderRadius: 4,
            padding: '5px 10px',
            cursor: 'pointer',
            fontFamily: 'inherit',
            fontSize: 12.5,
            background: night ? '#353535' : '#fff',
            color: night ? '#f4f4f4' : '#121416',
          }}
          title='Проверка data-theme + --theme_* (как onControlUpdate у хоста)'
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
