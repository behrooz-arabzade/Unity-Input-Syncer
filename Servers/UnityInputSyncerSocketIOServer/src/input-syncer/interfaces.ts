import { InjectionToken, ModuleMetadata, OptionalFactoryDependency } from '@nestjs/common';
import type { AllowedMatchTokens } from './match-access';
import {
  RewardMatchHookPayload,
  RewardOutcomeDeliveryMode,
  RewardPerUserHookPayload,
} from './reward-delivery';

export interface InputSyncerServerOptions {
  maxPlayers?: number;
  autoStartWhenFull?: boolean;
  stepIntervalSeconds?: number;
  allowLateJoin?: boolean;
  sendStepHistoryOnLateJoin?: boolean;
  quorumUserFinishEndsMatch?: boolean;
  sessionFinishMaxPayloadBytes?: number;
  sessionFinishBroadcast?: boolean;
  rejectInputAfterSessionFinish?: boolean;
  abandonMatchTimeoutSeconds?: number;
  /** Per-user disconnect grace period in seconds before marking as abandoned. */
  disconnectAbandonTimeoutSeconds?: number;
  matchInstanceId?: string;
  /** Nakama match manager match_id for result reporting back to Nakama. */
  nakamaMatchId?: string;
  /** Default `open`. */
  matchAccess?: 'open' | 'password' | 'token';
  matchPassword?: string;
  /** `["tok", …]` (unbound) or `{ "<userId>": "tok" }` (bound to a user). */
  allowedMatchTokens?: AllowedMatchTokens;
  /** Opaque JSON from admin create; sent to clients as on-match-context. */
  matchData?: unknown;
  /** Per-userId simulation payloads from admin create. */
  users?: Record<string, unknown>;
  rewardOutcomeDelivery?: RewardOutcomeDeliveryMode;
  onRewardHookPerUser?: (payload: RewardPerUserHookPayload) => void;
  onRewardHookMatch?: (payload: RewardMatchHookPayload) => void;
}

export interface InputSyncerPoolOptions {
  maxInstances?: number;
  autoRecycleOnFinish?: boolean;
  idleTimeoutSeconds?: number;
  maxInstanceLifetimeSeconds?: number;
  /** Base URL for Socket.IO clients (scheme + host + port, no path), e.g. https://game.example.com */
  publicClientSocketIoUrl?: string;
  /** When true, POST /api/instances must include non-empty matchData and/or users. */
  requireMatchUserDataOnCreate?: boolean;
}

export interface InputSyncerAdminOptions {
  authToken?: string;
  /**
   * The operator's explicit "yes, an open admin API is what I want". Without a token and
   * without this, `BearerAuthGuard` refuses every request and `main.ts` refuses to start.
   */
  authDisabled?: boolean;
}

export interface InputSyncerModuleOptions {
  pool?: InputSyncerPoolOptions;
  defaults?: InputSyncerServerOptions;
  admin?: InputSyncerAdminOptions;
}

export interface InputSyncerModuleAsyncOptions
  extends Pick<ModuleMetadata, 'imports'> {
  useFactory: (
    ...args: any[]
  ) => Promise<InputSyncerModuleOptions> | InputSyncerModuleOptions;
  inject?: (InjectionToken | OptionalFactoryDependency)[];
}

export const INPUT_SYNCER_OPTIONS = Symbol('INPUT_SYNCER_OPTIONS');
