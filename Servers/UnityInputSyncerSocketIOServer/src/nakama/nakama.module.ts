import { Module } from "@nestjs/common";
import { NakamaService } from "./nakama.service";
import { NakamaMatchResultService } from "./nakama-match-result.service";
import { NakamaMatchAbandonService } from "./nakama-match-abandon.service";
import { NakamaMatchInputLogService } from "./nakama-match-input-log.service";
import { NakamaPoolBridge } from "./nakama-pool-bridge.service";

@Module({
  providers: [
    NakamaService,
    NakamaMatchResultService,
    NakamaMatchAbandonService,
    NakamaMatchInputLogService,
    NakamaPoolBridge,
  ],
  exports: [
    NakamaService,
    NakamaMatchResultService,
    NakamaMatchAbandonService,
    NakamaMatchInputLogService,
  ],
})
export class NakamaModule {}
