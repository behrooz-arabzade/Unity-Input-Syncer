import { InputSyncerServer } from '../src/input-syncer/input-syncer-server';
import { InputSyncerServerOptions } from '../src/input-syncer/interfaces';

/**
 * E08/S09. `abandon_timeout` had never fired in any configuration a deployment would run,
 * because the abandon deadline is armed off a seat count and an abandoned player still
 * filled a seat. The wire-level proof is in `instance-options.integration.test.ts`; these
 * are the cases that are awkward to reach through two real sockets — in particular the one
 * where *both* players go, which leaves nobody connected to be told the match ended.
 *
 * `InputSyncerServer` is a plain class, so a match is two `addPlayer`/`handleJoin` pairs
 * and a `sendToSocket` that goes nowhere.
 */
function startedMatch(opts?: InputSyncerServerOptions): {
  server: InputSyncerServer;
  reasons: string[];
} {
  const server = new InputSyncerServer({
    maxPlayers: 2,
    autoStartWhenFull: true,
    allowLateJoin: true,
    stepIntervalSeconds: 60, // no step ever fires; the tests drive the clock themselves
    disconnectAbandonTimeoutSeconds: 60,
    abandonMatchTimeoutSeconds: 120,
    ...opts,
  });

  const reasons: string[] = [];
  server.onMatchFinishedWithReason = (reason) => reasons.push(reason);

  server.addPlayer('sock-a');
  server.addPlayer('sock-b');
  server.handleJoin('sock-a', { userId: 'u1' });
  server.handleJoin('sock-b', { userId: 'u2' });

  expect(server.isMatchStarted).toBe(true);
  return { server, reasons };
}

/**
 * The disconnect window is a real `setTimeout` inside the server. Rather than wait it out,
 * these tests use a window of 0 s… which takes the *removal* path, not the abandon one.
 * So instead: disconnect with a window, then fire the pending timer by hand through the
 * only door there is — jest's fake timers.
 */
function abandonByTimeout(server: InputSyncerServer, socketId: string): void {
  server.handlePlayerDisconnect(socketId);
  jest.advanceTimersByTime(60_000);
}

describe('the abandon deadline is armed when a seat is abandoned', () => {
  beforeEach(() => jest.useFakeTimers());
  afterEach(() => jest.useRealTimers());

  it('frees the seat: an abandoned player does not count as active', () => {
    const { server } = startedMatch();
    expect(server.getActivePlayerCount()).toBe(2);

    server.handlePlayerDisconnect('sock-a');
    // Inside the window the player is disconnected, not abandoned — they may still come
    // back, so the seat is still theirs and the deadline must stay disarmed.
    expect(server.getActivePlayerCount()).toBe(2);

    jest.advanceTimersByTime(60_000);
    expect(server.getActivePlayerCount()).toBe(1);
    // …and the seat record itself survives, because reconnect looks players up by userId.
    expect(server.getJoinedPlayerCount()).toBe(2);
  });

  it('ends the match once the abandon deadline passes', () => {
    const { server, reasons } = startedMatch();
    abandonByTimeout(server, 'sock-a');
    expect(reasons).toEqual([]);

    jest.advanceTimersByTime(120_001);
    server.handleRequestAllSteps('sock-b'); // any call is fine; the check runs on the step
    jest.advanceTimersByTime(60_000); // one step interval — the deadline is read there
    expect(reasons).toEqual(['abandon_timeout']);
  });

  it('a second abandon ends the match instead of disarming the deadline', () => {
    // The regression this pair of counters could have introduced. Arming off an active-seat
    // count means the second abandon takes it to zero, and `joined > 0 && joined < max` is
    // false at zero — so an armed deadline would have been quietly *disarmed* and the match
    // would have run to `max_instance_lifetime` after all, which is where it started.
    const { server, reasons } = startedMatch();
    abandonByTimeout(server, 'sock-a');
    abandonByTimeout(server, 'sock-b');

    expect(server.getActivePlayerCount()).toBe(0);
    expect(reasons).toEqual(['all_disconnected']);
  });

  it('a manual abandon arms it too', () => {
    const { server, reasons } = startedMatch();
    server.handleManualAbandon('sock-a');
    expect(server.getActivePlayerCount()).toBe(1);

    jest.advanceTimersByTime(120_001);
    jest.advanceTimersByTime(60_000);
    expect(reasons).toEqual(['abandon_timeout']);
  });

  it('without late join, an abandon is insufficient_players at once', () => {
    // The abandon deadline is a grace period for a seat to be *refilled*, which only means
    // anything when late join is on — `updateAbandonDeadline` returns early without it. So
    // the rule is the one `removePlayer` already followed: too few players, end it now.
    const { server, reasons } = startedMatch({ allowLateJoin: false });
    abandonByTimeout(server, 'sock-a');
    expect(reasons).toEqual(['insufficient_players']);
  });

  it('a player who returns inside the window is never abandoned', () => {
    const { server, reasons } = startedMatch();
    server.handlePlayerDisconnect('sock-a');
    jest.advanceTimersByTime(30_000);
    server.handlePlayerReconnect('sock-a', 'sock-a2', 'u1');

    jest.advanceTimersByTime(300_000);
    expect(server.getActivePlayerCount()).toBe(2);
    expect(reasons).toEqual([]);
  });

  it('a player who returns after the abandon marker is not let back in', () => {
    // Deliberate, and the answer to E08/S09's third acceptance criterion. An abandon is not
    // a flag — it is an `abandon` entry pushed into the step stream that both clients have
    // already simulated, and `onPlayerAbandoned` has already told Nakama through
    // `fv_match_abandon_report`. Reversing it would need an un-abandon marker, a client
    // rule for applying one, and a retraction to Nakama. The deadline stays armed and the
    // match ends; the returning player reaches a finished match, which is the honest state.
    const { server } = startedMatch();
    abandonByTimeout(server, 'sock-a');

    server.handlePlayerReconnect('sock-a', 'sock-a2', 'u1');
    expect(server.findDisconnectedPlayerByUserId('u1')).toBeUndefined();
    expect(server.getActivePlayerCount()).toBe(1);
  });
});
