// ============================================================
// host-actions.ts — безопасный вызов Cover/Card executeAction.
// Аудит RC: всегда canExecuteAction перед executeAction (как в гайде платформы).
// ============================================================
import type { IRemoteComponentCardApi } from '@directum/sungero-remote-component-types';

/** Минимальный контракт API с действиями (Cover / Card). */
export interface ActionHostApi {
  canExecuteAction: (actionName: string) => boolean;
  executeAction: (actionName: string) => void | Promise<void>;
}

export type SafeExecuteResult =
  { ok: true } | { ok: false; reason: 'forbidden' | 'error'; error?: unknown };

/**
 * Вызвать действие хоста только если canExecuteAction === true.
 * Не бросает наружу — возвращает результат для UI/логгера.
 */
export async function safeExecuteAction(
  api: ActionHostApi | IRemoteComponentCardApi | null | undefined,
  actionName: string,
): Promise<SafeExecuteResult> {
  if (!api || typeof api.canExecuteAction !== 'function') {
    return { ok: false, reason: 'forbidden' };
  }
  if (!api.canExecuteAction(actionName)) {
    return { ok: false, reason: 'forbidden' };
  }
  try {
    await Promise.resolve(api.executeAction(actionName));
    return { ok: true };
  } catch (error) {
    return { ok: false, reason: 'error', error };
  }
}
