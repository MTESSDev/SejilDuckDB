import type { Cursor, LogEntry, NodePage } from './types';

/** Un serveur que l'on interroge : `fetch(null)` donne ses événements les plus récents, `fetch(curseur)` la suite. */
export interface FeedSource {
  label: string;
  fetch(cursor: Cursor | null): Promise<NodePage>;
}

interface Lane {
  source: FeedSource;
  /** Événements reçus de ce serveur et pas encore montrés, du plus récent au plus ancien. */
  buffer: LogEntry[];
  cursor: Cursor | null;
  exhausted: boolean;
  error: unknown;
  failed: boolean;
  /** Jour le plus ancien lu par la dernière réponse (« aaaa-mm-jj »). */
  scannedTo: string | null;
  /** Nom du serveur qui a réellement répondu (celui que renvoie le serveur, pas celui qu'on visait). */
  answeredBy: string | null;
}

export interface FeedProgress {
  scannedTo: string | null;
}

/** Vrai si `a` est plus récent que `b` (heure UTC, puis numéro de réception sur le serveur, puis nom du serveur). */
export function isNewer(a: LogEntry, b: LogEntry): boolean {
  if (a.utc !== b.utc) return a.utc > b.utc;
  if (a.seq !== b.seq && a.node === b.node) return BigInt(a.seq) > BigInt(b.seq);
  return a.node < b.node;
}

/**
 * Fusionne les réponses de plusieurs serveurs en une seule liste du plus récent au plus ancien, page après page.
 *
 * Chaque serveur donne ses événements triés. Pour produire le suivant, il faut connaître le prochain événement de CHAQUE serveur :
 * on ne charge donc la suite d'un serveur que lorsque sa réserve est vide. Un serveur qui rend une réponse partielle (sa recherche a
 * épuisé son temps) est simplement réinterrogé là où il s'était arrêté.
 */
export class MergedFeed {
  private readonly lanes: Lane[];
  private readonly seen = new Set<string>();

  constructor(sources: FeedSource[], private readonly pageSize: number) {
    this.lanes = sources.map(source => ({ source, buffer: [], cursor: null, exhausted: false, error: null, failed: false, scannedTo: null, answeredBy: null }));
  }

  /** Plus rien à lire : chaque serveur est épuisé ou en panne, et leurs réserves sont vides. */
  get done(): boolean {
    return this.lanes.every(l => !l.buffer.length && (l.exhausted || l.failed));
  }

  /** Les serveurs qui n'ont pas répondu. */
  get failures(): { label: string; error: unknown }[] {
    return this.lanes.filter(l => l.failed).map(l => ({ label: l.source.label, error: l.error }));
  }

  /** Tous les serveurs sont en panne. */
  get allFailed(): boolean {
    return this.lanes.length > 0 && this.lanes.every(l => l.failed);
  }

  /** Si plusieurs serveurs étaient visés mais qu'un même serveur a répondu à plusieurs appels : son nom. */
  get sameServer(): string | null {
    const answered = this.lanes.filter(l => l.answeredBy !== null);
    if (this.lanes.length < 2 || answered.length < 2) return null;
    const names = new Set(answered.map(l => l.answeredBy));
    return names.size < answered.length ? (answered[0].answeredBy as string) : null;
  }

  /** Les serveurs en panne seront réinterrogés au prochain `next()`. */
  retryFailed(): void {
    for (const lane of this.lanes) {
      if (lane.failed) {
        lane.failed = false;
        lane.error = null;
      }
    }
  }

  /** La page suivante (`pageSize` événements au plus ; moins s'il n'y en a plus). */
  async next(onProgress?: (progress: FeedProgress) => void): Promise<LogEntry[]> {
    const page: LogEntry[] = [];

    for (;;) {
      const hungry = this.lanes.filter(l => !l.buffer.length && !l.exhausted && !l.failed);
      if (hungry.length) {
        await Promise.all(hungry.map(l => this.fill(l)));
        onProgress?.({ scannedTo: this.oldestScanned() });
        continue;
      }

      // chaque serveur a un événement en réserve (ou n'en a plus) : le plus récent des premiers est le suivant
      let best: Lane | null = null;
      for (const lane of this.lanes) {
        if (lane.buffer.length && (!best || isNewer(lane.buffer[0], best.buffer[0]))) best = lane;
      }
      if (!best) break;

      const entry = best.buffer.shift() as LogEntry;
      // un même événement vu par deux appels (l'en-tête du répartiteur n'a pas été pris en compte) ne compte qu'une fois
      if (this.seen.has(entry.id)) continue;
      this.seen.add(entry.id);

      page.push(entry);
      if (page.length >= this.pageSize) break;
    }

    return page;
  }

  private async fill(lane: Lane): Promise<void> {
    try {
      const answer = await lane.source.fetch(lane.cursor);
      lane.answeredBy = answer.node || lane.answeredBy;
      lane.scannedTo = answer.scannedTo;
      lane.buffer.push(...answer.events);

      // pas de curseur pour continuer, ou le même qu'avant : on ne tourne pas en rond
      const stuck = !answer.exhausted && (!answer.resume || (lane.cursor && answer.resume.utc === lane.cursor.utc && answer.resume.id === lane.cursor.id));
      lane.exhausted = answer.exhausted || !!stuck;
      lane.cursor = answer.resume;
    } catch (error) {
      lane.failed = true;
      lane.error = error;
    }
  }

  private oldestScanned(): string | null {
    const days = this.lanes.map(l => l.scannedTo).filter((d): d is string => !!d).sort();
    return days[0] ?? null;
  }
}

/** Les événements les plus récents de chaque serveur, fusionnés (actualisation automatique). Un serveur qui échoue est ignoré. */
export async function newest(sources: FeedSource[]): Promise<LogEntry[]> {
  const answers = await Promise.allSettled(sources.map(s => s.fetch(null)));
  const all: LogEntry[] = [];
  const seen = new Set<string>();
  for (const answer of answers) {
    if (answer.status !== 'fulfilled') continue;
    for (const e of answer.value.events) {
      if (!seen.has(e.id)) {
        seen.add(e.id);
        all.push(e);
      }
    }
  }
  return all.sort((a, b) => (isNewer(a, b) ? -1 : 1));
}
