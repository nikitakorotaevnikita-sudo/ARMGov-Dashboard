// Подставляет base64 сабсета Tabler Icons вместо @@FONT@@ в CSS.
const fs = require('fs');
const path = require('path');

const font = fs.readFileSync(path.join(__dirname, 'assets/tabler_sub2.b64'), 'utf8').trim();

module.exports = function fontInjectLoader(source) {
  return source.replace(/@@FONT@@/g, font);
};
