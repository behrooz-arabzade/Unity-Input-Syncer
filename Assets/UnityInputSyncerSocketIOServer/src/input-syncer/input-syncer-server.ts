import { createHash } from 'crypto';
import { InputSyncerEvents } from './input-syncer-events';
import { InputSyncerFinishReasons } from './finish-reasons';
import { InputSyncerPlayer } from './input-syncer-player';
import { InputSyncerState } from './input-syncer-state';
import { InputSyncerServerOptions } from './interfaces';
import {
  RewardOutcomeDeliveryMode,
  type RewardMatchHookPayload,
  type RewardPerUserHookPayload,
} from './reward-delivery';
import { AllStepInputs, StepInputs } from './types';
import type { MatchAccessMode } from './match-access';

export type SendToSocketFn = (
  socketId: string,
  event: string,
  data: unknown,
) => void;

function defaultAnonymousUserId(socketId: string): string {
  const h = createHash('sha256').update(socketId, 'utf8').digest('hex');
  return `player-${h.slice(0, 8)}`;
}

type ResolvedServerOptions = {
  maxPlayers: number;
  autoStartWhenFull: boolean;
  stepIntervalSeconds: number;
  allowLateJoin: boolean;
  sendStepHistoryOnLateJoin: boolean;
  quorumUserFinishEndsMatch: boolean;
  sessionFinishMaxPayloadBytes: number;
  sessionFinishBroadcast: boolean;
  rejectInputAfterSessionFinish: boolean;
  abandonMatchTimeoutSeconds: number;
  matchInstanceId: string;
  matchAccess: MatchAccessMode;
  matchPassword: string;
  allowedMatchTokens: Set<string>;
  rewardOutcomeDelivery: RewardOutcomeDeliveryMode;
  onRewardHookPerUser?: (payload: RewardPerUserHookPayload) => void;
  onRewardHookMatch?: (payload: RewardMatchHookPayload) => void;
};

function resolveMatchAccess(
  raw: string | undefined,
): MatchAccessMode {
  const s = (raw ?? 'open').trim().toLowerCase();
  if (s === 'password') return 'password';
  if (s === 'token') return 'token';
  return 'open';
}

function resolveOptions(o?: InputSyncerServerOptions): ResolvedServerOptions {
  const matchAccess = resolveMatchAccess(o?.matchAccess);
  let matchPassword = '';
  let allowedMatchTokens = new Set<string>();
  if (matchAccess === 'password') {
    matchPassword = o?.matchPassword ?? '';
  }
  if (matchAccess === 'token') {
    allowedMatchTokens = new Set(o?.allowedMatchTokens ?? []);
  }

  return {
    maxPlayers: o?.maxPlayers ?? 2,
    autoStartWhenFull: o?.autoStartWhenFull ?? false,
    stepIntervalSeconds: o?.stepIntervalSeconds ?? 0.1,
    allowLateJoin: o?.allowLateJoin ?? false,
    sendStepHistoryOnLateJoin: o?.sendStepHistoryOnLateJoin ?? true,
    quorumUserFinishEndsMatch: o?.quorumUserFinishEndsMatch ?? true,
    sessionFinishMaxPayloadBytes: o?.sessionFinishMaxPayloadBytes ?? 4096,
    sessionFinishBroadcast: o?.sessionFinishBroadcast ?? true,
    rejectInputAfterSessionFinish: o?.rejectInputAfterSessionFinish ?? false,
    abandonMatchTimeoutSeconds: o?.abandonMatchTimeoutSeconds ?? 0,
    matchInstanceId: o?.matchInstanceId ?? '',
    matchAccess,
    matchPassword,
    allowedMatchTokens,
    rewardOutcomeDelivery:
      o?.rewardOutcomeDelivery ?? RewardOutcomeDeliveryMode.ClientToAdmin,
    onRewardHookPerUser: o?.onRewardHookPerUser,
    onRewardHookMatch: o?.onRewardHookMatch,
  };
}

/**
 * Per-match server logic. This is a plain class — NOT a NestJS provider.
 * One instance is created per match by InputSyncerPoolService.
 */
export class InputSyncerServer {
  readonly options: ResolvedServerOptions;
  private readonly state = new InputSyncerState();
  private stepInterval: ReturnType<typeof setInterval> | null = null;
  private disposed = false;
  private abandonDeadlineMs: number | null = null;
  lastFinishReason: string = InputSyncerFinishReasons.Completed;

  sendToSocket: SendToSocketFn = () => {};

