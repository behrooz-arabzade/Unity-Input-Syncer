import { Injectable, Logger, OnModuleInit } from "@nestjs/common";
import { InputSyncerPoolService } from "../input-syncer/pool.service";
import { NakamaMatchResultService } from "./nakama-match-result.service";

@Injectable()
export class NakamaPoolBridge implements OnModuleInit {
  private readonly logger = new Logger(NakamaPoolBridge.name);

  constructor(
    private readonly pool: InputSyncerPoolService,
    private readonly matchResult: NakamaMatchResultService,
  ) {}

  onModuleInit(): void {
    this.pool.registerAfterPlayerSessionFinishedHandler((instance, userId, data) => {
      const nakamaMatchId = instance.server.options.nakamaMatchId;

      this.matchResult.reportUserFinish(nakamaMatchId, userId, data).then((result) => {
        if (!result) return;

        // Send the Nakama response back to the user's socket
        const players = instance.server.getJoinedPlayers();
        const player = players.find((p) => p.userId === userId);
        if (player) {
          instance.server.sendToSocket(player.socketId, "on-match-result", result);
        }
      }).catch((err) => {
        this.logger.error(`Failed in afterPlayerSessionFinished for ${userId}: ${err}`);
      });
    });
  }
}
