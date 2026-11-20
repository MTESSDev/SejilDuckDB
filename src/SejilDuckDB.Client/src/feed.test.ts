import { describe, expect, it } from 'vitest';
import { MergedFeed, isNewer, newest } from './feed';
import type { FeedSource } from './feed';
import type { Cursor, LogEntry, NodePage } from './types';

let counter = 0;

/** Un événement : `utc` = « 2026-10-07T10:00:05 » (les secondes suffisent), numéro de réception croissant. */
function event(node: string, utc: string, seq = ++counter): LogEntry {
  return {
    id: `${node}:${seq}`,
    seq: String(seq),
    node,
    utc: `${utc}.000000`,
    timestamp: utc,
    message: `${node} ${utc}`,
    sourceApp: 'App',
    level: 'Information',
    exception: null,
    properties: [],
  };
}

/** Un serveur qui détient ces événements (déjà du plus récent au plus ancien) et les rend par pages de `size`. */
function server(node: string, events: LogEntry[], size = 100, calls: Array<Cursor | null> = []): FeedSource {
  return {
    label: node,
    async fetch(cursor) {
      calls.push(cursor);
      const rest = cursor ? events.filter(e => e.utc < cursor.utc || (e.utc === cursor.utc && BigInt(e.seq) < BigInt(cursor.id))) : events;
      const page = rest.slice(0, size);
      const last = page[page.length - 1];
      const more = rest.length > page.length;
      return { node, events: page, resume: more ? { utc: last.utc, id: last.seq } : null, exhausted: !more, partial: false, scannedTo: '2026-10-07' };
    },
  };
}

const messages = (events: LogEntry[]) => events.map(e => e.message);

