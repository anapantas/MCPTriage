import http from 'node:http';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { UnauthorizedError } from '@modelcontextprotocol/sdk/client/auth.js';
import { FileOAuthProvider, authFile, callbackPort, tenant } from './auth.js';

// Signs in to the tenant once (browser, OAuth + PKCE) and stores the tokens; `npm run tokens` prints them.
if (!tenant) {
  console.error('Set OS_TENANT to your tenant hostname, e.g. OS_TENANT=mycompany.outsystems.dev npm run login');
  process.exit(1);
}

function waitForCode() {
  let server;
  const code = new Promise((resolve, reject) => {
    server = http.createServer((req, res) => {
      const url = new URL(req.url, `http://localhost:${callbackPort}`);
      if (url.pathname !== '/callback') {
        res.writeHead(404).end();
        return;
      }
      const error = url.searchParams.get('error');
      const value = url.searchParams.get('code');
      // Plain text: the error comes from the URL, so it must never be read as HTML
      res.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end(error ? `Sign-in failed: ${error}` : 'Signed in. You can close this tab.');
      if (error) reject(new Error(`Sign-in failed: ${error} ${url.searchParams.get('error_description') || ''}`));
      else if (value) resolve(value);
    });
    server.on('error', reject);
    server.listen(callbackPort, '127.0.0.1');
    setTimeout(() => reject(new Error('No sign-in within 5 minutes')), 5 * 60_000).unref();
  });
  return { code, close: () => server.close() };
}

const mcpUrl = new URL(`https://${tenant}/mcp`);
const provider = new FileOAuthProvider();
const callback = waitForCode();
const newClient = () => new Client({ name: 'mcptriage-signin', version: '1.0.0' });
let client = newClient();
const transport = new StreamableHTTPClientTransport(mcpUrl, { authProvider: provider });

try {
  try {
    await client.connect(transport);
    console.log(`Already signed in to ${tenant}.`);
  } catch (err) {
    if (!(err instanceof UnauthorizedError)) throw err;
    await transport.finishAuth(await callback.code);
    client = newClient();
    await client.connect(new StreamableHTTPClientTransport(mcpUrl, { authProvider: provider }));
    console.log(`Signed in to ${tenant}.`);
  }
  const status = await client.callTool({ name: 'auth_status', arguments: {} });
  const loggedIn = JSON.parse(status.content?.[0]?.text || '{}').logged_in;
  console.log(`auth_status: logged_in = ${loggedIn}. Tokens saved to ${authFile}. Run npm run tokens to print them.`);
  await client.close();
} catch (err) {
  console.error(err.message || err);
  process.exitCode = 1;
} finally {
  callback.close();
}
