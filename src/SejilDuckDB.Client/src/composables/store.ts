import { computed, reactive } from 'vue';
import { api, HttpError, loadNodes, nodeCount, PAGE_SIZE } from '../api';
import { MergedFeed, isNewer, newest } from '../feed';
import { t } from '../i18n';
import { withCondition } from '../queryText';
import { LEVELS, PERIODS } from '../types';
import type { DateRange, Filters, LevelName, LogEntry, Period, SavedQuery } from '../types';
import { notify } from './toasts';

const emptyFilters = (): Filters => ({ query: '', period: null, range: null, level: null, exceptionsOnly: false });

export const state = reactive({
  filters: emptyFilters(),
  entries: [] as LogEntry[],
  loading: false,
  error: null as string | null,
  exhausted: false,
  selectedId: null as string | null,
  queries: [] as SavedQuery[],
  minLevel: '',
  userName: '',
  title: 'Sejil',
  autoRefresh: 0,
  newCount: 0,
  /** Nombre de serveurs interrogés (1 : pas de fusion, pas de pastille de serveur). */
  servers: 1,
  /** Les serveurs qui n'ont pas répondu : les résultats sont partiels. */
  nodeErrors: [] as { node: string; message: string }[],
  /** Jour le plus ancien lu pendant une recherche longue (« aaaa-mm-jj »), pour montrer qu'elle avance. */
  scanning: null as string | null,
});

// Les serveurs sont lus en parallèle et fusionnés : chaque recherche a son MergedFeed, qui se souvient d'où en est chacun.
// Les pages suivantes partent du curseur de chaque serveur : les événements qui arrivent pendant la lecture sont plus récents,
// ils ne décalent rien.
let feed: MergedFeed | null = null;
let ticket = 0;
let timer = 0;
let warnedSameServer = false;

function describe(error: unknown): string {
  if (error instanceof HttpError) {
    return error.status === 401 || error.status === 403 ? t('forbidden') : t('serverError', { status: error.status });
  }
  return t('networkError');
}

export const selectedEntry = computed(() => state.entries.find(e => e.id === state.selectedId) ?? null);

export const applications = computed(() => {
  const counts = new Map<string, number>();
  for (const e of state.entries) counts.set(e.sourceApp, (counts.get(e.sourceApp) ?? 0) + 1);
  return [...counts].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).map(([name, count]) => ({ name, count }));
});

export const hasActiveFilters = computed(() => {
  const f = state.filters;
  return !!(f.query || f.period || f.range || f.level || f.exceptionsOnly);
});

/* ---------- événements ---------- */

export async function search() {
  ticket++;
  feed = new MergedFeed(api.sources({ ...state.filters }), PAGE_SIZE);
  warnedSameServer = false;
  Object.assign(state, { entries: [], exhausted: false, error: null, nodeErrors: [], scanning: null, selectedId: null, newCount: 0, loading: false });
  writeHash();
  await loadMore();
}

export async function loadMore() {
  if (!feed || state.loading || state.exhausted) return;
  const mine = ticket;
  const current = feed;
  state.loading = true;
  state.error = null;
  try {
    const events = await current.next(progress => {
      if (mine === ticket) state.scanning = progress.scannedTo;
    });
    if (mine !== ticket) return;

    if (!events.length && current.allFailed) {
      // aucun serveur n'a répondu : c'est une vraie erreur, avec « Réessayer »
      state.error = describe(current.failures[0].error);
      current.retryFailed();
      return;
    }

    const known = new Set(state.entries.map(e => e.id));
    state.entries.push(...events.filter(e => !known.has(e.id)));
    state.exhausted = current.done;
    state.nodeErrors = current.failures.map(f => ({ node: f.label, message: describe(f.error) }));

    const same = current.sameServer;
    if (same && !warnedSameServer) {
      warnedSameServer = true;
      notify(t('sameServer', { node: same }), 'error');
    }
  } catch (error) {
    if (mine === ticket) state.error = describe(error);
  } finally {
    if (mine === ticket) {
      state.loading = false;
      state.scanning = null;
    }
  }
}

/** Réinterroge les serveurs qui n'avaient pas répondu. */
export async function retryNodes() {
  if (!feed) return;
  feed.retryFailed();
  state.nodeErrors = [];
  state.exhausted = false;
  await loadMore();
}

/** Ajoute en tête les événements arrivés depuis le dernier chargement, sans toucher aux pages déjà lues. */
export async function refreshNew() {
  if (state.loading || state.error) return;
  const mine = ticket;
  try {
    const events = await newest(api.sources({ ...state.filters }));
    if (mine !== ticket) return;
    const known = new Set(state.entries.map(e => e.id));
    const top = state.entries[0];
    const fresh = events.filter(e => !known.has(e.id) && (!top || isNewer(e, top)));
    if (fresh.length) {
      state.entries.unshift(...fresh);
      state.newCount += fresh.length;
    }
  } catch {
    // une actualisation ratée n'interrompt pas la lecture : la suivante réessaiera
  }
}

