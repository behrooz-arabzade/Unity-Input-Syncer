export class InputSyncerPlayer {
  socketId: string;
  userId: string;
  joined: boolean;
  finished: boolean;
  sessionFinished: boolean;
  disconnected: boolean;
  abandoned: boolean;

  constructor(socketId: string) {
    this.socketId = socketId;
    this.userId = '';
    this.joined = false;
    this.finished = false;
    this.sessionFinished = false;
    this.disconnected = false;
    this.abandoned = false;
  }
}