  onPlayerConnected: (player: InputSyncerPlayer) => void = () => {};
  onPlayerDisconnected: (player: InputSyncerPlayer) => void = () => {};
  onPlayerJoined: (player: InputSyncerPlayer) => void = () => {};
  onPlayerFinished: (player: InputSyncerPlayer) => void = () => {};
  onPlayerSessionFinished: (player: InputSyncerPlayer) => void = () => {};
  onMatchStarted: () => void = () => {};
  onMatchFinished: () => void = () => {};
  onMatchFinishedWithReason: (reason: string) => void = () => {};
  onStepBroadcast: (step: number, stepInputs: StepInputs) => void = () => {};

  constructor(options?: InputSyncerServerOptions) {
    this.options = resolveOptions(options);
  }

  startMatch(): void {
    if (this.state.matchStarted) return;

    this.state.matchStarted = true;
    this.state.currentStep = 0;

    const intervalMs = this.options.stepIntervalSeconds * 1000;
    this.stepInterval = setInterval(() => this.processStep(), intervalMs);

    this.broadcastToJoined(InputSyncerEvents.INPUT_SYNCER_START_EVENT, {});
    this.onMatchStarted();
    this.updateAbandonDeadline();
  }

  finishMatch(reason: string = InputSyncerFinishReasons.Completed): void {
    if (!this.state.matchStarted || this.state.matchFinished) return;

    const joinedUserIds = [...this.state.players.values()]
      .filter((p) => p.joined)
      .map((p) => p.userId);

    this.state.matchFinished = true;
    this.abandonDeadlineMs = null;
    this.clearStepInterval();

    this.lastFinishReason = reason ?? InputSyncerFinishReasons.Completed;
    this.broadcastToJoined(InputSyncerEvents.INPUT_SYNCER_FINISH_EVENT, {
      reason: this.lastFinishReason,
    });
    this.onMatchFinished();
    this.onMatchFinishedWithReason(this.lastFinishReason);

    if (
      this.options.rewardOutcomeDelivery ===
      RewardOutcomeDeliveryMode.ServerHookMatchOrReferee
    ) {
      try {
        const payload: RewardMatchHookPayload = {
          matchInstanceId: this.options.matchInstanceId,
          reason: this.lastFinishReason,
          joinedUserIds,
        };
        this.options.onRewardHookMatch?.(payload);
      } catch (e) {
        console.warn('[InputSyncerServer] onRewardHookMatch failed', e);
      }
    }
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;

    this.clearStepInterval();
    this.state.players.clear();
    this.state.stepHistory.clear();
    this.state.pendingInputs = [];
  }

  addPlayer(socketId: string): void {
    const player = new InputSyncerPlayer(socketId);
    this.state.players.set(socketId, player);
    this.onPlayerConnected(player);
  }

  removePlayer(socketId: string): void {
    const player = this.state.players.get(socketId);
    if (!player) return;

    this.state.players.delete(socketId);
    this.onPlayerDisconnected(player);
    this.checkMatchAbandonAfterPlayerRemoved();
  }

  handleJoin(socketId: string, data?: Record<string, unknown>): void {
    const player = this.state.players.get(socketId);
    if (!player) return;

    if (player.joined) {
      if (
        !this.state.matchStarted &&
        data?.userId != null &&
        typeof data.userId === 'string' &&
        data.userId.length > 0
      ) {
        player.userId = data.userId;
      }
      return;
    }

    if (this.state.matchStarted && !this.options.allowLateJoin) return;

    if (this.getJoinedPlayerCount() >= this.options.maxPlayers) {
      this.sendToSocket(socketId, InputSyncerEvents.INPUT_SYNCER_CONTENT_ERROR, {
        reason: 'match-full',
        message: `Match is full (${this.options.maxPlayers} players max)`,
      });
      return;
    }

    const userId =
      (data?.userId as string) || defaultAnonymousUserId(socketId);
    player.userId = userId;
    player.joined = true;

    this.onPlayerJoined(player);

    if (
      this.state.matchStarted &&
      this.options.allowLateJoin &&
      this.options.sendStepHistoryOnLateJoin
    ) {
      this.sendAllStepsToPlayer(socketId);
    }

    if (
      this.options.autoStartWhenFull &&
      !this.state.matchStarted &&
      this.getJoinedPlayerCount() >= this.options.maxPlayers
    ) {
      this.startMatch();
    } else {
      this.updateAbandonDeadline();
    }
  }

