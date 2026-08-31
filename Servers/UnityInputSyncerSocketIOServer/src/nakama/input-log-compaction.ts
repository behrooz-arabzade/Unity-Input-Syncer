import type { StepInputs } from "../input-syncer/types";

/**
 * What of a match's step history is worth *sending* — and why almost none of it is.
 *
 * ## The measurement this file exists because of
 *
 * The bridge uploads the whole step history on every match finish, and Nakama's HTTP gateway
 * caps a request body at **262,144 bytes** (`socket.max_request_size_bytes`, the shipped
 * default). Measured against a real stack on 2026-08-31, a lockstep client publishing one
 * in-band `state-hash` claim per step costs roughly **700 bytes a step**, so:
 *
 * | Steps | Match length at 0.1 s | Body | Result |
 * |---|---|---|---|
 * | 262 | 26 s | ~180 KB | rejected on a real match |
 * | 2,097 | 3.5 min | ~1.4 MB | rejected |
 * | 18,000 | the 30-minute lifetime cap | ~12.6 MB | rejected |
 *
 * The upload is fire-and-forget, so **the rejection was silent**: the relay logged a 400 and
 * moved on, and no match's log ever reached Nakama. Raising the cap is not the fix — the body
 * grows with the *clock*, so any cap is a wall a longer match eventually hits, and it would hit
 * it the same silent way.
 *
 * ## What is dropped, and why it is safe
 *
 * Nothing is dropped that a replay needs. The history is the game's own inputs braided together
 * with whatever high-volume, **recomputable** chatter the game publishes in-band — for a
 * lockstep simulation that is a per-step state hash, which is a pure function of the inputs
 * beside it. So for each type named in `keepLastTypes`, only the **last entry per user**
 * survives: enough for a receiver to compare what the two sides finally claimed, none of the
 * stream that can be replayed back. What is left scales with *inputs* — turns, not steps — so
 * a thirty-minute match is tens of kilobytes rather than megabytes, and the cap stops being a
 * wall at all.
 *
 * **The relay never looks inside `data`.** It reads `type` and `userId`, which are its own
 * envelope fields, and the types to thin are configuration — this module knows that some
 * message kinds are repetitive, never what any of them mean.
 *
 * ## And it sends the message, not its own handling of it
 *
 * An envelope is mostly the relay's per-message bookkeeping — `index`, `requestStep`, the cast
 * timers, the force/cancel flags — which describes **how this server handled a message**, not
 * what a player did. Measured on a real match, that is 200 of an envelope's 297 bytes. An
 * upload for replay reconstruction wants the message as its sender sent it, so `uploadFields`
 * projects each entry down to those fields (`type`, `userId`, `data` by default) and drops the
 * handling state. It is the difference between the transport cap binding at ~880 inputs and at
 * ~2,600 — which matters because the receiver has a deliberate storage policy of its own, and
 * **the deliberate limit should be the one that binds**, not an accident of envelope width.
 *
 * ## Two invariants the receiver depends on
 *
 * - **A kept entry stays at its original step.** A last claim carries the step it is about,
 *   and moving it would be this compaction testifying about a state nobody hashed.
 * - **The highest step survives even when it is emptied.** A receiver reads "how long did the
 *   match run" off the largest step in the history, so the final step is re-inserted with no
 *   inputs rather than dropped with the rest of the empties.
 */

export interface CompactionOutcome {
  steps: StepInputs[];
  /** Entries removed because a later one from the same user superseded them. */
  thinned: number;
  /** Entries carried through untouched, plus the survivors put back. */
  kept: number;
  /** Steps that had nothing left and were not the final one. */
  droppedSteps: number;
}

interface Envelope {
  type?: unknown;
  userId?: unknown;
}

function isObject(value: unknown): boolean {
  return !!value && typeof value === "object" && !Array.isArray(value);
}

/**
 * Thins the history for upload. Pure; the input is not mutated.
 *
 * @param history steps in any order — the result is sorted by step ascending.
 * @param keepLastTypes envelope `type` values to keep only the last of, per user. Empty means
 *   keep everything, which is the pre-2026-08-31 behaviour and is what a body too large looks
 *   like.
 * @param uploadFields envelope fields to carry. Empty means the whole envelope, bookkeeping
 *   included. A field an entry does not have is simply absent from the projection.
 */
export function compactForUpload(
  history: StepInputs[],
  keepLastTypes: string[],
  uploadFields: string[] = [],
): CompactionOutcome {
  const thin: { [type: string]: boolean } = {};
  for (const type of keepLastTypes) if (type) thin[type] = true;

  const ordered = [...history].sort((a, b) => a.step - b.step);
  const highestStep = ordered.length > 0 ? ordered[ordered.length - 1].step : -1;

  // Walked in step order, so the last write per key is the latest entry that user published.
  const latest: { [key: string]: { step: number; input: Record<string, unknown> } } = {};
  const keptByStep: { [step: number]: Record<string, unknown>[] } = {};
  let thinned = 0;
  let kept = 0;

  for (const entry of ordered) {
    const inputs = Array.isArray(entry.inputs) ? entry.inputs : [];
    for (const input of inputs) {
      const envelope = input as Envelope;
      const type = isObject(input) && typeof envelope.type === "string" ? envelope.type : "";
      if (type && thin[type]) {
        const userId = typeof envelope.userId === "string" ? envelope.userId : "";
        const key = type + " " + userId;
        if (latest[key]) thinned++;
        latest[key] = { step: entry.step, input };
        continue;
      }
      if (!keptByStep[entry.step]) keptByStep[entry.step] = [];
      keptByStep[entry.step].push(input);
      kept++;
    }
  }

  // Put each survivor back where it was published, beside whatever else that step kept.
  for (const key of Object.keys(latest)) {
    const survivor = latest[key];
    if (!keptByStep[survivor.step]) keptByStep[survivor.step] = [];
    keptByStep[survivor.step].push(survivor.input);
    kept++;
  }

  // The final step is what a receiver reads the match's length off, so it survives empty.
  if (highestStep >= 0 && !keptByStep[highestStep]) keptByStep[highestStep] = [];

  let droppedSteps = 0;
  for (const entry of ordered) {
    if (!keptByStep[entry.step]) droppedSteps++;
  }

  const steps: StepInputs[] = [];
  for (const key of Object.keys(keptByStep)) {
    const step = Number(key);
    steps.push({ step, inputs: keptByStep[step].map((input) => project(input, uploadFields)) });
  }
  steps.sort((a, b) => a.step - b.step);

  return { steps, thinned, kept, droppedSteps };
}

/** One entry with only the named fields. An unnamed list projects nothing away. */
function project(
  input: Record<string, unknown>,
  fields: string[],
): Record<string, unknown> {
  if (fields.length === 0 || !isObject(input)) return input;
  const out: Record<string, unknown> = {};
  for (const field of fields) {
    if (Object.prototype.hasOwnProperty.call(input, field)) out[field] = input[field];
  }
  return out;
}
