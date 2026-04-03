export interface StepInputs {
  step: number;
  inputs: Record<string, unknown>[];
}

export interface AllStepInputs {
  requestedUser: string;
  steps: StepInputs[];
  lastSentStep: number;
}

export enum ServerInstanceState {
  Idle = 'Idle',
  WaitingForPlayers = 'WaitingForPlayers',
  InMatch = 'InMatch',
  Finished = 'Finished',
}

export interface AdminInstanceInfo {
  id: string;
  state: string;
  playerCount: number;
  joinedPlayerCount: number;
  matchStarted: boolean;
  matchFinished: boolean;
  createdAt: string;
  currentStep: number;
  uptimeSeconds: number;
}

export interface AdminCreateInstanceRequest {
  maxPlayers?: number;
  stepIntervalSeconds?: number;
  autoStartWhenFull?: boolean;
  allowLateJoin?: boolean;
  sendStepHistoryOnLateJoin?: boolean;
}

export interface AdminResourceUsage {
  heapUsedBytes: number;
  rssBytes: number;
  processorCount: number;
}

export interface AdminPoolStats {
  totalInstances: number;
  availableSlots: number;
  idleCount: number;
  waitingCount: number;
  inMatchCount: number;
  finishedCount: number;
  instances: AdminInstanceInfo[];
  resourceUsage: AdminResourceUsage;
}
