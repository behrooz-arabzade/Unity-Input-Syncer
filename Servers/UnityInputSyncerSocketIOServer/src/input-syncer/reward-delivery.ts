export enum RewardOutcomeDeliveryMode {
  ClientToAdmin = 0,
  ServerHookPerUser = 1,
  ServerHookMatchOrReferee = 2,
}

export type RewardPerUserHookPayload = {
  matchInstanceId: string;
  userId: string;
  data: unknown;
};

export type RewardMatchHookPayload = {
  matchInstanceId: string;
  reason: string;
  joinedUserIds: string[];
};
