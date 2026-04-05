import { Inject, Injectable, Logger, OnModuleDestroy } from '@nestjs/common';
import { v4 as uuidv4 } from 'uuid';
import { InputSyncerServer } from './input-syncer-server';
import {
  INPUT_SYNCER_OPTIONS,
  InputSyncerModuleOptions,
  InputSyncerServerOptions,
} from './interfaces';
import { ServerInstance } from './server-instance';
import { ServerInstanceState } from './types';
import { InputSyncerFinishReasons } from './finish-reasons';

/** Partial overrides must not assign `undefined`, or they would erase pool defaults. */
function mergeServerOptions(
  defaults: InputSyncerServerOptions,
  override?: InputSyncerServerOptions,
): InputSyncerServerOptions {
  if (!override) return { ...defaults };
  const keys: (keyof InputSyncerServerOptions)[] = [
    'maxPlayers',
    'stepIntervalSeconds',
    'autoStartWhenFull',
    'allowLateJoin',
    'sendStepHistoryOnLateJoin',
    'quorumUserFinishEndsMatch',
    'sessionFinishMaxPayloadBytes',
    'sessionFinishBroadcast',
    'rejectInputAfterSessionFinish',
    'abandonMatchTimeoutSeconds',
    'matchInstanceId',
    'matchAccess',
    'matchPassword',
    'allowedMatchTokens',
    'rewardOutcomeDelivery',
    'onRewardHookPerUser',
    'onRewardHookMatch',
  ];
  const out: InputSyncerServerOptions = { ...defaults };
  for (const k of keys) {
    const v = override[k];
    if (v !== undefined) (out as Record<string, unknown>)[k as string] = v;
  }
  return out;
}

@Injectable()
export class InputSyncerPoolService implements OnModuleDestroy {
  private readonly logger = new Logger(InputSyncerPoolService.name);
  private readonly instances = new Map<string, ServerInstance>();
  private readonly pendingDestroys: string[] = [];
  private tickInterval: ReturnType<typeof setInterval> | null = null;
  private disposed = false;

  /** Called before removing an instance so transports (e.g. Socket.IO) can close client connections. */
  private beforeInstanceDestroyed: ((instanceId: string) => void) | undefined;

  registerBeforeInstanceDestroyedHandler(
    handler: (instanceId: string) => void,
  ): void {
    this.beforeInstanceDestroyed = handler;
  }

  private readonly maxInstances: number;
  private readonly autoRecycleOnFinish: boolean;
  private readonly idleTimeoutSeconds: number;
  private readonly maxInstanceLifetimeSeconds: number;
  private readonly defaultServerOptions: InputSyncerServerOptions;

  constructor(
    @Inject(INPUT_SYNCER_OPTIONS)
    private readonly moduleOptions: InputSyncerModuleOptions,
  ) {
    this.maxInstances = moduleOptions.pool?.maxInstances ?? 10;
    this.autoRecycleOnFinish = moduleOptions.pool?.autoRecycleOnFinish ?? true;
    this.idleTimeoutSeconds = moduleOptions.pool?.idleTimeoutSeconds ?? 0;
    this.maxInstanceLifetimeSeconds =
      moduleOptions.pool?.maxInstanceLifetimeSeconds ?? 0;
    this.defaultServerOptions = moduleOptions.defaults ?? {};

    this.tickInterval = setInterval(() => this.tick(), 1000);
  }

  onModuleDestroy(): void {
    this.dispose();
  }

  // -------------------------
  // INSTANCE CRUD
  // -------------------------

