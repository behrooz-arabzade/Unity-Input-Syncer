import { Injectable, Logger } from "@nestjs/common";
import { NakamaService } from "./nakama.service";

interface MatchUserFinishPayload {
  match_id: string;
  user_id: string;
  data: Record<string, unknown>;
}

export interface NakamaUserFinishResult {
  ok: boolean;
  match_id: string;
  match_status: "waiting" | "completed" | "cancelled";
  user_outcome: "win" | "loss" | "draw";
  mmr_update: { user_id: string; old_mmr: number; new_mmr: number; delta: number; outcome: string } | null;
}

@Injectable()
export class NakamaMatchResultService {
  private readonly logger = new Logger(NakamaMatchResultService.name);

  constructor(private readonly nakama: NakamaService) {}

  /**
   * Called immediately when a user sends player-session-finish.
   * Calls Nakama to process their result and returns the response
   * so Socket.IO can forward it back to the user.
   */
  async reportUserFinish(
    nakamaMatchId: string | undefined,
    userId: string,
    data: Record<string, unknown>,
  ): Promise<NakamaUserFinishResult | null> {
    if (!nakamaMatchId) {
      this.logger.debug("Skipping Nakama user finish report: no nakamaMatchId set");
      return null;
    }

    if (!this.nakama.isConfigured()) {
      this.logger.warn("Skipping Nakama user finish report: Nakama not configured");
      return null;
    }

    try {
      const payload: MatchUserFinishPayload = {
        match_id: nakamaMatchId,
        user_id: userId,
        data,
      };
      const result = await this.nakama.callRpc<NakamaUserFinishResult>("fv_match_user_finish", payload);
      this.logger.log(`Reported user finish to Nakama: match=${nakamaMatchId} user=${userId} outcome=${result.user_outcome}`);
      return result;
    } catch (err) {
      this.logger.error(`Failed to report user finish for ${userId} in match ${nakamaMatchId}: ${err}`);
      return null;
    }
  }
}
