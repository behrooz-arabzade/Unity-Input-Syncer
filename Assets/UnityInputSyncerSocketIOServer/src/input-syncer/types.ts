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

export interface AdminClientConnectionInfo {
  transport: string;
  matchId: string;
  host: string;
  port: number;
  socketIoUrl: string;
  matchGatewayPath: string;
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
  matchAccess: 'open' | 'password' | 'token';
  allowedMatchTokenCount: number;
  /** Base URL for Socket.IO client when configured (or localhost fallback). */
  serverUrl?: string;
  clientConnection?: AdminClientConnectionInfo;
}

export interface AdminCreateInstanceRequest {
  maxPlayers?: number;
  stepIntervalSeconds?: number;
  autoStartWhenFull?: boolean;
  allowLateJoin?: boolean;
  sendStepHistoryOnLateJoin?: boolean;
  matchAccess?: string;
  matchPassword?: string;
  allowedMatchTokens?: string[];
  matchData?: unknown;
  users?: Record<string, unknown>;
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
