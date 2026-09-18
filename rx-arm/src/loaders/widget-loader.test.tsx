/**
 * @jest-environment jsdom
 */
import React from 'react';
import { act } from 'react-dom/test-utils';
import { Theme } from '@directum/sungero-remote-component-types';
import type { ILoaderArgs } from '@directum/sungero-remote-component-types';
import { makeWidgetLoader } from './widget-loader';
import { safeExecuteAction } from '../shared/host-actions';

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

function mockArgs(container: HTMLElement): ILoaderArgs {
  return {
    container,
    initialContext: {
      theme: Theme.Default,
      currentCulture: 'ru-RU',
      logger: { info() {}, warn() {}, error() {}, debug() {} },
    },
    api: {
      onControlUpdate: undefined,
    },
  } as unknown as ILoaderArgs;
}

describe('makeWidgetLoader', () => {
  it('mounts, syncs theme via onControlUpdate, and unmounts', async () => {
    const Stub: React.FC = () => <div data-testid='stub'>ok</div>;
    const load = makeWidgetLoader(Stub);
    const el = document.createElement('div');
    document.body.appendChild(el);
    const args = mockArgs(el);

    let cleanup: () => void = () => undefined;
    await act(async () => {
      cleanup = await load(args);
    });
    expect(el.querySelector('[data-testid="stub"]')?.textContent).toBe('ok');
    const root = el.querySelector('.rx-arm-root') as HTMLElement;
    expect(root.getAttribute('data-theme')).toBe(String(Theme.Default));

    await act(async () => {
      args.api.onControlUpdate?.({
        ...args.initialContext,
        theme: Theme.Night,
      });
    });
    expect(root.getAttribute('data-theme')).toBe(String(Theme.Night));

    await act(async () => {
      cleanup();
    });
    expect(el.querySelector('.rx-arm-root')).toBeNull();
    el.remove();
  });
});

describe('safeExecuteAction', () => {
  it('refuses when canExecuteAction is false', async () => {
    const r = await safeExecuteAction(
      {
        canExecuteAction: () => false,
        executeAction: () => {
          throw new Error('should not run');
        },
      },
      'Open',
    );
    expect(r).toEqual({ ok: false, reason: 'forbidden' });
  });

  it('runs executeAction when allowed', async () => {
    let called = false;
    const r = await safeExecuteAction(
      {
        canExecuteAction: () => true,
        executeAction: () => {
          called = true;
        },
      },
      'Open',
    );
    expect(r).toEqual({ ok: true });
    expect(called).toBe(true);
  });
});
