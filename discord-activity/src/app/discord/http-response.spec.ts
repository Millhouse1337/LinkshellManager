import { describe, expect, it, vi } from 'vitest';
import { nonJsonMessage, readJsonResponse, withCorrelationId } from './http-response';

function reply(body: string, status: number, contentType = 'application/json'): Response {
  return new Response(body, { status, headers: { 'Content-Type': contentType } });
}

describe('readJsonResponse', () => {
  it('returns the parsed body on success', async () => {
    await expect(readJsonResponse<{ a: number }>(reply('{"a":1}', 200))).resolves.toEqual({ a: 1 });
  });

  it('returns an empty object for an empty success body', async () => {
    await expect(readJsonResponse(reply('', 200))).resolves.toEqual({});
  });

  it("throws the server's own error message, with its error id", async () => {
    await expect(readJsonResponse(reply('{"error":"Nope.","correlationId":"abc"}', 400)))
      .rejects.toThrow('Nope. (Error id: abc)');
  });

  it('never puts an HTML error page into the message', async () => {
    const html = '<!doctype html><html><body><h1>Bad Gateway</h1></body></html>';
    const error = await readJsonResponse(reply(html, 502, 'text/html')).catch((e: Error) => e);
    expect((error as Error).message).toBe(
      'The server is temporarily unavailable (HTTP 502). Please try again in a moment.');
    const login = await readJsonResponse(reply(html, 404, 'text/html')).catch((e: Error) => e);
    expect((login as Error).message).not.toContain('<');
  });

  it('treats an HTML page answered with 200 as a failure, not as data', async () => {
    await expect(readJsonResponse(reply('<html></html>', 200, 'text/html')))
      .rejects.toThrow('Your session may have expired');
  });

  it('tells the caller about a 401', async () => {
    const onUnauthorized = vi.fn();
    await readJsonResponse(reply('{"error":"Sign in."}', 401), { onUnauthorized }).catch(() => undefined);
    expect(onUnauthorized).toHaveBeenCalledOnce();
  });
});

describe('nonJsonMessage', () => {
  it('calls a crash a server problem, not an expired session', () => {
    expect(nonJsonMessage(500, false, '<html>')).toBe('Something went wrong on the server (HTTP 500).');
  });

  it('falls back to the status for anything else', () => {
    expect(nonJsonMessage(418, false, 'teapot')).toBe('The server returned an unexpected response (status 418).');
  });
});

describe('withCorrelationId', () => {
  it('leaves a body without an id as it is', () => {
    expect(withCorrelationId({ error: 'Nope.' })).toBe('Nope.');
  });
});
