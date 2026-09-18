// Точка входа standalone-режима (webpack --env mode=standalone):
// экран рендерится без хоста RX, чтобы смотреть вёрстку в браузере.
// На стенде тот же экран монтирует loader из component.loaders.ts.
import React from 'react';
import { createRoot } from 'react-dom/client';
import Preview from './src/standalone/preview';
// CSS: tokens/tabler via ArmRoot; modules via components

const container = document.getElementById('app');
if (container) {
  createRoot(container).render(React.createElement(Preview));
}