  handleInput(socketId: string, data?: Record<string, unknown>): void {
    const player = this.state.players.get(socketId);
    if (!player) return;
    if (!player.joined || !this.state.matchStarted || this.state.matchFinished)
      return;
    if (this.options.rejectInputAfterSessionFinish && player.sessionFinished)
      return;

    if (!data || typeof data !== 'object' || Array.isArray(data)) return;

    const inputData = data.inputData as Record<string, unknown> | undefined;
    if (
      inputData &&
      typeof inputData === 'object' &&
      !Array.isArray(inputData)
    ) {
      this.state.pendingInputs.push({
        ...inputData,
        userId: player.userId,
      });
    } else {
      this.state.pendingInputs.push({
        ...data,
        userId: player.userId,
      });
    }
  }

  handleUserFinish(socketId: string): void {
    const player = this.state.players.get(socketId);
    if (!player) return;
    if (!player.joined || player.finished) return;

    player.finished = true;

    this.broadcastToJoined(InputSyncerEvents.INPUT_SYNCER_USER_FINISH_EVENT, {
      userId: player.userId,
    });
    this.onPlayerFinished(player);

    if (
      this.options.quorumUserFinishEndsMatch &&
      this.state.matchStarted &&
      !this.state.matchFinished
    ) {
      const allFinished = [...this.state.players.values()]
        .filter((p) => p.joined)
        .every((p) => p.finished);

      if (allFinished) {
        this.finishMatch(InputSyncerFinishReasons.Completed);
      }
    }
  }

  handlePlayerSessionFinish(
    socketId: string,
    data?: Record<string, unknown>,
  ): void {
    const player = this.state.players.get(socketId);
    if (!player) return;
    if (!player.joined || player.sessionFinished) return;

    const payloadData = extractSessionFinishData(data);
    const serialized = JSON.stringify(payloadData ?? {});
    const byteCount = Buffer.byteLength(serialized, 'utf8');
    if (byteCount > this.options.sessionFinishMaxPayloadBytes) {
      console.warn(
        `[InputSyncerServer] player-session-finish payload too large (${byteCount} bytes)`,
      );
      return;
    }

    player.sessionFinished = true;

    const outbound = { userId: player.userId, data: payloadData ?? {} };

    if (this.options.sessionFinishBroadcast) {
      this.broadcastToJoined(
        InputSyncerEvents.INPUT_SYNCER_PLAYER_SESSION_FINISH_EVENT,
        outbound,
      );
    } else {
      this.sendToSocket(
        socketId,
        InputSyncerEvents.INPUT_SYNCER_PLAYER_SESSION_FINISH_EVENT,
        outbound,
      );
    }

    this.onPlayerSessionFinished(player);

    if (
      this.options.rewardOutcomeDelivery ===
      RewardOutcomeDeliveryMode.ServerHookPerUser
    ) {
      try {
        const hookPayload: RewardPerUserHookPayload = {
          matchInstanceId: this.options.matchInstanceId,
          userId: player.userId,
          data: payloadData ?? {},
        };
        this.options.onRewardHookPerUser?.(hookPayload);
      } catch (e) {
        console.warn('[InputSyncerServer] onRewardHookPerUser failed', e);
      }
    }
  }

  handleRequestAllSteps(socketId: string): void {
    const player = this.state.players.get(socketId);
    if (!player) return;

    this.sendAllStepsToPlayer(socketId);
  }

  getPlayerCount(): number {
    return this.state.players.size;
  }

  getJoinedPlayerCount(): number {
    return [...this.state.players.values()].filter((p) => p.joined).length;
  }

  getPlayers(): InputSyncerPlayer[] {
    return [...this.state.players.values()];
  }

  getJoinedPlayers(): InputSyncerPlayer[] {
    return [...this.state.players.values()].filter((p) => p.joined);
  }

  get isMatchStarted(): boolean {
    return this.state.matchStarted;
  }

  get isMatchFinished(): boolean {
    return this.state.matchFinished;
  }

  get currentStep(): number {
    return this.state.currentStep;
  }

  sendJsonToAll(event: string, data: unknown): void {
    this.broadcastToJoined(event, data);
  }

  sendJsonToPlayer(userId: string, event: string, data: unknown): void {
    const player = [...this.state.players.values()].find(
      (p) => p.userId === userId,
    );
    if (player) {
      this.sendToSocket(player.socketId, event, data);
    }
  }

