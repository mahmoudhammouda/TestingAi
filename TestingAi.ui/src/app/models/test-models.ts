export type TestStatus = 'Pending' | 'Running' | 'Green' | 'Red' | 'Ignored' | 'EnAttenteDecision';
export type TestAction = 'None' | 'FixTest' | 'FixCode' | 'Ignore';

export interface TestCase {
  id: number;
  sessionId: number;
  testName: string;
  className: string;
  methodName: string;
  testFilePath: string;
  sourceFilePath: string;
  status: TestStatus;
  action: TestAction;
  errorMessage?: string;
  fixApplied?: string;
  retryCount: number;
  createdAt: string;
  updatedAt?: string;
}

export interface SessionSummary {
  total: number;
  green: number;
  red: number;
  pending: number;
  awaitingDecision: number;
  ignored: number;
}

export interface Session {
  id: number;
  targetProject: string;
  sourceProject: string;
  status: string;
  createdAt: string;
  globalState?: string;
  metadata?: string;
  testStrategy?: string;
  summary: SessionSummary;
}

export interface PipelineSettings {
  humanInterventionEnabled: boolean;
  maxRetries: number;
  preferredProvider: string;
}

export interface StartSessionRequest {
  testProjectPath: string;
  sourceProjectPath?: string;
}

export interface StartSessionFromCodeRequest {
  sourceCode: string;
  fileName?: string;
}

export interface FolderFile {
  relativePath: string;
  content: string;
}

export interface StartSessionFromFolderRequest {
  files: FolderFile[];
  // Sous-ensemble de chemins .cs à couvrir ; vide/absent → tous les .cs.
  generateFor?: string[];
}

export interface CodeFile {
  fileName: string;
  content: string;
}

export interface SessionCode {
  sourceFiles: CodeFile[];
  testFiles: CodeFile[];
}

// ── Artefacts de la phase de génération (visualisation pipeline) ──────────────
export interface ParameterMetadata {
  name: string;
  type: string;
}

export interface MethodMetadata {
  name: string;
  returnType: string;
  parameters: ParameterMetadata[];
}

export interface CodeMetadata {
  className: string;
  namespace: string;
  dependencies: string[];
  methods: MethodMetadata[];
}

export interface AgentLog {
  id: number;
  sessionId: number;
  stepName: string;
  actionSummary: string;
  timestamp: string;
  // Commande lancée + résultat (agents déterministes / TestRunner) ; absent pour
  // les simples événements de cycle de vie (Début/Succès/Erreur).
  commandText?: string;
  commandOutput?: string;
}

// ── Dialogue LLM réel entre agents (AgentPrivateMemory) ──────────────────────
export interface AgentMessage {
  id: number;
  sessionId: number;
  agentName: string;
  role: string;
  content: string;
  timestamp: string;
}

// Un "tour" = un appel LLM regroupant le message système, le prompt et la réponse.
export interface AgentTurn {
  index: number;
  agentName: string;
  time: string;
  system: string;
  prompt: string;
  response: string;
}

// ── Communication agentique unifiée (A2A + dialogue LLM fusionnés) ────────────
// Un événement de cycle de vie A2A (Début / Fin / Erreur) tel qu'enregistré.
export interface AgentLifecycleEvent {
  summary: string;
  time: string;
  isError: boolean;
}

// Une commande lancée par un agent déterministe (ou TestRunner) et son résultat :
// ex. `dotnet build` / `dotnet test`, écriture de fichier, scan de découverte.
export interface AgentCommand {
  command: string;   // la commande / l'action exécutée
  output: string;    // le résultat / la sortie de la commande
  time: string;
}

// Une "activité" = une exécution d'un agent : son cycle de vie A2A + son éventuel
// dialogue LLM + sa spécialité + ses échanges entrants/sortants avec les autres agents.
export interface AgentActivity {
  index: number;
  agent: string;          // nom affiché (sans le suffixe "Agent")
  rawName: string;        // nom brut du step (tel qu'en base)
  icon: string;
  specialty: string;      // rôle court de l'agent
  does: string;           // ce que fait l'agent (données chargées / produites)
  startTime: string;
  endTime: string | null;
  status: 'running' | 'ok' | 'error';
  prevAgent: string | null;   // de qui il reçoit le relais
  nextAgent: string | null;   // à qui il transmet le relais
  events: AgentLifecycleEvent[];
  dialogues: AgentTurn[];     // appels LLM (peut être vide pour un agent déterministe)
  commands: AgentCommand[];   // commandes/actions déterministes et leurs résultats
  occurrence?: number;        // n° de passage de cet agent (boucle d'auto-correction)
  occurrenceTotal?: number;   // nombre total de passages de cet agent dans la session
}
