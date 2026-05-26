import { Injectable, Logger } from "@nestjs/common";
import { NakamaService } from "./nakama.service";

interface MatchInputLogStep {
  step: number;
  inputs: Record<string, unknown>[];
}

interface MatchInputLogPutPayload {
  match_id: string;
  log_steps: MatchInputLogStep[];
  finish_reason: string;
}

interface MatchInputLogPutResponse {
  ok: boolean;
  step_count: number;
  bytes: number;
}

@Injectable()
export class NakamaMatchInputLogService {
  private readonly logger = new Logger(NakamaMatchInputLogService.name);

  constructor(private readonly nakama: NakamaService) {}

  /**
   * Fire-and-forget upload of the per-step input log captured by InputSyncer
   * on match finish. Failures are logged and swallowed: replay metadata must
   * never block match teardown or reward delivery.
   */
  async uploadInputLog(
    nakamaMatchId: string | undefined,
    logSteps: MatchInputLogStep[],
    finishReason: string,
  ): Promise<void> {
    if (!nakamaMatchId) {
      this.logger.debug("Skipping input log upload: no nakamaMatchId set");
      return;
    }

    if (!this.nakama.isConfigured()) {
      this.logger.warn("Skipping input log upload: Nakama not configured");
      return;
    }

    try {
      const payload: MatchInputLogPutPayload = {
        match_id: nakamaMatchId,
        log_steps: logSteps,
        finish_reason: finishReason ?? "",
      };
      const result = await this.nakama.callRpc<MatchInputLogPutResponse>(
        "fv_match_input_log_put",
        payload,
      );
      this.logger.log(
        `Uploaded input log to Nakama: match=${nakamaMatchId} steps=${result?.step_count ?? logSteps.length} bytes=${result?.bytes ?? 0} reason=${finishReason}`,
      );
    } catch (err) {
      this.logger.error(
        `Failed to upload input log for match ${nakamaMatchId}: ${err}`,
      );
    }
  }
}
