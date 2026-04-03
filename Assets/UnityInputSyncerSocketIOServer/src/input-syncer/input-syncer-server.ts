import { InputSyncerEvents } from './input-syncer-events';
import { InputSyncerPlayer } from './input-syncer-player';
import { InputSyncerState } from './input-syncer-state';
import { InputSyncerServerOptions } from './interfaces';
import { AllStepInputs, StepInputs } from './types';

export type SendToSocketFn = (
  socketId: string,
  event: string,
  data: unknown,
) => void;

const DEFAULT_OPTIONS: Required<InputSyncerServerOptions> = {
  maxPlayers: 2,
  autoStartWhenFull: false,
  stepIntervalSeconds: 0.1,
  allowLateJoin: false,
  sendStepHistoryOnLateJoin: true,
};

/**
 * Per-match server logic. This is a plain class — NOT a NestJS provider.
 * One instance is created per match by InputSyncerPoolService.
 */
export class InputSyncerServer {
  readonly options: Required<InputSyncerServerOptions>;
  private readonly state = new InputSyncerState();
  private stepInterval: ReturnType<typeof setInterval> | null = null;
  private disposed = false;

  // Injected by the gateway to actually send data over sockets
  sendToSocket: SendToSocketFn = () => {};

  // Event callbacks for external listeners (pool state machine, custom game logic)
  onPlayerConnected: (player: InputSyncerPlayer) => void = () => {};
  onPlayerDisconnected: (player: InputSyncerPlayer) => void = () => {};
  onPlayerJoined: (player: InputSyncerPlayer) => void = () => {};
  onPlayerFinished: (player: InputSyncerPlayer) => void = () => {};
  onMatchStarted: () => void = () => {};
  onMatchFinished: () => void = () => {};
  onStepBroadcast: (step: number, stepInputs: StepInputs) => void = () => {};

  constructor(options?: InputSyncerServerOptions) {
    this.options = { ...DEFAULT_OPTIONS, ...options };
  }

  // -------------------------
  // LIFECYCLE
  // -------------------------

  startMatch(): void {
    if (this.state.matchStarted) return;

    this.state.matchStarted = true;
    this.state.currentStep = 0;

    const intervalMs = this.options.stepIntervalSeconds * 1000;
    this.stepInterval = setInterval(() => this.processStep(), intervalMs);

    this.broadcastToJoined(InputSyncerEvents.INPUT_SYNCER_START_EVENT, {});
    this.onMatchStarted();
  }

  finishMatch(): void {
    if (!this.state.matchStarted || this.state.matchFinished) return;

    this.state.matchFinished = true;
    this.clearStepInterval();

    this.broadcastToJoined(InputSyncerEvents.INPUT_SYNCER_FINISH_EVENT, {});
    this.onMatchFinished();
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;

    this.clearStepInterval();
    this.state.players.clear();
    this.state.stepHistory.clear();
    this.state.pendingInputs = [];
  }

  // -------------------------
  // PLAYER MANAGEMENT
  // -------------------------

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
  }

  // -------------------------
  // PROTOCOL HANDLERS
  // -------------------------

  handleJoin(socketId: string, data?: Record<string, unknown>): void {
    const player = this.state.players.get(socketId);
    if (!player) return;
    if (player.joined) return;

    if (this.state.matchStarted && !this.options.allowLateJoin) return;

    if (this.getJoinedPlayerCount() >= this.options.maxPlayers) {
      this.sendToSocket(socketId, InputSyncerEvents.INPUT_SYNCER_CONTENT_ERROR, {
        reason: 'match-full',
        message: `Match is full (${this.options.maxPlayers} players max)`,
      });
      return;
    }

    const userId =
      (data?.userId as string) || `player-${socketId.substring(0, 8)}`;
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
    }
  }

  handleInput(socketId: string, data?: Record<string, unknown>): void {
    const player = this.state.players.get(socketId);
    if (!player) return;
    if (!player.joined || !this.state.matchStarted || this.state.matchFinished)
      return;

    if (!data || typeof data !== 'object') return;

    const inputData = data.inputData as Record<string, unknown> | undefined;
    if (inputData && typeof inputData === 'object') {
      inputData.userId = player.userId;
      this.state.pendingInputs.push(inputData);
    } else {
      data.userId = player.userId;
      this.state.pendingInputs.push(data);
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

    if (this.state.matchStarted && !this.state.matchFinished) {
      const allFinished = [...this.state.players.values()]
        .filter((p) => p.joined)
        .every((p) => p.finished);

      if (allFinished) {
        this.finishMatch();
      }
    }
  }

  handleRequestAllSteps(socketId: string): void {
    const player = this.state.players.get(socketId);
    if (!player) return;

    this.sendAllStepsToPlayer(socketId);
  }

  // -------------------------
  // QUERIES
  // -------------------------

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

  // -------------------------
  // PUBLIC MESSAGING
  // -------------------------

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

  // -------------------------
  // INTERNALS
  // -------------------------

  private processStep(): void {
    if (!this.state.matchStarted || this.state.matchFinished) return;

    const inputs: Record<string, unknown>[] = [];
    let index = 0;
    for (const pendingInput of this.state.pendingInputs) {
      pendingInput.index = index++;
      inputs.push(pendingInput);
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
