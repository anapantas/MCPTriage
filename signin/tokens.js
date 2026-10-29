import { randomBytes } from 'node:crypto';
import { FileOAuthProvider, authFile, tenant } from './auth.js';

// Prints the stored sign-in in the shape the McpTriage library takes (McpConnection), to seed an ODC app.
const stored = new FileOAuthProvider().read();
if (!stored.client?.client_id || !stored.tokens?.refresh_token) {
  console.error(`No sign-in in ${authFile}: run npm run login first`);
  process.exit(1);
}

const expiry = (token) => {
  try {
    const { exp } = JSON.parse(Buffer.from(token.split('.')[1], 'base64url'));
    return new Date(exp * 1000).toISOString();
  } catch {
    return 'unknown';
  }
};

console.log(`TenantHostname: ${tenant || '(set OS_TENANT)'}`);
console.log(`ClientId:       ${stored.client.client_id}`);
console.log(`RefreshToken:   ${stored.tokens.refresh_token}`);
console.log(`AccessToken:    ${stored.tokens.access_token || ''}`);
console.log(`\nThis sign-in stops working at ${expiry(stored.tokens.refresh_token)}; after that, run npm run login again.`);
console.log('Treat these values as passwords: store them as secret settings or in a protected entity.');
// A new random key each run: use it only when the app has no McpTokenKey yet, since changing the key makes stored tokens unreadable
console.log(`\nTokenKey, only if the app has none yet (secret Setting McpTokenKey): ${randomBytes(32).toString('base64url')}`);
