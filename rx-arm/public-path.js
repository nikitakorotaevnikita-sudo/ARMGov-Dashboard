// public-path.js — критично для Module Federation.
// Хост Directum RX кладёт вычисленный publicPath атрибутом на <script>-тег remoteEntry.js;
// читаем его в рантайме, иначе split-чанки резолвятся от неверного base URL.
// Если атрибута нет (напр. отдельный IIS-хостинг) — остаётся output.publicPath: 'auto' из webpack.
try {
  var cs = document.currentScript;
  if (cs && cs['publicPath']) {
    // eslint-disable-next-line no-undef, camelcase
    __webpack_public_path__ = cs['publicPath'];
  }
} catch (e) {
  /* fallback → output.publicPath: 'auto' */
}
