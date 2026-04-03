import 'reflect-metadata';
import * as fs from 'fs';
import { NestFactory } from '@nestjs/core';
import { IoAdapter } from '@nestjs/platform-socket.io';
import { AppModule } from './app.module';

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

async function bootstrap() {
  const app = await NestFactory.create(AppModule);

  app.useWebSocketAdapter(new IoAdapter(app));
  app.enableCors();

  const port = parseInt(process.env.INPUT_SYNCER_PORT ?? '3000', 10);
  await app.listen(port);

  console.log(`${LOG} Listening on port ${port}`);
  console.log(`${LOG} Admin API: http://localhost:${port}/api`);
  console.log(`${LOG} WebSocket path: /match-gateway`);
}

bootstrap().catch((err: unknown) => {
  console.error(`${LOG} bootstrap() failed`);
  console.error(err);
  if (err instanceof Error && err.stack) console.error(err.stack);
  process.exit(1);
});
