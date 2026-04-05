import {
  CanActivate,
  ExecutionContext,
  Inject,
  Injectable,
  UnauthorizedException,
} from '@nestjs/common';
import {
  INPUT_SYNCER_OPTIONS,
  InputSyncerModuleOptions,
} from './interfaces';

@Injectable()
export class BearerAuthGuard implements CanActivate {
  private readonly authToken: string;

  constructor(
    @Inject(INPUT_SYNCER_OPTIONS)
    options: InputSyncerModuleOptions,
  ) {
    this.authToken = options.admin?.authToken ?? '';
  }

  canActivate(context: ExecutionContext): boolean {
    if (!this.authToken) return true;

    const request = context.switchToHttp().getRequest();
    const authHeader: string | undefined = request.headers?.authorization;

    if (!authHeader) throw new UnauthorizedException();

    if (!authHeader.startsWith('Bearer '))
      throw new UnauthorizedException();

    const token = authHeader.slice('Bearer '.length).trim();
    if (token !== this.authToken) throw new UnauthorizedException();

    return true;
  }
}