describe('fusion de plusieurs serveurs', () => {
  it('rend les événements des deux serveurs du plus récent au plus ancien', async () => {
    const a = server('A', [event('A', '2026-10-07T10:00:05'), event('A', '2026-10-07T10:00:01')]);
    const b = server('B', [event('B', '2026-10-07T10:00:04'), event('B', '2026-10-07T10:00:03'), event('B', '2026-10-07T10:00:00')]);

    const feed = new MergedFeed([a, b], 100);
    const page = await feed.next();

    expect(messages(page)).toEqual([
      'A 2026-10-07T10:00:05',
      'B 2026-10-07T10:00:04',
      'B 2026-10-07T10:00:03',
      'A 2026-10-07T10:00:01',
      'B 2026-10-07T10:00:00',
    ]);
    expect(feed.done).toBe(true);
  });

  it('découpe en pages sans trou ni doublon, même quand un serveur a de longues séries d’affilée', async () => {
    const a = server('A', Array.from({ length: 10 }, (_, i) => event('A', `2026-10-07T10:00:${String(59 - i).padStart(2, '0')}`)), 3);
    const b = server('B', Array.from({ length: 4 }, (_, i) => event('B', `2026-10-07T10:00:${String(30 - i).padStart(2, '0')}`)), 3);

    const feed = new MergedFeed([a, b], 4);
    const all: LogEntry[] = [];
    for (let guard = 0; guard < 10 && !feed.done; guard++) all.push(...(await feed.next()));

    expect(all).toHaveLength(14);
    expect(new Set(all.map(e => e.id)).size).toBe(14);
    const times = all.map(e => e.utc);
    expect(times).toEqual([...times].sort().reverse());
  });

  it('ne demande la suite d’un serveur que quand sa réserve est vide', async () => {
    const callsA: Array<Cursor | null> = [];
    const callsB: Array<Cursor | null> = [];
    const a = server('A', Array.from({ length: 6 }, (_, i) => event('A', `2026-10-07T10:00:${String(59 - i).padStart(2, '0')}`)), 3, callsA);
    const b = server('B', Array.from({ length: 6 }, (_, i) => event('B', `2026-10-07T10:00:${String(20 - i).padStart(2, '0')}`)), 3, callsB);

    const feed = new MergedFeed([a, b], 2);
    await feed.next(); // deux événements de A : B n'est lu qu'une fois, et A pas plus loin que sa première page

    expect(callsA).toHaveLength(1);
    expect(callsB).toHaveLength(1);
  });

  it('départage deux événements de même heure par leur numéro puis par leur serveur, toujours dans le même ordre', () => {
    const a1 = event('A', '2026-10-07T10:00:00', 5);
    const a2 = event('A', '2026-10-07T10:00:00', 9);
    const b1 = event('B', '2026-10-07T10:00:00', 7);

    expect(isNewer(a2, a1)).toBe(true);
    expect(isNewer(a1, a2)).toBe(false);
    expect(isNewer(a1, b1)).toBe(true); // même heure, serveurs différents : A avant B
    expect(isNewer(b1, a1)).toBe(false);
  });

  it('un serveur qui ne répond pas ne bloque pas les autres, et peut être réinterrogé', async () => {
    let down = true;
    const a = server('A', [event('A', '2026-10-07T10:00:05'), event('A', '2026-10-07T10:00:01')]);
    const bEvents = [event('B', '2026-10-07T10:00:04')];
    const b: FeedSource = {
      label: 'B',
      fetch: cursor => (down ? Promise.reject(new Error('B est tombé')) : server('B', bEvents).fetch(cursor)),
    };

    const feed = new MergedFeed([a, b], 100);
    const first = await feed.next();

    expect(messages(first)).toEqual(['A 2026-10-07T10:00:05', 'A 2026-10-07T10:00:01']);
    expect(feed.failures.map(f => f.label)).toEqual(['B']);
    expect(feed.allFailed).toBe(false);
    expect(feed.done).toBe(true); // rien d'autre à lire tant que B est en panne

    down = false;
    feed.retryFailed();
    const second = await feed.next();

    expect(messages(second)).toEqual(['B 2026-10-07T10:00:04']);
    expect(feed.failures).toEqual([]);
  });

  it('sait quand tous les serveurs sont en panne', async () => {
    const dead: FeedSource = { label: 'A', fetch: () => Promise.reject(new Error('non')) };

    const feed = new MergedFeed([dead, { ...dead, label: 'B' }], 100);
    const page = await feed.next();

    expect(page).toEqual([]);
    expect(feed.allFailed).toBe(true);
  });

  it('continue de lui-même quand un serveur rend une réponse partielle, et le dit au fil de l’eau', async () => {
    const found = event('A', '2026-10-01T09:00:00');
    const answers: NodePage[] = [
      { node: 'A', events: [], resume: { utc: '2026-10-06T00:00:00.000000', id: '0' }, exhausted: false, partial: true, scannedTo: '2026-10-06' },
      { node: 'A', events: [], resume: { utc: '2026-10-03T00:00:00.000000', id: '0' }, exhausted: false, partial: true, scannedTo: '2026-10-03' },
      { node: 'A', events: [found], resume: null, exhausted: true, partial: false, scannedTo: '2026-10-01' },
    ];
    const cursors: Array<Cursor | null> = [];
    const a: FeedSource = { label: 'A', fetch: async cursor => (cursors.push(cursor), answers.shift() as NodePage) };
    const progress: Array<string | null> = [];

    const feed = new MergedFeed([a], 100);
    const page = await feed.next(p => progress.push(p.scannedTo));

    expect(page).toEqual([found]);
    expect(progress).toEqual(['2026-10-06', '2026-10-03', '2026-10-01']);
    expect(cursors[1]?.utc).toBe('2026-10-06T00:00:00.000000'); // reprend là où il s'était arrêté
    expect(feed.done).toBe(true);
  });

  it('ne tourne pas en rond si un serveur répond « suite » sans avancer', async () => {
    const stuck: NodePage = { node: 'A', events: [], resume: { utc: '2026-10-06T00:00:00.000000', id: '0' }, exhausted: false, partial: true, scannedTo: '2026-10-06' };
    let calls = 0;
    const a: FeedSource = { label: 'A', fetch: async () => (calls++, stuck) };

    const feed = new MergedFeed([a], 100);
    await feed.next();

    expect(calls).toBeLessThanOrEqual(3);
    expect(feed.done).toBe(true);
  });

  it('si l’en-tête du répartiteur est ignoré (même serveur deux fois), chaque événement n’apparaît qu’une fois et on le signale', async () => {
    const shared = [event('A', '2026-10-07T10:00:05', 1), event('A', '2026-10-07T10:00:01', 2)];

    const feed = new MergedFeed([server('A', shared), { ...server('A', shared), label: 'B' }], 100);
    const page = await feed.next();

    expect(page).toHaveLength(2);
    expect(feed.sameServer).toBe('A');
  });

  it('ne signale rien quand les deux serveurs sont bien distincts', async () => {
    const feed = new MergedFeed([server('A', [event('A', '2026-10-07T10:00:05')]), server('B', [event('B', '2026-10-07T10:00:04')])], 100);
    await feed.next();

    expect(feed.sameServer).toBeNull();
  });

  it('actualisation : les plus récents de chaque serveur, fusionnés, en ignorant un serveur en panne', async () => {
    const a = server('A', [event('A', '2026-10-07T10:00:05'), event('A', '2026-10-07T10:00:01')]);
    const b = server('B', [event('B', '2026-10-07T10:00:04')]);
    const dead: FeedSource = { label: 'C', fetch: () => Promise.reject(new Error('non')) };

    const fresh = await newest([a, dead, b]);

    expect(messages(fresh)).toEqual(['A 2026-10-07T10:00:05', 'B 2026-10-07T10:00:04', 'A 2026-10-07T10:00:01']);
  });

  it('un seul serveur : se comporte comme avant', async () => {
    const feed = new MergedFeed([server('A', [event('A', '2026-10-07T10:00:05')])], 100);

    expect(messages(await feed.next())).toEqual(['A 2026-10-07T10:00:05']);
    expect(feed.done).toBe(true);
    expect(feed.sameServer).toBeNull();
  });
});
