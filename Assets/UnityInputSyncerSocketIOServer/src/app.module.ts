import { Module } from '@nestjs/common';
import { InputSyncerModule } from './input-syncer';
import { RewardOutcomeDeliveryMode } from './input-syncer/reward-delivery';

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

function envRewardMode(): RewardOutcomeDeliveryMode {
  const v = envInt('INPUT_SYNCER_REWARD_OUTCOME_DELIVERY', 0);
  if (v === 1) return RewardOutcomeDeliveryMode.ServerHookPerUser;
  if (v === 2) return RewardOutcomeDeliveryMode.ServerHookMatchOrReferee;
  return RewardOutcomeDeliveryMode.ClientToAdmin;
}

@Module({
  imports: [
    InputSyncerModule.forRoot({
      pool: {
        maxInstances: envInt('INPUT_SYNCER_MAX_INSTANCES', 10),
        autoRecycleOnFinish: envBool('INPUT_SYNCER_AUTO_RECYCLE', true),
        idleTimeoutSeconds: envFloat('INPUT_SYNCER_IDLE_TIMEOUT', 0),
        maxInstanceLifetimeSeconds: envFloat(
          'INPUT_SYNCER_MAX_INSTANCE_LIFETIME',
          0,
        ),
        publicClientSocketIoUrl:
          process.env.INPUT_SYNCER_PUBLIC_CLIENT_SOCKET_IO_URL,
        requireMatchUserDataOnCreate: envBool(
          'INPUT_SYNCER_ADMIN_REQUIRE_MATCH_USER_DATA',
          false,
        ),
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
        quorumUserFinishEndsMatch: envBool(
          'INPUT_SYNCER_QUORUM_USER_FINISH_ENDS_MATCH',
          true,
        ),
        abandonMatchTimeoutSeconds: envFloat(
          'INPUT_SYNCER_ABANDON_MATCH_TIMEOUT',
          0,
        ),
        rewardOutcomeDelivery: envRewardMode(),
      },
      admin: {
        authToken: process.env.INPUT_SYNCER_ADMIN_AUTH_TOKEN ?? '',
      },
    }),
  ],
})
export class AppModule {}
