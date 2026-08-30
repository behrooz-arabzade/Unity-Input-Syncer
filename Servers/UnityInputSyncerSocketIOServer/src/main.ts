import 'reflect-metadata';
import * as fs from 'fs';
import { INestApplication } from '@nestjs/common';
import { NestFactory } from '@nestjs/core';
import { IoAdapter } from '@nestjs/platform-socket.io';
import { AppModule } from './app.module';
import {
  adminAuthMisconfigurationMessage,
  readAdminAuthConfig,
} from './input-syncer/admin-auth';

const LOG = '[InputSyncerSocketIOServer]';

/**
 * Unity sets INPUT_SYNCER_EDITOR_LOG to an absolute path so logs survive domain reload:
 * after reload the editor reattaches by PID but loses stdout pipes, and tails this file.
 */
function installEditorLogMirror(): void {
  const p = process.env.INPUT_SYNCER_EDITOR_LOG;
  if (!p) return;

  const appendChunk = (chunk: unknown) => {
    try {
      if (chunk === undefined || chunk === null) return;
      const s =
        typeof chunk === 'string'
          ? chunk
          : Buffer.isBuffer(chunk)
            ? chunk.toString('utf8')
            : String(chunk);
      fs.appendFileSync(p, s, 'utf8');
    } catch {
      /* disk full / permissions — do not crash the server */
    }
  };

  const wrapStream = (stream: NodeJS.WriteStream) => {
    const origWrite = stream.write.bind(stream) as (
      chunk: unknown,
      encoding?: BufferEncoding | ((err?: Error | null) => void),
      cb?: (err?: Error | null) => void,
    ) => boolean;

    (stream as NodeJS.WriteStream & { write: typeof stream.write }).write = (
      chunk: unknown,
      encodingOrCb?: BufferEncoding | ((err?: Error | null) => void),
      cb?: (err?: Error | null) => void,
    ): boolean => {
      appendChunk(chunk);
      return origWrite(chunk, encodingOrCb as never, cb as never);
    };
  };

  wrapStream(process.stdout);
  wrapStream(process.stderr);
}

installEditorLogMirror();

function installFatalProcessLogging(): void {
  process.on('uncaughtException', (err, origin) => {
    console.error(`${LOG} FATAL uncaughtException (origin=${origin})`);
    console.error(err);
    if (err instanceof Error && err.stack) console.error(err.stack);
    process.exit(1);
  });

  // Do not process.exit() here. Nest/Socket.IO (and dependencies) can surface
  // promise rejections that are non-fatal; exiting would kill the dev server and
  // show only "exited (code 1)" in the Unity console with no useful stderr.
  process.on('unhandledRejection', (reason) => {
    console.error(`${LOG} unhandledRejection (server keeps running):`);
    console.error(reason);
    if (reason instanceof Error && reason.stack) console.error(reason.stack);
  });
}

installFatalProcessLogging();

/**
 * Without this the process ignores SIGTERM outright: Nest registers no signal listeners
 * unless shutdown hooks are enabled, and as PID 1 in a container the kernel applies no
 * default disposition either — so `docker stop` waits out its whole grace period and then
 * SIGKILLs. That pause looks like a graceful drain and is not one.
 *
 * There is nothing to drain: a match lives in this process's memory and cannot be moved.
 * What closing properly does buy is that `InputSyncerPoolService.onModuleDestroy` actually
 * runs — the pool tick is cleared and instances are torn down — and that an operator's stop
 * is prompt and honest. Draining is done *upstream*, by not allocating here any more.
 */
function installSignalHandlers(app: INestApplication): void {
  let closing = false;
  const close = (signal: NodeJS.Signals): void => {
    if (closing) return;
    closing = true;
    console.log(`${LOG} ${signal} received — closing`);
    void app
      .close()
      .catch((e: unknown) => console.error(`${LOG} close failed`, e))
      .finally(() => process.exit(0));
  };
  process.on('SIGTERM', () => close('SIGTERM'));
  process.on('SIGINT', () => close('SIGINT'));
}

async function bootstrap() {
  // Before anything binds a port. An open allocator on a public port is the kind of
  // mistake that is only ever found by someone else, so it is a startup failure here
  // rather than a warning in a log nobody reads.
  const misconfigured = adminAuthMisconfigurationMessage(readAdminAuthConfig());
  if (misconfigured) {
    console.error(`${LOG} ${misconfigured}`);
    process.exit(1);
  }

  const app = await NestFactory.create(AppModule);

  app.useWebSocketAdapter(new IoAdapter(app));
  app.enableCors();
  app.enableShutdownHooks();
  installSignalHandlers(app);

  const port = parseInt(process.env.INPUT_SYNCER_PORT ?? '3000', 10);
  const bind = process.env.INPUT_SYNCER_BIND;
  if (bind) {
    await app.listen(port, bind);
  } else {
    await app.listen(port);
  }

  const role = process.env.INPUT_SYNCER_ROLE ?? '';
  const where =
    bind !== undefined ? `${bind}:${port}` : `port ${port}`;
  console.log(
    `${LOG} Listening on ${where}${role ? ` (role=${role})` : ''}`,
  );
  console.log(`${LOG} Admin API: http://localhost:${port}/api`);
  console.log(`${LOG} WebSocket path: /match-gateway`);
}

bootstrap().catch((err: unknown) => {
  console.error(`${LOG} bootstrap() failed`);
  console.error(err);
  if (err instanceof Error && err.stack) console.error(err.stack);
  process.exit(1);
});
