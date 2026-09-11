// ============================================================
// Preview.tsx — standalone-превью без хоста RX: тот же экран, что монтирует loader,
// плюс переключатель ширины — им проверяется контейнерная раскладка (на 1100px и уже
// сетка переходит на 6 колонок, KPI-плитки — на две в ряд).
// Обвязка превью намеренно свёрстана инлайновыми стилями, без классов контрола,
// чтобы её нельзя было спутать с самим экраном.
// ============================================================
import React, { useState } from 'react';
import { Dashboard } from '../dashboard/Dashboard';

const WIDTHS: { label: string; value: string }[] = [
  { label: 'Вся ширина', value: '100%' },
  { label: '1280 px', value: '1280px' },
  { label: '1000 px', value: '1000px' },
  { label: '760 px', value: '760px' },
];

const Preview: React.FC = () => {
  const [width, setWidth] = useState('100%');

  return (
    // Подложка белая — такая же, как область обложки в RX-веб: контрол прозрачный
    // и цвет подложки берёт у хоста, превью должно показывать то же самое.
    <div style={{ minHeight: '100vh', background: '#fff', fontFamily: '"Segoe UI", Tahoma, sans-serif' }}>
      <div
        style={{
          display: 'flex', alignItems: 'center', gap: 8, padding: '10px 14px',
          background: '#fff', borderBottom: '1px solid #dfe0e3', fontSize: 13, color: '#121416',
        }}
      >
        <strong style={{ marginRight: 8 }}>АРМ руководителя · превью контрола</strong>
        {WIDTHS.map((w) => (
          <button
            key={w.value}
            type="button"
            onClick={() => setWidth(w.value)}
            style={{
              border: '1px solid #dfe0e3', borderRadius: 4, padding: '5px 10px', cursor: 'pointer',
              fontFamily: 'inherit', fontSize: 12.5,
              background: width === w.value ? '#e0f0fc' : '#fff',
            }}
          >
            {w.label}
          </button>
        ))}
      </div>

      <div style={{ width, margin: '0 auto', transition: 'width .15s' }}>
        <div className="rx-arm-root">
          <Dashboard />
        </div>
      </div>
    </div>
  );
};

export default Preview;
