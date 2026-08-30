import 'reflect-metadata';
import { Test } from '@nestjs/testing';
import { INestApplication } from '@nestjs/common';
import { InputSyncerModule } from '../src/input-syncer';
import {
  ADMIN_AUTH_DISABLED_ENV,
  ADMIN_AUTH_TOKEN_ENV,
  adminAuthMisconfigurationMessage,
  readAdminAuthConfig,
} from '../src/input-syncer/admin-auth';
import { InputSyncerAdminOptions } from '../src/input-syncer/interfaces';

/**
 * E08/S10. `BearerAuthGuard` used to read an empty token as "no check wanted", so a deploy
 * that forgot `INPUT_SYNCER_ADMIN_AUTH_TOKEN` answered an anonymous `POST /api/instances`
 * with a 201 — measured that way in E08/S05. The admin API allocates match instances and
 * shares its port with the player WebSocket, so that is an open allocator on the public
 * port, not an internal one.
 *
 * These build the module with explicit `admin` options rather than through `AppModule`,
 * whose options are read from the environment when it is imported — before any test could
 * change them. The env-to-options mapping is covered separately at the bottom.
 */
async function appWith(
  admin: InputSyncerAdminOptions,
): Promise<{ app: INestApplication; baseUrl: string }> {
  const module = await Test.createTestingModule({
    imports: [InputSyncerModule.forRoot({ admin })],
  }).compile();

  const app = module.createNestApplication();
  await app.listen(0);
  const baseUrl = (await app.getUrl()).replace('[::1]', 'localhost');
  return { app, baseUrl };
}

function createInstance(
  baseUrl: string,
  authorization?: string,
): Promise<Response> {
  return fetch(`${baseUrl}/api/instances`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      ...(authorization ? { Authorization: authorization } : {}),
    },
    body: JSON.stringify({ maxPlayers: 2 }),
  });
}

describe('the admin API with no token configured', () => {
  let app: INestApplication;
  let baseUrl: string;

  beforeAll(async () => {
    ({ app, baseUrl } = await appWith({ authToken: '' }));
  });
  afterAll(async () => {
    await app.close().catch(() => {});
  });

  it('refuses an anonymous create — the 201 E08/S05 measured', async () => {
    const res = await createInstance(baseUrl);
    expect(res.status).toBe(401);
  });

  it('refuses a bearer too: there is nothing to check it against', async () => {
    const res = await createInstance(baseUrl, 'Bearer anything-at-all');
    expect(res.status).toBe(401);
  });

  it('refuses reads as well as writes', async () => {
    const res = await fetch(`${baseUrl}/api/instances`);
    expect(res.status).toBe(401);
  });
});

describe('the admin API with a token configured', () => {
  let app: INestApplication;
  let baseUrl: string;

  beforeAll(async () => {
    ({ app, baseUrl } = await appWith({ authToken: 'the-token' }));
  });
  afterAll(async () => {
    await app.close().catch(() => {});
  });

  it('admits the right bearer', async () => {
    const res = await createInstance(baseUrl, 'Bearer the-token');
    expect(res.status).toBe(201);
  });

  it('refuses the wrong one', async () => {
    const res = await createInstance(baseUrl, 'Bearer not-the-token');
    expect(res.status).toBe(401);
  });

  it('refuses no bearer at all', async () => {
    const res = await createInstance(baseUrl);
    expect(res.status).toBe(401);
  });
});

describe('the explicit opt-out', () => {
  let app: INestApplication;
  let baseUrl: string;

  beforeAll(async () => {
    ({ app, baseUrl } = await appWith({ authToken: '', authDisabled: true }));
  });
  afterAll(async () => {
    await app.close().catch(() => {});
  });

  it('admits an anonymous create, because the operator said so', async () => {
    const res = await createInstance(baseUrl);
    expect(res.status).toBe(201);
  });
});

describe('what the process checks before it binds a port', () => {
  it('names the variable when neither a token nor the opt-out is set', () => {
    const message = adminAuthMisconfigurationMessage({
      authToken: '',
      authDisabled: false,
    });
    expect(message).toContain(ADMIN_AUTH_TOKEN_ENV);
    expect(message).toContain(ADMIN_AUTH_DISABLED_ENV);
  });

  it('is satisfied by a token', () => {
    expect(
      adminAuthMisconfigurationMessage({
        authToken: 'x',
        authDisabled: false,
      }),
    ).toBeNull();
  });

  it('is satisfied by the opt-out', () => {
    expect(
      adminAuthMisconfigurationMessage({
        authToken: '',
        authDisabled: true,
      }),
    ).toBeNull();
  });

  it('reads both variables, and takes 1 or true for the opt-out', () => {
    expect(readAdminAuthConfig({})).toEqual({
      authToken: '',
      authDisabled: false,
    });
    expect(
      readAdminAuthConfig({
        [ADMIN_AUTH_TOKEN_ENV]: 'tok',
        [ADMIN_AUTH_DISABLED_ENV]: '1',
      }),
    ).toEqual({ authToken: 'tok', authDisabled: true });
    expect(
      readAdminAuthConfig({ [ADMIN_AUTH_DISABLED_ENV]: 'true' }).authDisabled,
    ).toBe(true);
    // Anything else is not an opt-out. "false", "0", "no", a typo — all keep the guard on.
    expect(
      readAdminAuthConfig({ [ADMIN_AUTH_DISABLED_ENV]: 'false' }).authDisabled,
    ).toBe(false);
    expect(
      readAdminAuthConfig({ [ADMIN_AUTH_DISABLED_ENV]: 'yes' }).authDisabled,
    ).toBe(false);
  });
});
