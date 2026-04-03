import 'reflect-metadata';
import { NestFactory } from '@nestjs/core';
import { IoAdapter } from '@nestjs/platform-socket.io';
import { AppModule } from './app.module';

async function bootstrap() {
  const app = await NestFactory.create(AppModule);

  app.useWebSocketAdapter(new IoAdapter(app));
  app.enableCors();

  const port = parseInt(process.env.INPUT_SYNCER_PORT ?? '3000', 10);
  await app.listen(port);

  console.log(`[InputSyncerSocketIOServer] Listening on port ${port}`);
  console.log(
    `[InputSyncerSocketIOServer] Admin API: http://localhost:${port}/api`,
  );
  console.log(
    `[InputSyncerSocketIOServer] WebSocket path: /match-gateway`,
  );
}

bootstrap();
