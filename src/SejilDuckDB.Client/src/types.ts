export const LEVELS = ['Verbose', 'Debug', 'Information', 'Warning', 'Error', 'Fatal'] as const;
export type LevelName = (typeof LEVELS)[number];

export const PERIODS = ['5m', '15m', '1h', '6h', '12h', '24h', '2d', '5d'] as const;
export type Period = (typeof PERIODS)[number];

export interface LogProperty {
  name: string;
  value: string | null;
}

export interface LogEntry {
  /** Unique dans l'interface : « serveur:numéro ». */
  id: string;
  /** Numéro de l'événement sur son serveur (du texte : il dépasse ce que JavaScript compte exactement). */
  seq: string;
  /** Le serveur qui détient l'événement. */
  node: string;
  /** Heure UTC à la microseconde : c'est elle qui sert à fusionner les serveurs. */
  utc: string;
  message: string;
  sourceApp: string;
  level: LevelName;
  /** Heure locale du serveur, sans fuseau : « 2026-10-07T19:21:41.083 ». */
  timestamp: string;
  exception: string | null;
  properties: LogProperty[];
}

export interface SavedQuery {
  name: string;
  query: string;
}

/** Bornes incluses, au format « aaaa-mm-jj ». */
export interface DateRange {
  from: string;
  to: string;
}

/** Un point dans l'ordre d'un serveur : la suite, ce sont les événements plus anciens. */
export interface Cursor {
  utc: string;
  id: string;
}

/** Une réponse d'un serveur à une recherche. */
export interface NodePage {
  node: string;
  events: LogEntry[];
  /** D'où reprendre pour obtenir la suite ; null s'il n'y en a plus. */
  resume: Cursor | null;
  exhausted: boolean;
  /** La page est courte parce que le temps de recherche du serveur est écoulé, pas parce qu'il n'y a plus rien. */
  partial: boolean;
  /** Le jour le plus ancien lu (« aaaa-mm-jj »). */
  scannedTo: string | null;
}

/** Les serveurs à interroger, et l'en-tête qui permet au répartiteur d'en atteindre un en particulier. */
export interface NodesInfo {
  header: string;
  nodes: string[];
  node: string;
}

export interface Filters {
  query: string;
  period: Period | null;
  range: DateRange | null;
  level: LevelName | null;
  exceptionsOnly: boolean;
}

/** Un point dans l'ordre d'un serveur : la suite, ce sont les événements plus anciens. */
export interface Cursor {
  utc: string;
  id: string;
}

/** Une réponse d'un serveur à une recherche. */
export interface NodePage {
  node: string;
  events: LogEntry[];
  /** D'où reprendre pour obtenir la suite ; null s'il n'y en a plus. */
  resume: Cursor | null;
  exhausted: boolean;
  /** La page est courte parce que le temps de recherche du serveur est écoulé, pas parce qu'il n'y a plus rien. */
  partial: boolean;
  /** Le jour le plus ancien lu (« aaaa-mm-jj »). */
  scannedTo: string | null;
}

/** Les serveurs à interroger, et l'en-tête qui permet au répartiteur d'en atteindre un en particulier. */
export interface NodesInfo {
  header: string;
  nodes: string[];
  node: string;
}
