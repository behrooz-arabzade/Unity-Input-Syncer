import { compactForUpload } from '../src/nakama/input-log-compaction';
import type { StepInputs } from '../src/input-syncer/types';

/**
 * The history a lockstep match produces: two clients publishing a state hash every step, and a
 * pick from each only when a turn asks for one.
 */
function history(steps: number, pickSteps: number[] = []): StepInputs[] {
  const out: StepInputs[] = [];
  for (let step = 0; step < steps; step++) {
    const inputs: Record<string, unknown>[] = [
      { type: 'state-hash', userId: 'home', data: { step, hash: `h${step}` } },
      { type: 'state-hash', userId: 'away', data: { step, hash: `a${step}` } },
    ];
    if (pickSteps.includes(step)) {
      inputs.push({ type: 'duel-pick', userId: 'home', data: { turnStep: step, action: 3 } });
      inputs.push({ type: 'duel-pick', userId: 'away', data: { turnStep: step, action: 4 } });
    }
    out.push({ step, inputs });
  }
  return out;
}

/** The envelope as the server really stores it — the message plus its own handling state. */
function envelope(type: string, userId: string, data: unknown): Record<string, unknown> {
  return {
    type,
    data,
    userId,
    index: 0,
    requestStep: 0,
    expectedCastTimeMs: 0,
    remainingCastTimeMs: 0,
    forceCast: false,
    castCanceled: false,
    payload: null,
    forceUserId: null,
  };
}

const KEEP_LAST = ['state-hash'];
const FIELDS = ['type', 'userId', 'data'];

