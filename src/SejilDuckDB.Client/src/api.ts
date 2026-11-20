import type { FeedSource } from './feed';
import { addDays } from './format';
import type { Cursor, Filters, LogEntry, NodePage, NodesInfo, SavedQuery } from './types';

/** Le site sert l'interface à l'adresse de SejilDuckDB (« /logs ») : ses routes sont juste en dessous. */
const root = location.pathname.replace(/\/+$/, '');

export const PAGE_SIZE = 100;

export class HttpError extends Error {
  constructor(public status: number, statusText: string) {
    super(statusText || `HTTP ${status}`);
  }
}

/**
 * Les serveurs à interroger. Vide : un seul serveur, sans en-tête.
 * Sinon chaque appel est répété pour chaque serveur, avec l'en-tête (ex. `x-nlb2: srv-a`) qui force le répartiteur à le choisir.
 */
let info: NodesInfo = { header: 'x-nlb2', nodes: [], node: '' };

/** Les cibles d'un appel : `null` = sans en-tête (un seul serveur). */
const targets = (): (string | null)[] => (info.nodes.length ? info.nodes : [null]);

export const nodeCount = () => targets().length;

/** La liste que le serveur a mise dans la page (absente en développement, où la page vient de Vite). */
function embedded(): NodesInfo | null {
  if (typeof document === 'undefined') return null;
  try {
    const parsed = JSON.parse(document.getElementById('sejil-config')?.textContent ?? '') as Partial<NodesInfo>;
    return parsed && Array.isArray(parsed.nodes) ? { header: parsed.header ?? info.header, nodes: parsed.nodes, node: parsed.node ?? '' } : null;
  } catch {
    return null; // marqueur jamais remplacé, ou pas de page du serveur
  }
}

/**
 * À appeler au démarrage : la liste des serveurs. Elle vient de la page elle-même ; à défaut (développement) on la demande au site,
 * en réessayant, car le répartiteur peut envoyer l'appel au serveur qui est justement en panne. Sans réponse, un seul serveur.
 */
export async function loadNodes(): Promise<NodesInfo> {
  const fromPage = embedded();
  if (fromPage) return (info = fromPage);

  for (let attempt = 0; attempt < 4; attempt++) {
    try {
      const response = await fetch(root + '/nodes');
      if (response.ok) return (info = (await response.json()) as NodesInfo);
    } catch {
      // on réessaie
    }
  }
  return info;
}

async function request(target: string | null, path: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers);
  if (target) headers.set(info.header, target);
  const response = await fetch(root + path, { ...init, headers });
  if (!response.ok) throw new HttpError(response.status, response.statusText);
  return response;
}

async function call<T>(target: string | null, path: string, init?: RequestInit): Promise<T> {
  return (await (await request(target, path, init)).json()) as T;
}

/** Le même appel auprès de chaque serveur ; échoue seulement si aucun n'a répondu. Les réponses des autres sont rendues. */
async function everywhere<T>(work: (target: string | null) => Promise<T>): Promise<T[]> {
  const answers = await Promise.allSettled(targets().map(work));
  const ok = answers.filter((a): a is PromiseFulfilledResult<Awaited<T>> => a.status === 'fulfilled').map(a => a.value);
  if (!ok.length) throw (answers[0] as PromiseRejectedResult).reason;
  return ok as T[];
}

/**
 * Le même ordre à chaque serveur, et il échoue si UN SEUL a échoué : sinon le niveau minimum, par exemple, resterait différent
 * d'un serveur à l'autre sans que personne le sache. Répéter l'ordre est sans danger (ceux qui l'ont reçu le reçoivent à nouveau).
 */
async function onEveryServer(work: (target: string | null) => Promise<unknown>): Promise<void> {
  const answers = await Promise.allSettled(targets().map(work));
  const failed = answers.find((a): a is PromiseRejectedResult => a.status === 'rejected');
  if (failed) throw failed.reason;
}

function searchBody(filters: Filters): string {
  return JSON.stringify({
    queryText: filters.query,
    dateFilter: filters.period,
    // le serveur exclut la borne de fin : on envoie le lendemain pour que le dernier jour choisi soit inclus
    dateRangeFilter: filters.range ? [filters.range.from, addDays(filters.range.to, 1)] : null,
    levelFilter: filters.level,
    exceptionsOnly: filters.exceptionsOnly,
  });
}

/** Un serveur de plus à lire pour une recherche donnée. */
function source(target: string | null, filters: Filters): FeedSource {
  return {
    label: target ?? 'serveur',
    async fetch(cursor: Cursor | null): Promise<NodePage> {
      const params = new URLSearchParams({ pageSize: String(PAGE_SIZE) });
      if (cursor) params.set('before', `${cursor.utc}|${cursor.id}`);
      const page = await call<NodePage>(target, `/events?${params}`, { method: 'POST', body: searchBody(filters) });
      return { ...page, events: page.events.map(identified) };
    },
  };
}

// Deux serveurs peuvent avoir le même numéro : l'identifiant montré à l'interface porte aussi le serveur.
const identified = (e: LogEntry): LogEntry => ({ ...e, seq: e.id, id: `${e.node}:${e.id}` });

export const api = {
  /** Une source par serveur pour cette recherche : le `MergedFeed` les fusionne. */
  sources: (filters: Filters): FeedSource[] => targets().map(t => source(t, filters)),

  /** Les requêtes enregistrées de tous les serveurs, sans doublon de nom. */
  async queries(): Promise<SavedQuery[]> {
    const lists = await everywhere(t => call<SavedQuery[]>(t, '/log-queries'));
    const byName = new Map<string, SavedQuery>();
    for (const q of lists.flat()) if (!byName.has(q.name.toLowerCase())) byName.set(q.name.toLowerCase(), q);
    return [...byName.values()].sort((a, b) => a.name.localeCompare(b.name));
  },

  // Les écritures vont à TOUS les serveurs : chacun garde les siennes, et le répartiteur envoie les lectures (celles des applications
  // qui lisent le niveau minimum, par exemple) à n'importe lequel.
  saveQuery: (q: SavedQuery) => onEveryServer(t => request(t, '/log-query', { method: 'POST', body: JSON.stringify(q) })),
  deleteQuery: (name: string) => onEveryServer(t => request(t, '/del-query', { method: 'POST', body: name })),
  setMinLevel: (level: string) => onEveryServer(t => request(t, '/min-log-level', { method: 'POST', body: level })),

  minLevel: async () => (await everywhere(t => call<{ minimumLogLevel: string }>(t, '/min-log-level')))[0],

  // Ces deux-là ne dépendent pas du serveur : un appel ordinaire, sans en-tête.
  userName: () => call<{ userName: string }>(null, '/user-name'),
  title: () => call<{ title: string }>(null, '/title'),
};
