import { createHash, timingSafeEqual } from 'crypto';
import type { AdminCreateInstanceRequest } from './types';

export const MATCH_ACCESS_MAX_TOKENS = 64;
export const MATCH_ACCESS_MAX_TOKEN_LENGTH = 256;

export type MatchAccessMode = 'open' | 'password' | 'token';

/**
 * `allowedMatchTokens` accepts two forms.
 *
 * - **Bound** — `{ "<userId>": "<token>" }`. A token then admits exactly the user it was
 *   minted for. This is the form to use whenever the allocator knows who the players are.
 * - **Unbound** — `["<token>", …]`, the original form. Any holder of any listed token may
 *   claim any `userId`, because the joining `userId` comes from the client's own handshake
 *   query and there is nothing to check it against. Kept so this is not a flag day.
 *
 * The unbound form is a match-level secret, not a player-level one: with it, one of two
 * invited players can occupy both seats. Prefer the bound form.
 */
export type AllowedMatchTokens = string[] | Record<string, string>;

export interface ResolvedMatchTokens {
  /** Every accepted token, whichever form was configured. */
  all: Set<string>;
  /** userId → token, when the bound form was configured; `null` for the unbound form. */
  byUser: Map<string, string> | null;
}

export function isBoundTokenForm(
  raw: AllowedMatchTokens | undefined,
): raw is Record<string, string> {
  return raw != null && typeof raw === 'object' && !Array.isArray(raw);
}

export function resolveMatchTokens(
  raw: AllowedMatchTokens | undefined,
): ResolvedMatchTokens {
  if (isBoundTokenForm(raw)) {
    const byUser = new Map<string, string>();
    const all = new Set<string>();
    for (const userId of Object.keys(raw)) {
      const token = raw[userId];
      if (typeof token !== 'string' || token.length === 0) continue;
      byUser.set(userId, token);
      all.add(token);
    }
    return { all, byUser };
  }
  return { all: new Set(raw ?? []), byUser: null };
}

/** Every token in either form, for validation and for `allowedMatchTokenCount`. */
export function tokenValues(raw: AllowedMatchTokens | undefined): string[] {
  if (raw == null) return [];
  return isBoundTokenForm(raw) ? Object.keys(raw).map((k) => raw[k]) : raw;
}

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
      if (tokenValues(body.allowedMatchTokens).length > 0) {
        errors.push('allowedMatchTokens must not be set when matchAccess is open');
      }
      break;
    case 'password':
      if (!body.matchPassword || body.matchPassword.length === 0) {
        errors.push('matchPassword is required when matchAccess is password');
      }
      if (tokenValues(body.allowedMatchTokens).length > 0) {
        errors.push('allowedMatchTokens must not be set when matchAccess is password');
      }
      break;
    case 'token': {
      if (body.matchPassword != null && body.matchPassword !== '') {
        errors.push('matchPassword must not be set when matchAccess is token');
      }
      const values = tokenValues(body.allowedMatchTokens);
      if (values.length === 0) {
        errors.push('allowedMatchTokens is required when matchAccess is token');
        break;
      }
      if (isBoundTokenForm(body.allowedMatchTokens)) {
        const bound = body.allowedMatchTokens;
        for (const userId of Object.keys(bound)) {
          if (userId.trim().length === 0) {
            errors.push('allowedMatchTokens keys must be non-empty user ids');
            break;
          }
          if (typeof bound[userId] !== 'string') {
            errors.push('allowedMatchTokens values must be strings');
            break;
          }
        }
      }
      const seen = new Set<string>();
      for (const t of values) {
        if (typeof t !== 'string' || t.trim().length === 0) {
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
      // One token seating two users is the very hole the bound form closes.
      if (seen.size !== values.length) {
        errors.push('allowedMatchTokens entries must be distinct');
      }
      break;
    }
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

/**
 * Constant-time in the secret: both sides are hashed to a fixed 32 bytes first, so the
 * comparison cannot leak the token's length or its matching prefix. `Set.has` — what the
 * token path used before — is neither.
 */
export function tokenMatches(expected: string, provided: string): boolean {
  return passwordMatches(expected, provided);
}

/** Any of `expected` matching, without an early exit that would leak which one. */
function anyTokenMatches(expected: Iterable<string>, provided: string): boolean {
  let hit = false;
  for (const e of expected) {
    if (tokenMatches(e, provided)) hit = true;
  }
  return hit;
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

/**
 * Every refusal below reports `match-access-denied` with no detail about *which* half was
 * wrong. That is deliberate twice over: a caller learns nothing from the reason string, and
 * the existing clients already map it to "access denied" — a new reason string would reach
 * them as an unknown error and be treated as retryable, which a credential mismatch is not.
 */
export function checkSocketMatchAccess(
  mode: MatchAccessMode,
  serverPassword: string,
  allowedTokens: ResolvedMatchTokens,
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
      const denied = {
        ok: false as const,
        reason: 'match-access-denied',
        message: 'Invalid or unknown match token',
      };

      // Bound form: the token must be the one minted for the userId being claimed.
      if (allowedTokens.byUser) {
        const userId = firstQueryString(query.userId);
        if (!userId) return denied;
        const expected = allowedTokens.byUser.get(userId);
        if (expected === undefined) return denied;
        return tokenMatches(expected, t) ? { ok: true } : denied;
      }

      // Unbound form: any listed token admits any claimed userId. See AllowedMatchTokens.
      return anyTokenMatches(allowedTokens.all, t) ? { ok: true } : denied;
    }
    default:
      return {
        ok: false,
        reason: 'match-access-denied',
        message: 'Unknown match access configuration',
      };
  }
}

/**
 * When the allocator declared who is playing (`users` non-empty), a socket claiming some
 * other `userId` is not in this match. Checked in addition to `matchAccess`, because the
 * unbound token form cannot check it and `open` matches do not check anything at all.
 */
export function checkKnownUser(
  users: Record<string, unknown>,
  query: Record<string, string | string[] | undefined>,
): { ok: true } | { ok: false; reason: string; message: string } {
  const declared = Object.keys(users ?? {});
  if (declared.length === 0) return { ok: true };

  const userId = firstQueryString(query.userId);
  if (userId && Object.prototype.hasOwnProperty.call(users, userId)) {
    return { ok: true };
  }
  return {
    ok: false,
    reason: 'match-access-denied',
    message: 'userId is not a participant of this match',
  };
}
