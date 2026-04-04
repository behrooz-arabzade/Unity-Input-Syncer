import { Controller, Get, Inject, Param, UseGuards } from '@nestjs/common';
import {
  INPUT_SYNCER_OPTIONS,
  InputSyncerModuleOptions,
} from './interfaces';
import { InputSyncerPoolService } from './pool.service';
import { InternalSecretGuard } from './internal.guard';

@Controller('api/internal')
@UseGuards(InternalSecretGuard)
export class InternalController {
  constructor(
    private readonly pool: InputSyncerPoolService,
    @Inject(INPUT_SYNCER_OPTIONS)
    private readonly moduleOptions: InputSyncerModuleOptions,
  ) {}

  @Get('pool-meta')
  poolMeta(): {
    instanceCount: number;
    availableSlots: number;
    maxInstances: number;
  } {
    return {
      instanceCount: this.pool.getInstanceCount(),
      availableSlots: this.pool.getAvailableSlots(),
      maxInstances: this.moduleOptions.pool?.maxInstances ?? 10,
    };
  }

  @Get('instance/:id/exists')
  instanceExists(@Param('id') id: string): { exists: boolean } {
    return { exists: this.pool.getInstance(id) !== undefined };
  }
}