export function setAutoRefresh(seconds: number) {
  state.autoRefresh = seconds;
  window.clearInterval(timer);
  if (seconds > 0) {
    timer = window.setInterval(() => {
      if (document.visibilityState === 'visible') void refreshNew();
    }, seconds * 1000);
  }
}

/* ---------- filtres ---------- */

export const setPeriod = (period: Period | null) => {
  Object.assign(state.filters, { period, range: null });
  return search();
};

export const setRange = (range: DateRange | null) => {
  Object.assign(state.filters, { range, period: null });
  return search();
};

export const setLevel = (level: LevelName | null) => {
  state.filters.level = state.filters.level === level ? null : level;
  return search();
};

export const setExceptionsOnly = (value: boolean) => {
  state.filters.exceptionsOnly = value;
  return search();
};

export const runQuery = (query: string) => {
  state.filters.query = query;
  return search();
};

export const addCondition = (name: string, op: '=' | '!=' | 'like' | 'not like', value: string) => runQuery(withCondition(state.filters.query, name, op, value));

export const resetFilters = () => {
  state.filters = emptyFilters();
  return search();
};

/* ---------- sélection ---------- */

export function select(id: string | null) {
  state.selectedId = id;
}

export function moveSelection(delta: number) {
  if (!state.entries.length) return;
  const i = state.entries.findIndex(e => e.id === state.selectedId);
  const target = Math.min(Math.max((i < 0 ? (delta > 0 ? -1 : 0) : i) + delta, 0), state.entries.length - 1);
  state.selectedId = state.entries[target].id;
  if (target >= state.entries.length - 3) void loadMore();
}

/* ---------- requêtes enregistrées ---------- */

export async function loadQueries() {
  try {
    state.queries = await api.queries();
  } catch (error) {
    notify(describe(error), 'error');
  }
}

export async function saveCurrentQuery(name: string): Promise<boolean> {
  if (state.queries.some(q => q.name.toLowerCase() === name.toLowerCase())) {
    notify(t('nameTaken'), 'error');
    return false;
  }
  try {
    await api.saveQuery({ name, query: state.filters.query });
    await loadQueries();
    notify(t('saved'));
    return true;
  } catch (error) {
    notify(describe(error), 'error');
    return false;
  }
}

export async function deleteSavedQuery(query: SavedQuery) {
  try {
    await api.deleteQuery(query.name);
    state.queries = state.queries.filter(q => q.name !== query.name);
  } catch (error) {
    notify(describe(error), 'error');
  }
}

/* ---------- niveau minimum ---------- */

export async function changeMinLevel(level: string) {
  try {
    await api.setMinLevel(level);
    state.minLevel = level;
    notify(t('minLevelSaved', { level }));
  } catch (error) {
    notify(describe(error), 'error');
  }
}

/* ---------- adresse partageable : les filtres vivent dans le fragment (#…) ---------- */

function writeHash() {
  const f = state.filters;
  const p = new URLSearchParams();
  if (f.query) p.set('q', f.query);
  if (f.period) p.set('p', f.period);
  if (f.range) {
    p.set('from', f.range.from);
    p.set('to', f.range.to);
  }
  if (f.level) p.set('l', f.level);
  if (f.exceptionsOnly) p.set('x', '1');
  const hash = p.toString();
  history.replaceState(null, '', location.pathname + location.search + (hash ? `#${hash}` : ''));
}

function readHash() {
  const p = new URLSearchParams(location.hash.slice(1));
  const period = p.get('p') as Period | null;
  const level = p.get('l') as LevelName | null;
  const from = p.get('from');
  const to = p.get('to');
  const date = /^\d{4}-\d{2}-\d{2}$/;
  state.filters = {
    query: p.get('q') ?? '',
    period: period && (PERIODS as readonly string[]).includes(period) ? period : null,
    range: from && to && date.test(from) && date.test(to) ? { from, to } : null,
    level: level && (LEVELS as readonly string[]).includes(level) ? level : null,
    exceptionsOnly: p.get('x') === '1',
  };
}

/* ---------- démarrage ---------- */

export async function init() {
  readHash();
  await loadNodes(); // avant tout appel : c'est elle qui dit combien de serveurs interroger et avec quel en-tête
  state.servers = nodeCount();

  const settle = async (work: Promise<unknown>) => {
    try {
      await work;
    } catch {
      // ces informations sont facultatives : l'interface fonctionne sans
    }
  };
  void settle(api.title().then(r => ((state.title = r.title), (document.title = r.title))));
  void settle(api.userName().then(r => (state.userName = r.userName)));
  void settle(api.minLevel().then(r => (state.minLevel = r.minimumLogLevel)));
  void loadQueries();
  await search();
}
