import { Module } from "@nestjs/common";
import { NakamaService } from "./nakama.service";
import { NakamaMatchResultService } from "./nakama-match-result.service";
import { NakamaMatchAbandonService } from "./nakama-match-abandon.service";
import { NakamaPoolBridge } from "./nakama-pool-bridge.service";

@Module({
  providers: [NakamaService, NakamaMatchResultService, NakamaMatchAbandonService, NakamaPoolBridge],
  exports: [NakamaService, NakamaMatchResultService, NakamaMatchAbandonService],
})
export class NakamaModule {}
