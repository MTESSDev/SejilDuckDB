import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

interface Call {
  url: string;
  method: string;
  header: string | null;
  body: string | null;
}

let calls: Call[] = [];
let failing = new Set<string>();

// L'interface lit `location` au chargement et appelle `fetch` : on les remplace, puis on charge le module.
async function load(nodes: string[]) {
  vi.resetModules();
  calls = [];
  vi.stubGlobal('location', { pathname: '/logs/' });
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      const headers = new Headers(init.headers);
      const call = { url, method: init.method ?? 'GET', header: headers.get('x-nlb2'), body: (init.body as string) ?? null };
      calls.push(call);
      if (url.endsWith('/nodes')) return new Response(JSON.stringify({ header: 'x-nlb2', nodes, node: 'srv-a' }));
      if (call.header && failing.has(call.header)) return new Response('no', { status: 503 });
      return new Response('{"minimumLogLevel":"Warning"}');
    }),
  );
  const mod = await import('./api');
  await mod.loadNodes();
  calls = []; // seuls les appels qui suivent nous intéressent
  return mod;
}

beforeEach(() => (failing = new Set()));
afterEach(() => vi.unstubAllGlobals());

describe('changer le niveau minimum', () => {
  it('appelle tous les serveurs, chacun avec son en-tête x-nlb2', async () => {
    const { api } = await load(['srv-a', 'srv-b']);

    await api.setMinLevel('Error');

    const posts = calls.filter(c => c.method === 'POST');
    expect(posts.map(c => c.header).sort()).toEqual(['srv-a', 'srv-b']);
    expect(posts.every(c => c.url === '/logs/min-log-level' && c.body === 'Error')).toBe(true);
  });

  it('échoue si un seul serveur n’a pas pris le changement, pour ne pas les laisser en désaccord', async () => {
    const { api } = await load(['srv-a', 'srv-b']);
    failing.add('srv-b');

    await expect(api.setMinLevel('Error')).rejects.toMatchObject({ status: 503 });
    expect(calls.filter(c => c.method === 'POST')).toHaveLength(2); // srv-a l'a reçu quand même : on peut répéter sans risque
  });

  it('un seul serveur : un appel ordinaire, sans en-tête', async () => {
    const { api } = await load([]);

    await api.setMinLevel('Error');

    expect(calls).toHaveLength(1);
    expect(calls[0].header).toBeNull();
  });
});

describe('la liste des serveurs', () => {
  const page = (text: string) => vi.stubGlobal('document', { getElementById: () => ({ textContent: text }) });

  async function fresh(fetchImpl: (url: string) => Promise<Response>) {
    vi.resetModules();
    vi.stubGlobal('location', { pathname: '/logs' });
    const fetchSpy = vi.fn(fetchImpl);
    vi.stubGlobal('fetch', fetchSpy);
    return { fetchSpy, mod: await import('./api') };
  }

  it('vient de la page, sans aucun appel : elle arrive d’un serveur qui répond', async () => {
    page('{"header":"x-nlb2","nodes":["srv-a","srv-b"],"node":"srv-a"}');
    const { fetchSpy, mod } = await fresh(async () => new Response('{}'));

    const info = await mod.loadNodes();

    expect(info.nodes).toEqual(['srv-a', 'srv-b']);
    expect(fetchSpy).not.toHaveBeenCalled();
    expect(mod.nodeCount()).toBe(2);
  });

  it('un marqueur que le serveur n’a pas remplacé est ignoré : on la demande au site', async () => {
    page('__SEJIL_CONFIG__');
    const { fetchSpy, mod } = await fresh(async () => new Response('{"header":"x-nlb2","nodes":["srv-a","srv-b"],"node":"srv-a"}'));

    expect((await mod.loadNodes()).nodes).toEqual(['srv-a', 'srv-b']);
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });

  it('en la demandant, réessaie si le répartiteur envoie l’appel au serveur en panne', async () => {
    let n = 0;
    const { fetchSpy, mod } = await fresh(async () =>
      ++n < 3 ? new Response('bad gateway', { status: 502 }) : new Response('{"header":"x-nlb2","nodes":["srv-a","srv-b"],"node":"srv-a"}'),
    );

    expect((await mod.loadNodes()).nodes).toEqual(['srv-a', 'srv-b']);
    expect(fetchSpy).toHaveBeenCalledTimes(3);
  });

  it('si personne ne répond, un seul serveur et sans en-tête', async () => {
    const { mod } = await fresh(async () => new Response('no', { status: 502 }));

    await mod.loadNodes();

    expect(mod.nodeCount()).toBe(1);
  });
});

describe('les requêtes enregistrées', () => {
  it('sont enregistrées et supprimées sur chaque serveur', async () => {
    const { api } = await load(['srv-a', 'srv-b']);

    await api.saveQuery({ name: 'Erreurs', query: 'level = Error' });
    await api.deleteQuery('Erreurs');

    expect(calls.filter(c => c.url.endsWith('/log-query')).map(c => c.header).sort()).toEqual(['srv-a', 'srv-b']);
    expect(calls.filter(c => c.url.endsWith('/del-query')).map(c => c.header).sort()).toEqual(['srv-a', 'srv-b']);
  });

  it('la lecture, elle, se contente des serveurs qui répondent', async () => {
    const { api } = await load(['srv-a', 'srv-b']);
    failing.add('srv-b');

    await expect(api.minLevel()).resolves.toEqual({ minimumLogLevel: 'Warning' });
  });
});
