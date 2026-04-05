export const InputSyncerEvents = {
  // Server -> Client
  INPUT_SYNCER_STEPS_EVENT: 'on-steps',
  INPUT_SYNCER_ALL_STEPS_EVENT: 'on-all-steps',
  INPUT_SYNCER_FINISH_EVENT: 'on-finish',
  INPUT_SYNCER_USER_FINISH_EVENT: 'on-user-finish',
  INPUT_SYNCER_START_EVENT: 'on-start',
  INPUT_SYNCER_CONTENT_ERROR: 'content-error',

  // Client -> Server
  MATCH_USER_REQUEST_ALL_STEPS_EVENT: 'request-all-steps',
  MATCH_USER_JOIN_EVENT: 'join',
  MATCH_USER_INPUT_EVENT: 'input',
  MATCH_USER_FINISH_EVENT: 'user-finish',
  MATCH_PLAYER_SESSION_FINISH_EVENT: 'player-session-finish',
  INPUT_SYNCER_PLAYER_SESSION_FINISH_EVENT: 'on-player-session-finish',
} as const;
