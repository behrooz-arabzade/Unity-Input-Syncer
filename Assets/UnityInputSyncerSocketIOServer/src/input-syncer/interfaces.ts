import { InjectionToken, ModuleMetadata, OptionalFactoryDependency } from '@nestjs/common';

export interface InputSyncerServerOptions {
  maxPlayers?: number;
  autoStartWhenFull?: boolean;
  stepIntervalSeconds?: number;
  allowLateJoin?: boolean;
  sendStepHistoryOnLateJoin?: boolean;
}

export interface InputSyncerPoolOptions {
  maxInstances?: number;
  autoRecycleOnFinish?: boolean;
  idleTimeoutSeconds?: number;
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
