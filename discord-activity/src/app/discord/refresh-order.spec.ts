import { describe, expect, it } from 'vitest';
import { ResponseOrder, RunOnceMore } from './refresh-order';

// A promise the test resolves by hand, so it can decide which "server reply" arrives first.
function deferred(): { promise: Promise<void>; resolve: () => void; reject: (e: unknown) => void } {
  let resolve!: () => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<void>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

// Let every pending .then/.finally callback run.
async function flush(): Promise<void> {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

describe('ResponseOrder', () => {
  it('shows replies that come back in order', () => {
    const order = new ResponseOrder();
    const first = order.begin();
    const second = order.begin();
    expect(order.accept(first)).toBe(true);
    expect(order.accept(second)).toBe(true);
  });

  it('drops an older reply that arrives after a newer one', () => {
    const order = new ResponseOrder();
    const older = order.begin();
    const newer = order.begin();
    expect(order.accept(newer)).toBe(true);
    expect(order.accept(older)).toBe(false);
  });

  it('drops a reply for a linkshell the user has switched away from', () => {
    const order = new ResponseOrder();
    const forOld = order.begin(1);
    const forNew = order.begin(2);
    // The old linkshell's reply wins the race, but it is not what the screen now shows.
    expect(order.accept(forOld, 1)).toBe(false);
    expect(order.accept(forNew, 2)).toBe(true);
  });

  it('drops replies already on the wire after the data was replaced locally', () => {
    const order = new ResponseOrder();
    const inFlight = order.begin();
    order.invalidate();
    expect(order.accept(inFlight)).toBe(false);
    // A request sent afterwards is shown as normal.
    expect(order.accept(order.begin())).toBe(true);
  });
});

describe('RunOnceMore', () => {
  it('runs straight away when nothing is running', async () => {
    let runs = 0;
    const runner = new RunOnceMore(async () => { runs++; });
    await runner.run();
    expect(runs).toBe(1);
  });

  it('runs once more after the current run when asked mid-run, instead of joining it', async () => {
    const gates: ReturnType<typeof deferred>[] = [];
    const runner = new RunOnceMore(() => {
      const gate = deferred();
      gates.push(gate);
      return gate.promise;
    });

    const first = runner.run();
    const second = runner.run();
    await flush();
    // Only one run at a time: the second has not started yet.
    expect(gates.length).toBe(1);

    gates[0].resolve();
    await first;
    await flush();
    // ...and it starts as soon as the first finishes.
    expect(gates.length).toBe(2);

    let secondDone = false;
    void second.then(() => { secondDone = true; });
    await flush();
    // The second caller waits for ITS run, not the first one.
    expect(secondDone).toBe(false);
    gates[1].resolve();
    await second;
    expect(secondDone).toBe(true);
  });

  it('folds a burst of requests during one run into a single extra run', async () => {
    const gates: ReturnType<typeof deferred>[] = [];
    const runner = new RunOnceMore(() => {
      const gate = deferred();
      gates.push(gate);
      return gate.promise;
    });

    const first = runner.run();
    const burst = [runner.run(), runner.run(), runner.run()];
    gates[0].resolve();
    await first;
    await flush();
    expect(gates.length).toBe(2);
    gates[1].resolve();
    await Promise.all(burst);
    expect(gates.length).toBe(2);
  });

  it('still runs once more when the current run fails', async () => {
    const gates: ReturnType<typeof deferred>[] = [];
    const runner = new RunOnceMore(() => {
      const gate = deferred();
      gates.push(gate);
      return gate.promise;
    });

    const first = runner.run();
    const second = runner.run();
    gates[0].reject(new Error('network'));
    await expect(first).rejects.toThrow('network');
    await flush();
    expect(gates.length).toBe(2);
    gates[1].resolve();
    await expect(second).resolves.toBeUndefined();
  });

  it('starts fresh after everything has finished', async () => {
    let runs = 0;
    const runner = new RunOnceMore(async () => { runs++; });
    await runner.run();
    await runner.run();
    expect(runs).toBe(2);
  });
});
