import { DynamicModule, Module } from '@nestjs/common';
import {
  INPUT_SYNCER_OPTIONS,
  InputSyncerModuleAsyncOptions,
  InputSyncerModuleOptions,
} from './interfaces';
import { InputSyncerPoolService } from './pool.service';
import { MatchGateway } from './match.gateway';
import { AdminController } from './admin.controller';
import { BearerAuthGuard } from './admin.guard';

@Module({})
export class InputSyncerModule {
  static forRoot(options: InputSyncerModuleOptions): DynamicModule {
    return {
      module: InputSyncerModule,
      global: true,
      providers: [
        {
          provide: INPUT_SYNCER_OPTIONS,
          useValue: options,
        },
        InputSyncerPoolService,
        MatchGateway,
        BearerAuthGuard,
      ],
      controllers: [AdminController],
      exports: [InputSyncerPoolService, INPUT_SYNCER_OPTIONS],
    };
  }

  static forRootAsync(options: InputSyncerModuleAsyncOptions): DynamicModule {
    return {
      module: InputSyncerModule,
      global: true,
      imports: options.imports ?? [],
      providers: [
        {
          provide: INPUT_SYNCER_OPTIONS,
          useFactory: options.useFactory,
          inject: options.inject ?? [],
        },
        InputSyncerPoolService,
        MatchGateway,
        BearerAuthGuard,
      ],
      controllers: [AdminController],
      exports: [InputSyncerPoolService, INPUT_SYNCER_OPTIONS],
    };
  }
}
