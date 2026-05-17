import { Injectable, Logger } from "@nestjs/common";
import { NakamaService } from "./nakama.service";

interface MatchAbandonReportPayload {
  match_id: string;
  user_id: string;
}

@Injectable()
export class NakamaMatchAbandonService {
  private readonly logger = new Logger(NakamaMatchAbandonService.name);

  constructor(private readonly nakama: NakamaService) {}

  async reportAbandon(
    nakamaMatchId: string | undefined,
    userId: string,
  ): Promise<void> {
    if (!nakamaMatchId) {
      this.logger.debug("Skipping abandon report: no nakamaMatchId set");
      return;
    }

    if (!this.nakama.isConfigured()) {
      this.logger.warn("Skipping abandon report: Nakama not configured");
      return;
    }

    try {
      const payload: MatchAbandonReportPayload = {
        match_id: nakamaMatchId,
        user_id: userId,
      };
      await this.nakama.callRpc("fv_match_abandon_report", payload);
      this.logger.log(`Reported abandon to Nakama: match=${nakamaMatchId} user=${userId}`);
    } catch (err) {
      this.logger.error(`Failed to report abandon for ${userId} in match ${nakamaMatchId}: ${err}`);
    }
  }
}
