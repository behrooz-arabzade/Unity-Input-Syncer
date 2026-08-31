import { Injectable, Logger } from "@nestjs/common";
import { NakamaService } from "./nakama.service";
import { compactForUpload } from "./input-log-compaction";
import type { StepInputs } from "../input-syncer/types";

type MatchInputLogStep = StepInputs;

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

/** Nakama's `socket.max_request_size_bytes` default. Anything at or above it is refused. */
const DEFAULT_MAX_UPLOAD_BYTES = 262144;

/**
 * Message types whose stream is repetitive and recomputable, so only each user's last one is
 * uploaded. The default is the per-step state hash a lockstep client publishes in-band, which
 * measured at 98% of an uploaded history; override with
 * `INPUT_SYNCER_LOG_UPLOAD_KEEP_LAST_TYPES` (comma-separated), or set it empty to upload
 * everything.
 */
const DEFAULT_KEEP_LAST_TYPES = "state-hash";

/**
 * The envelope fields an upload carries: the message as its sender sent it, not this server's
 * per-message bookkeeping (`index`, `requestStep`, the cast timers, the force/cancel flags),
 * which is 200 of an envelope's 297 measured bytes. Override with
 * `INPUT_SYNCER_LOG_UPLOAD_FIELDS`, or set it empty to send whole envelopes.
 */
const DEFAULT_UPLOAD_FIELDS = "type,userId,data";

@Injectable()
export class NakamaMatchInputLogService {
  private readonly logger = new Logger(NakamaMatchInputLogService.name);
  private readonly keepLastTypes: string[];
  private readonly uploadFields: string[];
  private readonly maxUploadBytes: number;

  constructor(private readonly nakama: NakamaService) {
    this.keepLastTypes = list(
      process.env.INPUT_SYNCER_LOG_UPLOAD_KEEP_LAST_TYPES,
      DEFAULT_KEEP_LAST_TYPES,
    );
    this.uploadFields = list(process.env.INPUT_SYNCER_LOG_UPLOAD_FIELDS, DEFAULT_UPLOAD_FIELDS);

    const maxBytes = Number(process.env.INPUT_SYNCER_LOG_UPLOAD_MAX_BYTES);
    this.maxUploadBytes =
      Number.isFinite(maxBytes) && maxBytes > 0 ? maxBytes : DEFAULT_MAX_UPLOAD_BYTES;
  }

  /**
   * Fire-and-forget upload of the per-step input log captured by InputSyncer
   * on match finish. Failures are logged and swallowed: replay metadata must
   * never block match teardown or reward delivery.
   *
   * **The history is thinned first, and that is not an optimisation.** Uploading it whole meant
   * every real match was refused by Nakama's 262,144-byte request cap — a 26-second match is
   * already over it — and because this path swallows its own failures, no log had ever arrived.
   * `input-log-compaction.ts` has the measurements and the reasoning; what leaves here now
   * scales with a match's *inputs* rather than its clock.
   *
   * **A body still over the cap is logged as an error before it is sent**, so the next time
   * something outgrows this it says so instead of disappearing.
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

    const compacted = compactForUpload(logSteps, this.keepLastTypes, this.uploadFields);
    const payload: MatchInputLogPutPayload = {
      match_id: nakamaMatchId,
      log_steps: compacted.steps,
      finish_reason: finishReason ?? "",
    };
    const bytes = JSON.stringify(payload).length;

    if (bytes >= this.maxUploadBytes) {
      this.logger.error(
        `Input log for match ${nakamaMatchId} is ${bytes} bytes after compaction, at or over the ` +
          `${this.maxUploadBytes}-byte limit — Nakama will refuse it. Widen ` +
          `INPUT_SYNCER_LOG_UPLOAD_KEEP_LAST_TYPES (currently ` +
          `[${this.keepLastTypes.join(", ")}]) or raise the server's request cap.`,
      );
    }

    try {
      const result = await this.nakama.callRpc<MatchInputLogPutResponse>(
        "fv_match_input_log_put",
        payload,
      );
      this.logger.log(
        `Uploaded input log to Nakama: match=${nakamaMatchId} ` +
          `steps=${result?.step_count ?? compacted.steps.length} ` +
          `bytes=${result?.bytes ?? 0} reason=${finishReason} ` +
          `(sent ${bytes} bytes over ${compacted.steps.length} of ${logSteps.length} steps; ` +
          `${compacted.kept} entries kept, ${compacted.thinned} thinned)`,
      );
    } catch (err) {
      this.logger.error(
        `Failed to upload input log for match ${nakamaMatchId}: ${err}`,
      );
    }
  }
}

/** A comma-separated env list, or its default when the variable is unset. Empty means empty. */
function list(configured: string | undefined, fallback: string): string[] {
  return (configured === undefined ? fallback : configured)
    .split(",")
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
}
