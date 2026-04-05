export const InputSyncerFinishReasons = {
  Completed: 'completed',
  AllDisconnected: 'all_disconnected',
  InsufficientPlayers: 'insufficient_players',
  AbandonTimeout: 'abandon_timeout',
  MaxInstanceLifetime: 'max_instance_lifetime',
} as const;

export type InputSyncerFinishReason =
  (typeof InputSyncerFinishReasons)[keyof typeof InputSyncerFinishReasons];
