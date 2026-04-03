import { Module } from '@nestjs/common';
import { InputSyncerModule } from './input-syncer';

function envBool(name: string, fallback: boolean): boolean {
  const raw = process.env[name];
  if (!raw) return fallback;
  return raw === 'true' || raw === '1';
}

function envFloat(name: string, fallback: number): number {
  const raw = process.env[name];
  if (!raw) return fallback;
  const parsed = parseFloat(raw);
  return isNaN(parsed) ? fallback : parsed;
}

function envInt(name: string, fallback: number): number {
  const raw = process.env[name];
  if (!raw) return fallback;
  const parsed = parseInt(raw, 10);
  return isNaN(parsed) ? fallback : parsed;
}

@Module({
  imports: [
    InputSyncerModule.forRoot({
      pool: {
        maxInstances: envInt('INPUT_SYNCER_MAX_INSTANCES', 10),
        autoRecycleOnFinish: envBool('INPUT_SYNCER_AUTO_RECYCLE', true),
        idleTimeoutSeconds: envFloat('INPUT_SYNCER_IDLE_TIMEOUT', 0),
      },
      defaults: {
        maxPlayers: envInt('INPUT_SYNCER_MAX_PLAYERS', 2),
        autoStartWhenFull: envBool('INPUT_SYNCER_AUTO_START_WHEN_FULL', true),
        stepIntervalSeconds: envFloat('INPUT_SYNCER_STEP_INTERVAL', 0.1),
        allowLateJoin: envBool('INPUT_SYNCER_ALLOW_LATE_JOIN', false),
        sendStepHistoryOnLateJoin: envBool(
          'INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN',
          true,
        ),
      },
      admin: {
        authToken: process.env.INPUT_SYNCER_ADMIN_AUTH_TOKEN ?? '',
      },
    }),
  ],
})
export class AppModule {}
