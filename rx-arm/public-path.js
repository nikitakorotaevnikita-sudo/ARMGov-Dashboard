// public-path.js — критично для Module Federation (канон example-react).
// Хост кладёт publicPath атрибутом на <script> remoteEntry.js.
try {
  // eslint-disable-next-line no-undef, camelcase
  __webpack_public_path__ = document.currentScript['publicPath'];
} catch (e) {
  /* standalone / отсутствие атрибута — остаётся output.publicPath */
}