  private checkMatchAbandonAfterPlayerRemoved(): void {
    if (!this.state.matchStarted || this.state.matchFinished) return;

    const joined = this.getJoinedPlayerCount();
    if (joined === 0) {
      this.finishMatch(InputSyncerFinishReasons.AllDisconnected);
      return;
    }

    if (!this.options.allowLateJoin && joined < this.options.maxPlayers) {
      this.finishMatch(InputSyncerFinishReasons.InsufficientPlayers);
      return;
    }

    this.updateAbandonDeadline();
  }

  private updateAbandonDeadline(): void {
    if (
      this.options.abandonMatchTimeoutSeconds <= 0 ||
      !this.state.matchStarted ||
      this.state.matchFinished
    ) {
      this.abandonDeadlineMs = null;
      return;
    }

    if (!this.options.allowLateJoin) {
      this.abandonDeadlineMs = null;
      return;
    }

    const joined = this.getJoinedPlayerCount();
    if (joined > 0 && joined < this.options.maxPlayers) {
      this.abandonDeadlineMs =
        Date.now() + this.options.abandonMatchTimeoutSeconds * 1000;
    } else {
      this.abandonDeadlineMs = null;
    }
  }

  private checkAbandonDeadlineExpired(): void {
    if (
      !this.state.matchStarted ||
      this.state.matchFinished ||
      this.abandonDeadlineMs == null
    )
      return;

    if (Date.now() >= this.abandonDeadlineMs) {
      this.abandonDeadlineMs = null;
      this.finishMatch(InputSyncerFinishReasons.AbandonTimeout);
    }
  }

  private processStep(): void {
    try {
      this.checkAbandonDeadlineExpired();
      this.processStepCore();
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      const stack = e instanceof Error ? e.stack : '';
      console.error('[InputSyncerServer] processStep FAILED:', msg);
      if (stack) console.error(stack);
      try {
        console.error(
          '[InputSyncerServer] pendingInputs (JSON):',
          JSON.stringify(this.state.pendingInputs),
        );
      } catch {
        console.error(
          '[InputSyncerServer] pendingInputs: (could not JSON.stringify)',
        );
      }
      console.error(
        '[InputSyncerServer] step=',
        this.state.currentStep,
        'matchStarted=',
        this.state.matchStarted,
        'matchFinished=',
        this.state.matchFinished,
      );
      throw e;
    }
  }

  private processStepCore(): void {
    if (!this.state.matchStarted || this.state.matchFinished) return;

    const inputs: Record<string, unknown>[] = [];
    let index = 0;
    for (const pendingInput of this.state.pendingInputs) {
      if (
        pendingInput &&
        typeof pendingInput === 'object' &&
        !Array.isArray(pendingInput)
      ) {
        inputs.push({ ...pendingInput, index: index++ });
      }
    }
    this.state.pendingInputs = [];

    const stepInputs: StepInputs = {
      step: this.state.currentStep,
      inputs,
    };

    this.state.stepHistory.set(this.state.currentStep, stepInputs);

    const stepsArray: StepInputs[] = [stepInputs];
    this.broadcastToJoined(
      InputSyncerEvents.INPUT_SYNCER_STEPS_EVENT,
      stepsArray,
    );

    this.onStepBroadcast(this.state.currentStep, stepInputs);
    this.state.currentStep++;
  }

  private sendAllStepsToPlayer(socketId: string): void {
    const player = this.state.players.get(socketId);

    const steps = [...this.state.stepHistory.values()].sort(
      (a, b) => a.step - b.step,
    );

    const allSteps: AllStepInputs = {
      requestedUser: player?.userId ?? '',
      steps,
      lastSentStep:
        this.state.currentStep > 0 ? this.state.currentStep - 1 : 0,
    };

    this.sendToSocket(
      socketId,
      InputSyncerEvents.INPUT_SYNCER_ALL_STEPS_EVENT,
      allSteps,
    );
  }

  private broadcastToJoined(event: string, data: unknown): void {
    for (const player of this.state.players.values()) {
      if (player.joined) {
        this.sendToSocket(player.socketId, event, data);
      }
    }
  }

  private clearStepInterval(): void {
    if (this.stepInterval) {
      clearInterval(this.stepInterval);
      this.stepInterval = null;
    }
  }
}

function extractSessionFinishData(
  data?: Record<string, unknown>,
): Record<string, unknown> | null {
  if (data == null || typeof data !== 'object' || Array.isArray(data))
    return {};
  if (data.data != null && typeof data.data === 'object' && !Array.isArray(data.data)) {
    return data.data as Record<string, unknown>;
  }
  return data as Record<string, unknown>;
}
