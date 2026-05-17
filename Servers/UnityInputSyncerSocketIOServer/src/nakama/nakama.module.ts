import { Module } from "@nestjs/common";
import { NakamaService } from "./nakama.service";
import { NakamaMatchResultService } from "./nakama-match-result.service";
import { NakamaPoolBridge } from "./nakama-pool-bridge.service";

@Module({
  providers: [NakamaService, NakamaMatchResultService, NakamaPoolBridge],
  exports: [NakamaService, NakamaMatchResultService],
})
export class NakamaModule {}
