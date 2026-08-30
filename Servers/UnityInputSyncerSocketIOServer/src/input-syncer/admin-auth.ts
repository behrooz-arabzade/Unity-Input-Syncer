/**
 * The admin API allocates match instances, and it shares its port with the player
 * WebSocket — "internal only" is a path rule at the edge, not a firewall rule. So a
 * deployment that forgets `INPUT_SYNCER_ADMIN_AUTH_TOKEN` publishes an open allocator,
 * and it used to do so silently: the guard read an empty token as "no check wanted".
 *
 * A guard whose only job is to check now refuses when it has nothing to check with.
 * An operator who genuinely wants an open admin API says so with
 * `INPUT_SYNCER_ADMIN_AUTH_DISABLED=1`, which is a thing you can grep a deployment for.
 */
export const ADMIN_AUTH_TOKEN_ENV = 'INPUT_SYNCER_ADMIN_AUTH_TOKEN';
export const ADMIN_AUTH_DISABLED_ENV = 'INPUT_SYNCER_ADMIN_AUTH_DISABLED';

export interface AdminAuthConfig {
  /** Bearer token every admin request must present. Empty when auth is disabled. */
  authToken: string;
  /** The operator's explicit opt-out. Only ever true from the env var. */
  authDisabled: boolean;
}

function envBool(raw: string | undefined): boolean {
  return raw === 'true' || raw === '1';
}

export function readAdminAuthConfig(
  env: NodeJS.ProcessEnv = process.env,
): AdminAuthConfig {
  return {
    authToken: env[ADMIN_AUTH_TOKEN_ENV] ?? '',
    authDisabled: envBool(env[ADMIN_AUTH_DISABLED_ENV]),
  };
}

export function adminAuthMisconfigurationMessage(
  config: AdminAuthConfig,
): string | null {
  if (config.authToken) return null;
  if (config.authDisabled) return null;
  return (
    `${ADMIN_AUTH_TOKEN_ENV} is not set. The admin API allocates match instances and ` +
    `shares its port with the player WebSocket, so starting without a token would ` +
    `publish an open allocator. Set ${ADMIN_AUTH_TOKEN_ENV}, or set ` +
    `${ADMIN_AUTH_DISABLED_ENV}=1 if an unauthenticated admin API is what you mean.`
  );
}

/** Throws unless a token is configured or the operator has explicitly opted out. */
export function assertAdminAuthConfigured(
  env: NodeJS.ProcessEnv = process.env,
): void {
  const message = adminAuthMisconfigurationMessage(readAdminAuthConfig(env));
  if (message) throw new Error(message);
}
