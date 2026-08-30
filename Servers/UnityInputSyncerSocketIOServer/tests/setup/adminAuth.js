// `BearerAuthGuard` fails closed (E08/S10): an empty INPUT_SYNCER_ADMIN_AUTH_TOKEN is a
// refusal, not an open door. `AppModule` reads the variable when it is imported — before
// any beforeAll could run — so the token has to be in the environment by setupFiles time.
// The integration suite therefore runs the same configuration a deployment runs, and every
// admin call in it carries a bearer. The guard's own branches are tested in
// admin-auth.integration.test.ts, which builds its modules with explicit options.
process.env.INPUT_SYNCER_ADMIN_AUTH_TOKEN =
  process.env.INPUT_SYNCER_ADMIN_AUTH_TOKEN || 'test-admin-token';
