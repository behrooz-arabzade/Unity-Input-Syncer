import { InputSyncerPlayer } from './input-syncer-player';
import { InputSyncerServer } from './input-syncer-server';
import { ServerInstanceState } from './types';

export type InstanceStateChangeHandler = (
  instance: ServerInstance,
  oldState: ServerInstanceState,
  newState: ServerInstanceState,
) => void;

export class ServerInstance {
  readonly id: string;
  readonly server: InputSyncerServer;
  readonly createdAt: Date;

  private _state = ServerInstanceState.Idle;
  private _lastStateChangeTime = new Date();

  onStateChanged: InstanceStateChangeHandler = () => {};

  constructor(id: string, server: InputSyncerServer) {
    this.id = id;
    this.server = server;
    this.createdAt = new Date();

    server.onPlayerConnected = (player: InputSyncerPlayer) =>
      this.handlePlayerConnected(player);
    server.onPlayerDisconnected = (player: InputSyncerPlayer) =>
      this.handlePlayerDisconnected(player);
    server.onMatchStarted = () => this.handleMatchStarted();
    server.onMatchFinished = () => this.handleMatchFinished();
  }

  get state(): ServerInstanceState {
    return this._state;
  }

  get lastStateChangeTime(): Date {
    return this._lastStateChangeTime;
  }

  private setState(newState: ServerInstanceState): void {
    if (this._state === newState) return;

    const oldState = this._state;
    this._state = newState;
    this._lastStateChangeTime = new Date();
    this.onStateChanged(this, oldState, newState);
  }

  private handlePlayerConnected(_player: InputSyncerPlayer): void {
    if (this._state === ServerInstanceState.Idle) {
      this.setState(ServerInstanceState.WaitingForPlayers);
    }
  }

  private handlePlayerDisconnected(_player: InputSyncerPlayer): void {
    if (
      this._state === ServerInstanceState.WaitingForPlayers &&
      this.server.getPlayerCount() === 0
    ) {
      this.setState(ServerInstanceState.Idle);
    }
  }

  private handleMatchStarted(): void {
    this.setState(ServerInstanceState.InMatch);
  }

  private handleMatchFinished(): void {
    this.setState(ServerInstanceState.Finished);
  }
}
