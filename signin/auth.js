import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFile } from 'node:child_process';

export const tenant = process.env.OS_TENANT || '';
export const callbackPort = Number(process.env.CALLBACK_PORT || 8788);
// Outside the project folder so OneDrive and git never pick the tokens up
export const authFile = process.env.AUTH_FILE || path.join(os.homedir(), '.impactmap-triage-api', 'odc-library.json');

// OAuth client for the OutSystems MCP: keeps the registered client, the tokens and the PKCE verifier in one JSON file.
// The MCP SDK drives the flow (discovery, dynamic client registration, PKCE); this class only stores what it hands over.
export class FileOAuthProvider {
  read() {
    try {
      return JSON.parse(fs.readFileSync(authFile, 'utf8'));
    } catch {
      return {};
    }
  }

  write(data) {
    fs.mkdirSync(path.dirname(authFile), { recursive: true });
    fs.writeFileSync(authFile, JSON.stringify(data, null, 2), { mode: 0o600 });
  }

  get redirectUrl() {
    return `http://localhost:${callbackPort}/callback`;
  }

  get clientMetadata() {
    return {
      client_name: 'McpTriage ODC library',
      redirect_uris: [this.redirectUrl],
      grant_types: ['authorization_code', 'refresh_token'],
      response_types: ['code'],
      token_endpoint_auth_method: 'none',
    };
  }

  clientInformation() {
    return this.read().client;
  }

  saveClientInformation(client) {
    this.write({ ...this.read(), client });
  }

  tokens() {
    return this.read().tokens;
  }

  saveTokens(tokens) {
    this.write({ ...this.read(), tokens, savedAt: new Date().toISOString() });
  }

  saveCodeVerifier(codeVerifier) {
    this.write({ ...this.read(), codeVerifier });
  }

  codeVerifier() {
    const verifier = this.read().codeVerifier;
    if (!verifier) throw new Error('No sign-in in progress: run npm run login');
    return verifier;
  }

  invalidateCredentials(scope) {
    const data = this.read();
    if (scope === 'all' || scope === 'client') delete data.client;
    if (scope === 'all' || scope === 'tokens') delete data.tokens;
    if (scope === 'all' || scope === 'verifier') delete data.codeVerifier;
    this.write(data);
  }

  redirectToAuthorization(url) {
    const href = url.toString();
    console.log(`\nSign in to ${tenant} in the browser that just opened. If none opened, open this link:\n${href}\n`);
    // No shell: the link comes from the server's metadata, so it is passed as an argument, never parsed as a command
    const [command, args] = process.platform === 'win32' ? ['rundll32', ['url.dll,FileProtocolHandler', href]]
      : process.platform === 'darwin' ? ['open', [href]]
      : ['xdg-open', [href]];
    execFile(command, args, () => {});
  }
}