describe('input log compaction for upload', () => {
  it('keeps every input and only the last repetitive entry per user', () => {
    const result = compactForUpload(history(6, [0, 3]), KEEP_LAST);
    const flat = result.steps.flatMap((s) => s.inputs);

    expect(flat.filter((i: any) => i.type === 'duel-pick')).toHaveLength(4);

    const hashes = flat.filter((i: any) => i.type === 'state-hash');
    expect(hashes).toHaveLength(2);
    expect(hashes.map((i: any) => i.data.hash).sort()).toEqual(['a5', 'h5']);
    expect(result.thinned).toBe(10);
  });

  it('leaves a kept entry at the step it was published on', () => {
    // The claim carries the step it is *about*; moving it would testify about a state nobody
    // hashed, and a receiver compares the two sides' last claims by that number.
    const result = compactForUpload(history(6, [0]), KEEP_LAST);
    const last = result.steps[result.steps.length - 1];
    expect(last.step).toBe(5);
    for (const input of last.inputs as any[]) {
      expect(input.data.step).toBe(5);
    }
    expect(result.steps[0].step).toBe(0);
    expect((result.steps[0].inputs as any[]).every((i) => i.type === 'duel-pick')).toBe(true);
  });

  it('keeps the highest step even when nothing survives on it', () => {
    // A receiver reads how long the match ran off the largest step in the history.
    const raw: StepInputs[] = [
      { step: 0, inputs: [{ type: 'duel-pick', userId: 'home', data: {} }] },
      { step: 9, inputs: [{ type: 'presence', userId: 'home', data: {} }] },
      { step: 40, inputs: [{ type: 'state-hash', userId: 'home', data: { step: 40 } }] },
      { step: 41, inputs: [] },
    ];
    const result = compactForUpload(raw, ['state-hash', 'presence']);
    expect(result.steps[result.steps.length - 1].step).toBe(41);
    expect(result.steps[result.steps.length - 1].inputs).toEqual([]);
  });

  it('drops the steps that emptied, and says how many', () => {
    const result = compactForUpload(history(10), KEEP_LAST);
    // Only step 9 survives, carrying both last claims.
    expect(result.steps).toHaveLength(1);
    expect(result.steps[0].step).toBe(9);
    expect(result.droppedSteps).toBe(9);
  });

  it('is what takes a real match under the request cap', () => {
    // 2,097 steps is a 3.5-minute match, and it was refused at ~1.4 MB.
    const raw = history(2097, [10, 130, 250, 370, 490]);
    const before = JSON.stringify(raw).length;
    const after = JSON.stringify(compactForUpload(raw, KEEP_LAST).steps).length;

    expect(before).toBeGreaterThan(262144);
    expect(after).toBeLessThan(4096);
  });

  describe('projecting the envelope', () => {
    it('keeps the message and drops this server\'s handling of it', () => {
      const raw: StepInputs[] = [
        { step: 0, inputs: [envelope('duel-pick', 'home', { turnStep: 0, action: 3 })] },
      ];
      const [input] = compactForUpload(raw, KEEP_LAST, FIELDS).steps[0].inputs as any[];
      expect(Object.keys(input).sort()).toEqual(['data', 'type', 'userId']);
      expect(input.data).toEqual({ turnStep: 0, action: 3 });
    });

    it('sends whole envelopes when no fields are named', () => {
      const raw: StepInputs[] = [{ step: 0, inputs: [envelope('duel-pick', 'home', {})] }];
      const [input] = compactForUpload(raw, KEEP_LAST, []).steps[0].inputs as any[];
      expect(Object.keys(input)).toContain('remainingCastTimeMs');
    });

    it('projects the entries it kept last, too', () => {
      const raw: StepInputs[] = [
        { step: 0, inputs: [envelope('state-hash', 'home', { step: 0, hash: 'h0' })] },
        { step: 1, inputs: [envelope('state-hash', 'home', { step: 1, hash: 'h1' })] },
      ];
      const [input] = compactForUpload(raw, KEEP_LAST, FIELDS).steps[0].inputs as any[];
      expect(Object.keys(input).sort()).toEqual(['data', 'type', 'userId']);
      expect(input.data.hash).toBe('h1');
    });

    it('a field an entry does not have is simply absent', () => {
      const raw: StepInputs[] = [{ step: 0, inputs: [{ type: 'duel-pick' }] }];
      const [input] = compactForUpload(raw, KEEP_LAST, FIELDS).steps[0].inputs as any[];
      expect(input).toEqual({ type: 'duel-pick' });
    });
  });

  it('leaves the deliberate storage limit as the one that binds', () => {
    // Two bots pick every step, which is the densest history this ever sees — a human match is
    // two picks a turn. Measured against a real match: an envelope costs 297 bytes and a
    // *stored* pick 49, so the receiver's own 65,536-byte storage policy allows ~1,325 picks.
    // Uploading whole envelopes put the transport wall at ~880, *below* that policy; projected,
    // it is far above it, so what refuses an over-long match is the choice rather than the pipe.
    const steps = 660;
    const raw: StepInputs[] = [];
    for (let step = 0; step < steps; step++) {
      raw.push({
        step,
        inputs: [
          envelope('state-hash', '38a0ace7-c905-4f9a-bdef-8aa12bd7831c', { step, hash: '11310498870221196471' }),
          envelope('state-hash', '0a20e379-fa31-4b04-92ea-9a852d555320', { step, hash: '11310498870221196471' }),
          envelope('duel-pick', '38a0ace7-c905-4f9a-bdef-8aa12bd7831c', { turnStep: step, action: 3 }),
          envelope('duel-pick', '0a20e379-fa31-4b04-92ea-9a852d555320', { turnStep: step, action: 4 }),
        ],
      });
    }

    const whole = JSON.stringify(compactForUpload(raw, KEEP_LAST, []).steps).length;
    const projected = JSON.stringify(compactForUpload(raw, KEEP_LAST, FIELDS).steps).length;

    expect(whole).toBeGreaterThan(262144);
    expect(projected).toBeLessThan(262144);
  });

  it('changes nothing when no types are named', () => {
    const raw = history(4, [1]);
    const result = compactForUpload(raw, []);
    expect(result.steps).toEqual(raw);
    expect(result.thinned).toBe(0);
  });

  it('does not mutate what it was given', () => {
    const raw = history(4, [1]);
    const copy = JSON.parse(JSON.stringify(raw));
    compactForUpload(raw, KEEP_LAST);
    expect(raw).toEqual(copy);
  });

  it('tolerates an empty history and junk entries', () => {
    expect(compactForUpload([], KEEP_LAST).steps).toEqual([]);
    const junk: StepInputs[] = [
      { step: 0, inputs: [null as any, 'nonsense' as any, { userId: 'home' }] },
    ];
    // Nothing is typed, so nothing is thinned and nothing is lost.
    expect(compactForUpload(junk, KEEP_LAST).steps[0].inputs).toHaveLength(3);
  });

  it('thins per user, so one side going quiet does not take the other with it', () => {
    // A client that froze at step 3 keeps the claim it froze on; the one that played on keeps
    // its own, later one. An invented claim for the step the other side reached would be a lie.
    const raw: StepInputs[] = [];
    for (let step = 0; step < 8; step++) {
      const inputs: Record<string, unknown>[] = [
        { type: 'state-hash', userId: 'home', data: { step, hash: `h${step}` } },
      ];
      if (step <= 3) inputs.push({ type: 'state-hash', userId: 'away', data: { step, hash: `a${step}` } });
      raw.push({ step, inputs });
    }

    const flat = compactForUpload(raw, KEEP_LAST).steps.flatMap((s) => s.inputs) as any[];
    expect(flat).toHaveLength(2);
    expect(flat.find((i) => i.userId === 'away').data.step).toBe(3);
    expect(flat.find((i) => i.userId === 'home').data.step).toBe(7);
  });
});
