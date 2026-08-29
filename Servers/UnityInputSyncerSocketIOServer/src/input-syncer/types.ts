import type { AllowedMatchTokens } from './match-access';

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
  disconnectAbandonTimeoutSeconds?: number;
  quorumUserFinishEndsMatch?: boolean;
  sessionFinishMaxPayloadBytes?: number;
  sessionFinishBroadcast?: boolean;
  rejectInputAfterSessionFinish?: boolean;
  abandonMatchTimeoutSeconds?: number;
  rewardOutcomeDelivery?: number;
  matchAccess?: string;
  matchPassword?: string;
  /** `["tok", …]` (unbound) or `{ "<userId>": "tok" }` (bound to a user). */
  allowedMatchTokens?: AllowedMatchTokens;
  matchData?: unknown;
  users?: Record<string, unknown>;
  nakamaMatchId?: string;
}

/**
 * Every key `POST /api/instances` will accept. Anything else is a 400 naming the field,
 * because a body key that is silently ignored is a configuration change that appears to
 * have been applied and was not.
 *
 * The tail of the list is **accepted and deliberately unused**: allocators send context
 * with the request (Nakama's match manager sends `participant_user_ids` and `matchmaker`),
 * and refusing that would make an allocator-side addition break allocation outright.
 * Anything genuinely new from an allocator must be added here in the same change.
 */
export const ADMIN_CREATE_INSTANCE_KEYS: readonly string[] = [
  'maxPlayers',
  'stepIntervalSeconds',
  'autoStartWhenFull',
  'allowLateJoin',
  'sendStepHistoryOnLateJoin',
  'disconnectAbandonTimeoutSeconds',
  'quorumUserFinishEndsMatch',
  'sessionFinishMaxPayloadBytes',
  'sessionFinishBroadcast',
  'rejectInputAfterSessionFinish',
  'abandonMatchTimeoutSeconds',
  'rewardOutcomeDelivery',
  'matchAccess',
  'matchPassword',
  'allowedMatchTokens',
  'matchData',
  'users',
  'nakamaMatchId',
  // Accepted, never applied — allocator context.
  'participant_user_ids',
  'matchmaker',
];

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
