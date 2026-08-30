import {
  CanActivate,
  ExecutionContext,
  Inject,
  Injectable,
  Logger,
  UnauthorizedException,
} from '@nestjs/common';
import {
  INPUT_SYNCER_OPTIONS,
  InputSyncerModuleOptions,
} from './interfaces';
import { ADMIN_AUTH_DISABLED_ENV, ADMIN_AUTH_TOKEN_ENV } from './admin-auth';

@Injectable()
export class BearerAuthGuard implements CanActivate {
  private readonly logger = new Logger(BearerAuthGuard.name);
  private readonly authToken: string;
  private readonly authDisabled: boolean;
  private warned = false;

  constructor(
    @Inject(INPUT_SYNCER_OPTIONS)
    options: InputSyncerModuleOptions,
  ) {
    this.authToken = options.admin?.authToken ?? '';
    this.authDisabled = options.admin?.authDisabled ?? false;
  }

  canActivate(context: ExecutionContext): boolean {
    // The opt-out, and the only way through without a token. `main.ts` refuses to start
    // without one of the two, so reaching here with neither means the guard is being used
    // outside that entry point — a test, or an embedding app. Refuse, and say why once.
    if (this.authDisabled) {
      this.warnOnce(
        `${ADMIN_AUTH_DISABLED_ENV} is set: the admin API is unauthenticated.`,
      );
      return true;
    }

    if (!this.authToken) {
      this.warnOnce(
        `Refusing admin requests: ${ADMIN_AUTH_TOKEN_ENV} is not configured. ` +
          `Set it, or set ${ADMIN_AUTH_DISABLED_ENV}=1 to allow an open admin API.`,
      );
      throw new UnauthorizedException();
    }

    const request = context.switchToHttp().getRequest();
    const authHeader: string | undefined = request.headers?.authorization;

    if (!authHeader) throw new UnauthorizedException();

    if (!authHeader.startsWith('Bearer '))
      throw new UnauthorizedException();

    const token = authHeader.slice('Bearer '.length).trim();
    if (token !== this.authToken) throw new UnauthorizedException();

    return true;
  }

  private warnOnce(message: string): void {
    if (this.warned) return;
    this.warned = true;
    this.logger.warn(message);
  }
}
