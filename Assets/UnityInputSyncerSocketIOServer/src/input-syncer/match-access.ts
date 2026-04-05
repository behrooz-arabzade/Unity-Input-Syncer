import { createHash, timingSafeEqual } from 'crypto';
import type { AdminCreateInstanceRequest } from './types';

export const MATCH_ACCESS_MAX_TOKENS = 64;
export const MATCH_ACCESS_MAX_TOKEN_LENGTH = 256;

export type MatchAccessMode = 'open' | 'password' | 'token';

function parseMatchAccessMode(
  raw: string | undefined,
  errors: string[],
): MatchAccessMode | null {
  const s = (raw ?? 'open').trim().toLowerCase();
  if (!s || s === 'open') return 'open';
  if (s === 'password') return 'password';
  if (s === 'token') return 'token';
  errors.push('matchAccess must be open, password, or token');
  return null;
}

export function validateAdminMatchAccess(
  body: AdminCreateInstanceRequest | undefined,
): string[] {
  const errors: string[] = [];
  if (!body) return errors;

  const mode = parseMatchAccessMode(body.matchAccess, errors);
  if (mode === null) return errors;

  switch (mode) {
    case 'open':
      if (body.matchPassword != null && body.matchPassword !== '') {
        errors.push('matchPassword must not be set when matchAccess is open');
      }
      if (body.allowedMatchTokens != null && body.allowedMatchTokens.length > 0) {
        errors.push('allowedMatchTokens must not be set when matchAccess is open');
      }
      break;
    case 'password':
      if (!body.matchPassword || body.matchPassword.length === 0) {
        errors.push('matchPassword is required when matchAccess is password');
      }
      if (body.allowedMatchTokens != null && body.allowedMatchTokens.length > 0) {
        errors.push('allowedMatchTokens must not be set when matchAccess is password');
      }
      break;
    case 'token':
      if (body.matchPassword != null && body.matchPassword !== '') {
        errors.push('matchPassword must not be set when matchAccess is token');
      }
      if (!body.allowedMatchTokens || body.allowedMatchTokens.length === 0) {
        errors.push('allowedMatchTokens is required when matchAccess is token');
        break;
      }
      const seen = new Set<string>();
      for (const t of body.allowedMatchTokens) {
        if (!t || t.trim().length === 0) {
          errors.push('allowedMatchTokens entries must be non-empty');
          break;
        }
        if (t.length > MATCH_ACCESS_MAX_TOKEN_LENGTH) {
          errors.push(
            `each token must be at most ${MATCH_ACCESS_MAX_TOKEN_LENGTH} characters`,
          );
          break;
        }
        seen.add(t);
      }
      if (seen.size > MATCH_ACCESS_MAX_TOKENS) {
        errors.push(`at most ${MATCH_ACCESS_MAX_TOKENS} distinct tokens allowed`);
      }
      break;
  }

  return errors;
}

function sha256Utf8(s: string): Buffer {
  return createHash('sha256').update(s, 'utf8').digest();
}

export function passwordMatches(expected: string, provided: string): boolean {
  const e = sha256Utf8(expected);
  const a = sha256Utf8(provided);
  return e.length === a.length && timingSafeEqual(e, a);
}

/** Socket.IO handshake query: `string | string[] | undefined` per key */
export function firstQueryString(
  value: string | string[] | undefined,
): string | undefined {
  if (typeof value === 'string' && value.length > 0) return value;
  if (Array.isArray(value) && value.length > 0 && typeof value[0] === 'string') {
    return value[0].length > 0 ? value[0] : undefined;
  }
  return undefined;
}

export function checkSocketMatchAccess(
  mode: MatchAccessMode,
  serverPassword: string,
  allowedTokens: Set<string>,
  query: Record<string, string | string[] | undefined>,
):
  | { ok: true }
  | { ok: false; reason: string; message: string } {
  switch (mode) {
    case 'open':
      return { ok: true };
    case 'password': {
      const p = firstQueryString(query.matchPassword);
      if (!p) {
        return {
          ok: false,
          reason: 'missing-match-password',
          message: 'matchPassword query parameter is required for this match',
        };
      }
      if (!passwordMatches(serverPassword, p)) {
        return {
          ok: false,
          reason: 'match-access-denied',
          message: 'Invalid match password',
        };
      }
      return { ok: true };
    }
    case 'token': {
      const t = firstQueryString(query.matchToken);
      if (!t) {
        return {
          ok: false,
          reason: 'missing-match-token',
          message: 'matchToken query parameter is required for this match',
        };
      }
      if (!allowedTokens.has(t)) {
        return {
          ok: false,
          reason: 'match-access-denied',
          message: 'Invalid or unknown match token',
        };
      }
      return { ok: true };
    }
    default:
      return {
        ok: false,
        reason: 'match-access-denied',
        message: 'Unknown match access configuration',
      };
  }
}
