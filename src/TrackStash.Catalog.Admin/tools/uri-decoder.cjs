// query-string 7 expects require() to return a function; the patched decoder is ESM.
module.exports = require('patched-uri-decoder').default