  createInstance(overrideOptions?: InputSyncerServerOptions): ServerInstance {
    this.throwIfDisposed();
    this.processPendingDestroys();

    if (this.instances.size >= this.maxInstances) {
      throw new Error(
        `Cannot create instance: pool is full (${this.maxInstances}/${this.maxInstances})`,
      );
    }

    const id = uuidv4();
    const serverOptions = mergeServerOptions(
      this.defaultServerOptions,
      overrideOptions,
    );
    serverOptions.matchInstanceId = id;

    const server = new InputSyncerServer(serverOptions);
    const instance = new ServerInstance(id, server);

    instance.onStateChanged = (inst, _oldState, newState) => {
      this.logger.log(`Instance ${inst.id} state changed to ${newState}`);

      if (
        newState === ServerInstanceState.Finished &&
        this.autoRecycleOnFinish
      ) {
        this.pendingDestroys.push(inst.id);
      }
    };

    this.instances.set(id, instance);
    this.logger.log(`Instance ${id} created`);

    return instance;
  }

  destroyInstance(id: string): boolean {
    this.throwIfDisposed();
    this.processPendingDestroys();

    const instance = this.instances.get(id);
    if (!instance) return false;

    this.destroyInstanceInternal(instance);
    return true;
  }

  getInstance(id: string): ServerInstance | undefined {
    this.throwIfDisposed();
    return this.instances.get(id);
  }

  getAllInstances(): ServerInstance[] {
    this.throwIfDisposed();
    return [...this.instances.values()];
  }

  getInstancesByState(state: ServerInstanceState): ServerInstance[] {
    this.throwIfDisposed();
    return [...this.instances.values()].filter((i) => i.state === state);
  }

  getInstanceCount(): number {
    this.throwIfDisposed();
    return this.instances.size;
  }

  getAvailableSlots(): number {
    this.throwIfDisposed();
    return this.maxInstances - this.instances.size;
  }

  // -------------------------
  // TICK
  // -------------------------

  private tick(): void {
    if (this.disposed) return;
    this.processMaxInstanceLifetime();
    this.processIdleTimeouts();
    this.processPendingDestroys();
  }

  private processMaxInstanceLifetime(): void {
    if (this.maxInstanceLifetimeSeconds <= 0) return;

    const now = Date.now();
    for (const instance of [...this.instances.values()]) {
      const age = (now - instance.createdAt.getTime()) / 1000;
      if (age < this.maxInstanceLifetimeSeconds) continue;

      if (instance.server.isMatchStarted && !instance.server.isMatchFinished) {
        instance.server.finishMatch(InputSyncerFinishReasons.MaxInstanceLifetime);
      }
      this.pendingDestroys.push(instance.id);
    }
  }

  private processIdleTimeouts(): void {
    if (this.idleTimeoutSeconds <= 0) return;

    const now = Date.now();
    for (const instance of this.instances.values()) {
      if (
        instance.state !== ServerInstanceState.Idle &&
        instance.state !== ServerInstanceState.Finished
      )
        continue;

      const elapsed =
        (now - instance.lastStateChangeTime.getTime()) / 1000;
      if (elapsed >= this.idleTimeoutSeconds) {
        this.pendingDestroys.push(instance.id);
      }
    }
  }

  private processPendingDestroys(): void {
    if (this.pendingDestroys.length === 0) return;

    const toDestroy = [...this.pendingDestroys];
    this.pendingDestroys.length = 0;

    for (const id of toDestroy) {
      const instance = this.instances.get(id);
      if (instance) {
        this.destroyInstanceInternal(instance);
      }
    }
  }

  private destroyInstanceInternal(instance: ServerInstance): void {
    this.beforeInstanceDestroyed?.(instance.id);
    instance.onStateChanged = () => {};
    this.instances.delete(instance.id);
    instance.server.dispose();
    this.logger.log(`Instance ${instance.id} destroyed`);
  }

  // -------------------------
  // DISPOSE
  // -------------------------

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;

    if (this.tickInterval) {
      clearInterval(this.tickInterval);
      this.tickInterval = null;
    }

    for (const instance of this.instances.values()) {
      this.beforeInstanceDestroyed?.(instance.id);
      instance.onStateChanged = () => {};
      instance.server.dispose();
    }

    this.instances.clear();
    this.pendingDestroys.length = 0;
  }

  private throwIfDisposed(): void {
    if (this.disposed) {
      throw new Error('InputSyncerPoolService has been disposed');
    }
  }
}
