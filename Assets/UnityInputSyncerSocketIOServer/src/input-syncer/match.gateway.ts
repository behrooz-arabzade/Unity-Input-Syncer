import {
  WebSocketGateway,
  WebSocketServer,
  SubscribeMessage,
  OnGatewayConnection,
  OnGatewayDisconnect,
  MessageBody,
  ConnectedSocket,
} from '@nestjs/websockets';
import { Logger } from '@nestjs/common';
import { Server, Socket } from 'socket.io';
import { InputSyncerPoolService } from './pool.service';
import { InputSyncerEvents } from './input-syncer-events';

/**
 * SocketIOUnity serializes Emit args as JSON arrays. Unwrap nested `[[{...}]]` down to a plain object.
 */
function normalizeMessageBody(data: unknown): Record<string, unknown> | undefined {
  let current: unknown = data;
  while (current != null && Array.isArray(current)) {
    if (current.length === 0) return undefined;
    current = current[0];
  }
  if (
    current !== null &&
    typeof current === 'object' &&
    !Array.isArray(current)
  ) {
    return current as Record<string, unknown>;
  }
  return undefined;
}

function logGatewayError(
  logger: Logger,
  where: string,
  socketId: string,
  e: unknown,
): void {
  const msg = e instanceof Error ? e.message : String(e);
  const stack = e instanceof Error ? e.stack : undefined;
  logger.error(
    `${where} (socket=${socketId.slice(0, 12)}…): ${msg}`,
    stack ?? '',
  );
}

@WebSocketGateway({
  path: '/match-gateway',
  transports: ['websocket'],
  cors: { origin: '*' },
})
export class MatchGateway implements OnGatewayConnection, OnGatewayDisconnect {
  private readonly logger = new Logger(MatchGateway.name);

  /** socketId -> instanceId */
  private readonly socketToInstance = new Map<string, string>();

  @WebSocketServer()
  server!: Server;

  constructor(private readonly pool: InputSyncerPoolService) {}

  handleConnection(socket: Socket): void {
    try {
      this.handleConnectionInner(socket);
    } catch (e) {
      logGatewayError(this.logger, 'handleConnection', socket.id, e);
      throw e;
    }
  }

  private handleConnectionInner(socket: Socket): void {
    const matchId = socket.handshake.query.matchId as string | undefined;

    if (!matchId) {
      this.logger.warn(
        `Socket ${socket.id} connected without matchId — disconnecting`,
      );
      socket.emit(InputSyncerEvents.INPUT_SYNCER_CONTENT_ERROR, {
        reason: 'missing-match-id',
        message: 'matchId query parameter is required',
      });
      socket.disconnect(true);
      return;
    }

    const instance = this.pool.getInstance(matchId);
    if (!instance) {
      this.logger.warn(
        `Socket ${socket.id} requested unknown match ${matchId} — disconnecting`,
      );
      socket.emit(InputSyncerEvents.INPUT_SYNCER_CONTENT_ERROR, {
        reason: 'instance-not-found',
        message: `Match instance '${matchId}' does not exist`,
      });
      socket.disconnect(true);
      return;
    }

    this.socketToInstance.set(socket.id, matchId);

    instance.server.sendToSocket = (
      targetSocketId: string,
      event: string,
      data: unknown,
    ) => {
      try {
        this.server.to(targetSocketId).emit(event, data);
      } catch (e) {
        const msg = e instanceof Error ? e.message : String(e);
        const stack = e instanceof Error ? e.stack : '';
        this.logger.error(
          `emit failed (${event} → ${targetSocketId.slice(0, 8)}…): ${msg}`,
          stack,
        );
      }
    };

    instance.server.addPlayer(socket.id);
    this.logger.log(`Socket ${socket.id} connected to match ${matchId}`);
  }

  handleDisconnect(socket: Socket): void {
    const instanceId = this.socketToInstance.get(socket.id);
    if (!instanceId) return;

    const instance = this.pool.getInstance(instanceId);
    if (instance) {
      instance.server.removePlayer(socket.id);
    }

    this.socketToInstance.delete(socket.id);
    this.logger.log(
      `Socket ${socket.id} disconnected from match ${instanceId}`,
    );
  }

  @SubscribeMessage(InputSyncerEvents.MATCH_USER_JOIN_EVENT)
  handleJoin(
    @ConnectedSocket() socket: Socket,
    @MessageBody() data: unknown,
  ): void {
    try {
      const server = this.getServerForSocket(socket.id);
      if (!server) return;
      server.handleJoin(socket.id, normalizeMessageBody(data));
    } catch (e) {
      logGatewayError(this.logger, 'handleJoin', socket.id, e);
      throw e;
    }
  }

  @SubscribeMessage(InputSyncerEvents.MATCH_USER_INPUT_EVENT)
  handleInput(
    @ConnectedSocket() socket: Socket,
    @MessageBody() data: unknown,
  ): void {
    try {
      const server = this.getServerForSocket(socket.id);
      if (!server) return;
      server.handleInput(socket.id, normalizeMessageBody(data));
    } catch (e) {
      logGatewayError(this.logger, 'handleInput', socket.id, e);
      throw e;
    }
  }

  @SubscribeMessage(InputSyncerEvents.MATCH_USER_FINISH_EVENT)
  handleUserFinish(@ConnectedSocket() socket: Socket): void {
    try {
      const server = this.getServerForSocket(socket.id);
      if (!server) return;
      server.handleUserFinish(socket.id);
    } catch (e) {
      logGatewayError(this.logger, 'handleUserFinish', socket.id, e);
      throw e;
    }
  }

  @SubscribeMessage(InputSyncerEvents.MATCH_USER_REQUEST_ALL_STEPS_EVENT)
  handleRequestAllSteps(@ConnectedSocket() socket: Socket): void {
    try {
      const server = this.getServerForSocket(socket.id);
      if (!server) return;
      server.handleRequestAllSteps(socket.id);
    } catch (e) {
      logGatewayError(this.logger, 'handleRequestAllSteps', socket.id, e);
      throw e;
    }
  }

  private getServerForSocket(socketId: string) {
    const instanceId = this.socketToInstance.get(socketId);
    if (!instanceId) return null;

    const instance = this.pool.getInstance(instanceId);
    return instance?.server ?? null;
  }
}
