export class InputSyncerPlayer {
  socketId: string;
  userId: string;
  joined: boolean;
  finished: boolean;
  sessionFinished: boolean;

  constructor(socketId: string) {
    this.socketId = socketId;
    this.userId = '';
    this.joined = false;
    this.finished = false;
    this.sessionFinished = false;
  }
}
