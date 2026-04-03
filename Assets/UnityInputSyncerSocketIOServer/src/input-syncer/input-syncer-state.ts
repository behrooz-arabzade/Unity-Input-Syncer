import { InputSyncerPlayer } from './input-syncer-player';
import { StepInputs } from './types';

export class InputSyncerState {
  matchStarted = false;
  matchFinished = false;
  currentStep = 0;
  stepHistory: Map<number, StepInputs> = new Map();
  pendingInputs: Record<string, unknown>[] = [];
  players: Map<string, InputSyncerPlayer> = new Map();
}
