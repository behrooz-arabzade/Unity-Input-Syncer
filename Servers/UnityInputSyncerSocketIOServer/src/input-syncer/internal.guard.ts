import {
  CanActivate,
  ExecutionContext,
  Injectable,
  UnauthorizedException,
} from '@nestjs/common';

const HEADER = 'x-input-syncer-internal';

@Injectable()
export class InternalSecretGuard implements CanActivate {
  canActivate(context: ExecutionContext): boolean {
    const secret = process.env.INPUT_SYNCER_INTERNAL_SECRET;
    if (!secret) {
      throw new UnauthorizedException('Internal API disabled');
    }

    const request = context.switchToHttp().getRequest<{
      headers?: Record<string, string | string[] | undefined>;
    }>();
    const headerVal = request.headers?.[HEADER];
    const token = Array.isArray(headerVal) ? headerVal[0] : headerVal;

    if (token !== secret) {
      throw new UnauthorizedException();
    }

    return true;
  }
}

export const INPUT_SYNCER_INTERNAL_HEADER = HEADER;
