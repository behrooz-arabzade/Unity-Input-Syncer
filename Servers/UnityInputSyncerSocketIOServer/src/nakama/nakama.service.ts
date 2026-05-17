import { Injectable, Logger } from "@nestjs/common";

@Injectable()
export class NakamaService {
  private readonly logger = new Logger(NakamaService.name);
  private readonly httpUrl: string;
  private readonly serverKey: string;

  constructor() {
    this.httpUrl = (process.env.NAKAMA_HTTP_URL || "http://localhost:7350").replace(/\/$/, "");
    this.serverKey = process.env.NAKAMA_SERVER_KEY || "defaultkey";
  }

  async callRpc<T = unknown>(rpcId: string, payload: object): Promise<T> {
    const url = `${this.httpUrl}/v2/rpc/${rpcId}?http_key=${encodeURIComponent(this.serverKey)}`;
    const body = JSON.stringify(payload);

    const response = await fetch(url, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "application/json",
      },
      body,
    });

    if (!response.ok) {
      const errBody = await response.text().catch(() => "");
      this.logger.error(`Nakama RPC ${rpcId} failed: ${response.status} ${errBody}`);
      throw new Error(`Nakama RPC ${rpcId} failed with status ${response.status}: ${errBody}`);
    }

    const json = await response.json();
    if (json.payload) {
      return typeof json.payload === "string" ? JSON.parse(json.payload) : json.payload;
    }
    return json as T;
  }

  isConfigured(): boolean {
    return this.httpUrl.length > 0 && this.serverKey.length > 0;
  }
}
