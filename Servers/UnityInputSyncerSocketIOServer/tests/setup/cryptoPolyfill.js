// Polyfill globalThis.crypto for Node < 19 — see nakama-server's identical
// setup file for the rationale. Tests in this repo reference
// crypto.randomUUID() as a Web Crypto global.
const nodeCrypto = require('node:crypto');
if (typeof globalThis.crypto === 'undefined' && nodeCrypto.webcrypto) {
  Object.defineProperty(globalThis, 'crypto', {
    value: nodeCrypto.webcrypto,
    configurable: true,
    writable: true,
  });
}
