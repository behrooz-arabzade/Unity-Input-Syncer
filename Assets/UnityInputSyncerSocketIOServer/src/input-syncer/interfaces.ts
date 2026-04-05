import { InjectionToken, ModuleMetadata, OptionalFactoryDependency } from '@nestjs/common';
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
  matchInstanceId?: string;
  rewardOutcomeDelivery?: RewardOutcomeDeliveryMode;
  onRewardHookPerUser?: (payload: RewardPerUserHookPayload) => void;
  onRewardHookMatch?: (payload: RewardMatchHookPayload) => void;
}

export interface InputSyncerPoolOptions {
  maxInstances?: number;
  autoRecycleOnFinish?: boolean;
  idleTimeoutSeconds?: number;
  maxInstanceLifetimeSeconds?: number;
}

export interface InputSyncerAdminOptions {
  authToken?: string;
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
