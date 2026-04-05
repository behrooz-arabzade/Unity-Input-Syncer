import type { AdminCreateInstanceRequest } from './types';

const MAX_MATCH_DATA_UTF8 = 65536;
const MAX_PER_USER_UTF8 = 16384;
const MAX_USER_ENTRIES = 64;

function utf8ByteLengthJson(value: unknown): number {
  const s =
    value === undefined || value === null
      ? 'null'
      : typeof value === 'string'
        ? value
        : JSON.stringify(value);
  return Buffer.byteLength(s, 'utf8');
}

export function validateAdminMatchContext(
  body: AdminCreateInstanceRequest | undefined,
  requirePayload: boolean,
): string[] {
  const errors: string[] = [];

  if (requirePayload && !body) {
    errors.push(
      'matchData or users is required when requireMatchUserDataOnCreate is enabled',
    );
    return errors;
  }

  if (!body) return errors;

  if (requirePayload) {
    const hasMatch =
      body.matchData != null &&
      typeof body.matchData === 'object' &&
      !Array.isArray(body.matchData) &&
      Object.keys(body.matchData as object).length > 0;
    const u = body.users;
    const hasUsers =
      u != null &&
      typeof u === 'object' &&
      !Array.isArray(u) &&
      Object.keys(u as object).length > 0;
    if (!hasMatch && !hasUsers) {
      errors.push(
        'Provide matchData and/or users when requireMatchUserDataOnCreate is enabled',
      );
    }
  }

  if (body.matchData !== undefined && body.matchData !== null) {
    const n = utf8ByteLengthJson(body.matchData);
    if (n > MAX_MATCH_DATA_UTF8) {
      errors.push(
        `matchData must be at most ${MAX_MATCH_DATA_UTF8} UTF-8 bytes (got ${n})`,
      );
    }
  }

  if (body.users === undefined || body.users === null) return errors;
  if (typeof body.users !== 'object' || Array.isArray(body.users)) {
    errors.push('users must be a JSON object (userId -> payload)');
    return errors;
  }

  const keys = Object.keys(body.users);
  if (keys.length > MAX_USER_ENTRIES) {
    errors.push(`users must have at most ${MAX_USER_ENTRIES} entries`);
    return errors;
  }

  for (const k of keys) {
    if (!k.trim()) {
      errors.push('users object keys must be non-empty userIds');
      break;
    }
    const n = utf8ByteLengthJson((body.users as Record<string, unknown>)[k]);
    if (n > MAX_PER_USER_UTF8) {
      errors.push(
        `users['${k}'] must be at most ${MAX_PER_USER_UTF8} UTF-8 bytes (got ${n})`,
      );
    }
  }

  return errors;
}
