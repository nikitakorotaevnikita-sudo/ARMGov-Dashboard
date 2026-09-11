// ============================================================
// tokens.ts — цвета, которые нужны в инлайновых стилях (иконки в шапках карточек).
// Вся остальная палитра живёт CSS-переменными в arm.css — дословно из макета,
// чтобы дизайн не «поехал» при переносе в контрол.
//
// Соответствие токенам темы хоста RX (для шага, когда виджет начнут красить темой):
//   --card   #FFFFFF → var(--theme_widget-background)
//   --border #DDE3ED → var(--theme_widget-border-color)
//   --text   #1C2B3A → var(--theme_text-primary)
//   --muted  #6B7A90 → var(--theme_text-secondary)
//   --hint   #8A98B0 → var(--theme_text-tertiary)
//   --link   #1A73E8 → var(--theme_text-link)
//   --green  #2E9F0C → var(--theme_green-entity-color)
//   --amber  #E37400 → var(--theme_warning-text-brush)
//   --red    #D93025 → var(--theme_high-importance-text-brush)
// Светофорных состояний в теме хоста нет (см. WORKAROUND-01 в README), поэтому
// green/amber/red остаются локальными в любом случае.
// ============================================================

export const ARM = {
  navy: '#13406D',
  create: '#1A73E8',
  orange: '#FF8600',
  green: '#2E9F0C',
} as const;
