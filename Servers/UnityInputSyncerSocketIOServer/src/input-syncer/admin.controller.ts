import {
  Body,
  Controller,
  Delete,
  Get,
  HttpCode,
  HttpException,
  HttpStatus,
  Inject,
  Param,
  Post,
  UseGuards,
} from '@nestjs/common';
import * as os from 'os';
import { InputSyncerPoolService } from './pool.service';
import { BearerAuthGuard } from './admin.guard';
import { ServerInstance } from './server-instance';
import {
  INPUT_SYNCER_OPTIONS,
  InputSyncerModuleOptions,
  InputSyncerServerOptions,
} from './interfaces';
import {
  AdminCreateInstanceRequest,
  AdminInstanceInfo,
  AdminPoolStats,
  ServerInstanceState,
} from './types';
import { validateAdminMatchAccess } from './match-access';
import { validateAdminMatchContext } from './match-context-admin';

/** Only pass fields present on the body so `{}` does not wipe module defaults. */
function overridesFromCreateBody(
  body?: AdminCreateInstanceRequest,
): InputSyncerServerOptions | undefined {
  if (!body) return undefined;
  const o: InputSyncerServerOptions = {};
  if (body.maxPlayers !== undefined) o.maxPlayers = body.maxPlayers;
  if (body.stepIntervalSeconds !== undefined)
    o.stepIntervalSeconds = body.stepIntervalSeconds;
  if (body.autoStartWhenFull !== undefined)
    o.autoStartWhenFull = body.autoStartWhenFull;
  if (body.allowLateJoin !== undefined) o.allowLateJoin = body.allowLateJoin;
  if (body.sendStepHistoryOnLateJoin !== undefined)
    o.sendStepHistoryOnLateJoin = body.sendStepHistoryOnLateJoin;
  if (body.matchAccess !== undefined)
    o.matchAccess = body.matchAccess as 'open' | 'password' | 'token';
  if (body.matchPassword !== undefined) o.matchPassword = body.matchPassword;
  if (body.allowedMatchTokens !== undefined)
    o.allowedMatchTokens = body.allowedMatchTokens;
  if (body.matchData !== undefined) o.matchData = body.matchData;
  if (body.users !== undefined) {
    o.users =
      body.users != null &&
      typeof body.users === 'object' &&
      !Array.isArray(body.users)
        ? { ...(body.users as Record<string, unknown>) }
        : {};
  }
  return Object.keys(o).length > 0 ? o : undefined;
}

@Controller('api')
@UseGuards(BearerAuthGuard)
export class AdminController {
  constructor(
    private readonly pool: InputSyncerPoolService,
    @Inject(INPUT_SYNCER_OPTIONS)
    private readonly moduleOptions: InputSyncerModuleOptions,
  ) {}

  @Post('instances')
  @HttpCode(HttpStatus.CREATED)
  createInstance(
    @Body() body?: AdminCreateInstanceRequest,
  ): AdminInstanceInfo {
    const errors: string[] = [];
    if (body?.maxPlayers !== undefined && body.maxPlayers < 1)
      errors.push('maxPlayers must be >= 1');
    if (
      body?.stepIntervalSeconds !== undefined &&
      body.stepIntervalSeconds <= 0
    )
      errors.push('stepIntervalSeconds must be > 0');

    errors.push(...validateAdminMatchAccess(body));
    errors.push(
      ...validateAdminMatchContext(body, this.pool.requireMatchUserDataOnCreate),
    );

    if (errors.length > 0) {
      throw new HttpException(
        { error: 'Invalid parameters', details: errors },
        HttpStatus.BAD_REQUEST,
      );
    }

    try {
      const instance = this.pool.createInstance(overridesFromCreateBody(body));

      return mapToInfo(instance, this.moduleOptions);
    } catch (err) {
      throw new HttpException(
        { error: (err as Error).message },
        HttpStatus.CONFLICT,
      );
    }
  }

  @Get('instances')
  listInstances(): AdminInstanceInfo[] {
    return this.pool
      .getAllInstances()
      .map((i) => mapToInfo(i, this.moduleOptions));
  }

  @Get('instances/:id')
  getInstance(@Param('id') id: string): AdminInstanceInfo {
    const instance = this.pool.getInstance(id);
    if (!instance) {
      throw new HttpException(
        { error: 'Not found' },
        HttpStatus.NOT_FOUND,
      );
    }
    return mapToInfo(instance, this.moduleOptions);
  }

  @Delete('instances/:id')
  deleteInstance(@Param('id') id: string): { success: boolean } {
    const destroyed = this.pool.destroyInstance(id);
    if (!destroyed) {
      throw new HttpException(
        { error: 'Not found' },
        HttpStatus.NOT_FOUND,
      );
    }
    return { success: true };
  }

  @Get('stats')
  getStats(): AdminPoolStats {
    const all = this.pool.getAllInstances();
    const mem = process.memoryUsage();

    return {
      totalInstances: all.length,
      availableSlots: this.pool.getAvailableSlots(),
      idleCount: all.filter((i) => i.state === ServerInstanceState.Idle)
        .length,
      waitingCount: all.filter(
        (i) => i.state === ServerInstanceState.WaitingForPlayers,
      ).length,
      inMatchCount: all.filter(
        (i) => i.state === ServerInstanceState.InMatch,
      ).length,
      finishedCount: all.filter(
        (i) => i.state === ServerInstanceState.Finished,
      ).length,
      instances: all.map((i) => mapToInfo(i, this.moduleOptions)),
      resourceUsage: {
        heapUsedBytes: mem.heapUsed,
        rssBytes: mem.rss,
        processorCount: os.cpus().length,
      },
    };
  }
}

function mapToInfo(
  instance: ServerInstance,
  moduleOptions: InputSyncerModuleOptions,
): AdminInstanceInfo {
  const opts = instance.server.options;
  const port = parseInt(process.env.INPUT_SYNCER_PORT ?? '3000', 10);
  const base =
    moduleOptions.pool?.publicClientSocketIoUrl?.trim() ||
    `http://localhost:${port}`;
  let host = '';
  try {
    host = new URL(base).hostname;
  } catch {
    /* ignore */
  }
  return {
    id: instance.id,
    state: instance.state,
    playerCount: instance.server.getPlayerCount(),
    joinedPlayerCount: instance.server.getJoinedPlayerCount(),
    matchStarted: instance.server.isMatchStarted,
    matchFinished: instance.server.isMatchFinished,
    createdAt: instance.createdAt.toISOString(),
    currentStep: instance.server.currentStep,
    uptimeSeconds: (Date.now() - instance.createdAt.getTime()) / 1000,
    matchAccess: opts.matchAccess,
    allowedMatchTokenCount: opts.allowedMatchTokens.size,
    serverUrl: base,
    clientConnection: {
      transport: 'socket.io',
      matchId: instance.id,
      host,
      port,
      socketIoUrl: base,
      matchGatewayPath: '/match-gateway',
    },
  };
}
