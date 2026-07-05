import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { interval, Subscription } from 'rxjs';
import { switchMap } from 'rxjs/operators';
import { ApiService } from '../services/api.service';
import { Session, TestCase, PipelineSettings, SessionCode, CodeFile, CodeMetadata, MethodMetadata, ParameterMetadata, AgentLog, AgentMessage, AgentTurn, AgentActivity, AgentLifecycleEvent } from '../models/test-models';
import { NewSessionModalComponent } from '../new-session-modal/new-session-modal.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule, NewSessionModalComponent],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss'
})
export class DashboardComponent implements OnInit, OnDestroy {
  sessions: Session[] = [];
  selectedSessionId: number | null = null;
  selectedSession: Session | null = null;
  testCases: TestCase[] = [];
  filteredTests: TestCase[] = [];
  settings: PipelineSettings = { humanInterventionEnabled: false, maxRetries: 3, preferredProvider: 'Gemini' };

  filterStatus = 'all';
  selectedTestIds = new Set<number>();
  expandedErrors = new Set<number>();
  expandedIssues = new Set<string>();

  showNewSession = false;
  isResuming = false;
  isRerunning = false;

  activeTab: 'tests' | 'code' | 'pipeline' = 'tests';
  sessionCode: SessionCode | null = null;
  isLoadingCode = false;
  selectedSourceIdx = 0;
  selectedTestIdx = 0;

  agentLogs: AgentLog[] = [];
  isLoadingLogs = false;

  agentMessages: AgentMessage[] = [];
  isLoadingConversation = false;

  // Communication agentique unifiée (A2A + dialogue LLM)
  agentActivities: AgentActivity[] = [];
  expandedActivities = new Set<number>();

  private pollSub?: Subscription;
  private lastActivityCount = 0;
  // Signature des données ayant servi à la dernière reconstruction des activités :
  // évite de réaffecter agentActivities (et donc de re-render) quand rien n'a changé.
  private lastBuildSig = '';
  // Dès que l'utilisateur ouvre/ferme un détail, on cesse le défilement automatique
  // « live » pour le laisser consulter et copier tranquillement.
  private userPinnedActivities = false;

  // Fiche descriptive statique par agent : icône, spécialité et rôle (données chargées / produites).
  private readonly agentInfo: { [k: string]: { icon: string; specialty: string; does: string } } = {
    Analyzer:     { icon: '🔍', specialty: 'Analyse statique (Roslyn)', does: 'Lit le code source soumis et en extrait la structure : classe, namespace, dépendances et méthodes publiques. Produit les métadonnées consommées par le Decider.' },
    Decider:      { icon: '🧭', specialty: 'Stratège de test', does: 'Consulte les métadonnées produites par l\'Analyzer pour raisonner et définit la stratégie de test (cas nominaux, limites, erreurs). Transmet la stratégie au Creator.' },
    Creator:      { icon: '✍️', specialty: 'Génération du code de test', does: 'À partir de la stratégie du Decider, rédige un fichier de tests xUnit complet et compilable. Transmet le code à l\'Exporter.' },
    Exporter:     { icon: '📤', specialty: 'Écriture des fichiers', does: 'Écrit le fichier de tests généré dans le projet cible sur le disque. Action déterministe, sans appel LLM.' },
    Verifier:     { icon: '🔨', specialty: 'Compilation', does: 'Compile le projet de tests (dotnet build) et remonte les erreurs éventuelles au Creator pour correction. Action déterministe.' },
    TestDiscovery:{ icon: '🔎', specialty: 'Découverte des tests', does: 'Recense les cas de test présents dans le projet compilé. Action déterministe.' },
    TestRunner:   { icon: '▶️', specialty: 'Exécution des tests', does: 'Lance dotnet test et collecte les résultats (verts / rouges). Action déterministe.' },
    TestDecision: { icon: '⚖️', specialty: 'Diagnostic des échecs', does: 'Analyse chaque test rouge et décide de l\'action : corriger le test, corriger le code source, ou ignorer. Transmet sa décision au Fixer.' },
    TestFixer:    { icon: '🔧', specialty: 'Correction', does: 'Applique la correction décidée par le Decision puis relance la vérification.' }
  };

  private readonly csharpKeywords = new Set([
    'using','namespace','public','private','protected','internal','static',
    'readonly','const','class','interface','struct','enum','void','int','long',
    'double','float','decimal','string','bool','object','var','new','this',
    'base','null','true','false','if','else','for','foreach','in','while',
    'do','switch','case','default','break','continue','return','throw','try',
    'catch','finally','async','await','override','virtual','abstract','partial',
    'sealed','record','get','set','where','is','as','params','out','ref','Task'
  ]);

  constructor(private api: ApiService, private sanitizer: DomSanitizer) {}

  ngOnInit() { this.loadSessions(); this.loadSettings(); }
  ngOnDestroy() { this.pollSub?.unsubscribe(); }

  loadSessions() {
    this.api.getSessions().subscribe({ next: s => this.sessions = s, error: () => {} });
  }

  selectSession(id: number) {
    this.selectedSessionId = id;
    this.selectedSession = this.sessions.find(s => s.id === id) || null;
    this.filterStatus = 'all';
    this.selectedTestIds.clear();
    this.activeTab = 'tests';
    this.sessionCode = null;
    this.agentMessages = [];
    this.agentActivities = [];
    this.agentLogs = [];
    this.expandedActivities.clear();
    this.lastActivityCount = 0;
    this.lastBuildSig = '';
    this.userPinnedActivities = false;
    this.loadTests(id);
    this.startPolling(id);
  }

  loadTests(sessionId: number) {
    this.api.getTestCases(sessionId).subscribe({
      next: tests => {
        this.testCases = tests;
        this.applyFilter();
        this.api.getSession(sessionId).subscribe(s => {
          this.selectedSession = s;
          const idx = this.sessions.findIndex(x => x.id === sessionId);
          if (idx >= 0) this.sessions[idx] = s;
        });
      }
    });
  }

  startPolling(sessionId: number) {
    this.pollSub?.unsubscribe();
    this.pollSub = interval(2000).pipe(switchMap(() => this.api.getTestCases(sessionId))).subscribe({
      next: tests => {
        this.testCases = tests; this.applyFilter();
        this.api.getSession(sessionId).subscribe(s => {
          this.selectedSession = s;
          const idx = this.sessions.findIndex(x => x.id === sessionId);
          if (idx >= 0) this.sessions[idx] = s;
          if (this.activeTab === 'pipeline') {
            this.api.getLogs(sessionId).subscribe({ next: l => { this.agentLogs = l; this.rebuildActivities(); }, error: () => {} });
            this.api.getConversation(sessionId).subscribe({ next: m => { this.agentMessages = m; this.rebuildActivities(); }, error: () => {} });
          }
          // Une fois la session dans un état terminal, on arrête le polling : les données
          // ne changent plus, donc plus aucun re-render ne vient casser la sélection/copie.
          if (s.status === 'Terminé' || s.status === 'EchecPartiel' || s.status === 'Erreur') {
            this.pollSub?.unsubscribe();
          }
        });
      }
    });
  }

  switchTab(tab: 'tests' | 'code' | 'pipeline') {
    this.activeTab = tab;
    if (tab === 'code' && !this.sessionCode && this.selectedSessionId) {
      this.loadCode(this.selectedSessionId);
    }
    if (tab === 'pipeline' && this.selectedSessionId) {
      this.loadLogs(this.selectedSessionId);
      this.loadConversation(this.selectedSessionId);
    }
  }

  loadLogs(sessionId: number) {
    this.isLoadingLogs = true;
    this.api.getLogs(sessionId).subscribe({
      next: logs => { this.agentLogs = logs; this.isLoadingLogs = false; this.rebuildActivities(); },
      error: () => { this.isLoadingLogs = false; }
    });
  }

  loadConversation(sessionId: number) {
    this.isLoadingConversation = true;
    this.api.getConversation(sessionId).subscribe({
      next: msgs => { this.agentMessages = msgs; this.isLoadingConversation = false; this.rebuildActivities(); },
      error: () => { this.isLoadingConversation = false; }
    });
  }

  reloadPipeline() {
    if (!this.selectedSessionId) return;
    this.lastBuildSig = '';
    this.loadLogs(this.selectedSessionId);
    this.loadConversation(this.selectedSessionId);
  }

  trackActivity = (_: number, a: AgentActivity): number => a.index;

  // Normalise un nom de step (retire le suffixe "Agent") pour les correspondances.
  private normalizeAgent(name: string): string {
    return (name || '').replace(/Agent$/, '');
  }

  // Regroupe les messages bruts (User=prompt, System, Assistant=réponse) en tours d'agent.
  private buildConversationTurns(msgs: AgentMessage[]): AgentTurn[] {
    const turns: AgentTurn[] = [];
    let current: AgentTurn | null = null;
    for (const m of msgs) {
      const role = (m.role || '').toLowerCase();
      if (role === 'user') {
        current = { index: turns.length + 1, agentName: m.agentName, time: m.timestamp, system: '', prompt: m.content, response: '' };
        turns.push(current);
      } else if (role === 'system') {
        if (current && !current.system) { current.system = m.content; }
        else { current = { index: turns.length + 1, agentName: m.agentName, time: m.timestamp, system: m.content, prompt: '', response: '' }; turns.push(current); }
      } else if (role === 'assistant') {
        if (current && !current.response) { current.response = m.content; }
        else { current = { index: turns.length + 1, agentName: m.agentName, time: m.timestamp, system: '', prompt: '', response: m.content }; turns.push(current); }
      }
    }
    return turns;
  }

  // Fusionne la timeline A2A (agentLogs) et le dialogue LLM (conversation) en une suite
  // d'activités d'agents, dans l'ordre d'exécution (l'orchestrateur est séquentiel).
  private rebuildActivities() {
    // Tri défensif par id (= ordre d'insertion = ordre d'exécution) au cas où l'API
    // ne renverrait pas les enregistrements déjà triés.
    const logs = [...this.agentLogs].sort((a, b) => a.id - b.id);
    const messages = [...this.agentMessages].sort((a, b) => a.id - b.id);

    // Garde anti-rerender : si ni les logs ni le dialogue n'ont changé depuis la
    // dernière reconstruction, on ne réaffecte pas agentActivities. Cela préserve la
    // sélection de texte et la position de défilement pendant le polling (l'utilisateur
    // peut ouvrir un détail et copier sans que le « pull » de données réinitialise tout).
    const sig = `${logs.length}|${logs.length ? logs[logs.length - 1].id : 0}|`
              + `${messages.length}|${messages.length ? messages[messages.length - 1].id : 0}`;
    if (sig === this.lastBuildSig) { return; }
    this.lastBuildSig = sig;

    const turns = this.buildConversationTurns(messages);

    const isStart = (s: string) => /d[ée]but/i.test(s || '');
    const isEnd = (s: string) => /fin|succ[èe]s|termin/i.test(s || '');

    // 1) Reconstituer les occurrences d'agents à partir des logs (ordre d'insertion = exécution).
    const activities: AgentActivity[] = [];
    let cur: AgentActivity | null = null;
    for (const log of logs) {
      const summary = log.actionSummary || '';
      const err = this.isErrorLog(summary);
      if (!cur || cur.rawName !== log.stepName || isStart(summary)) {
        const key = this.normalizeAgent(log.stepName);
        const info = this.agentInfo[key];
        cur = {
          index: activities.length + 1,
          agent: key,
          rawName: log.stepName,
          icon: info?.icon ?? this.agentIcon(key),
          specialty: info?.specialty ?? 'Agent',
          does: info?.does ?? '',
          startTime: log.timestamp,
          endTime: null,
          status: 'running',
          prevAgent: null,
          nextAgent: null,
          events: [],
          dialogues: [],
          commands: []
        };
        activities.push(cur);
      }
      // Ligne « commande + résultat » (TestRunner / agent déterministe) : section
      // dédiée, en dehors du cycle de vie A2A (n'affecte pas le statut de l'activité).
      if (log.commandText || log.commandOutput) {
        cur.commands.push({
          command: log.commandText || '',
          output: log.commandOutput || '',
          time: log.timestamp
        });
        continue;
      }
      const evt: AgentLifecycleEvent = { summary, time: log.timestamp, isError: err };
      cur.events.push(evt);
      if (err) { cur.status = 'error'; cur.endTime = log.timestamp; }
      else if (isEnd(summary)) { cur.status = 'ok'; cur.endTime = log.timestamp; }
    }

    // 2) Rattacher chaque tour LLM à la bonne occurrence (file FIFO par agent normalisé).
    //    Borne haute d'attribution : la fin de l'occurrence si elle est connue ; sinon
    //    (fin non encore journalisée) le début de la PROCHAINE occurrence du même agent,
    //    afin de ne jamais attribuer les dialogues d'une exécution ultérieure à une
    //    exécution précédente. Comparaisons en millisecondes (timestamps ISO).
    const ms = (t: string | null): number => { const n = t ? Date.parse(t) : NaN; return isNaN(n) ? Number.POSITIVE_INFINITY : n; };
    const queues: { [k: string]: AgentTurn[] } = {};
    for (const t of turns) { (queues[this.normalizeAgent(t.agentName)] ||= []).push(t); }
    for (let i = 0; i < activities.length; i++) {
      const a = activities[i];
      const q = queues[a.agent];
      if (!q) continue;
      let boundMs = Number.POSITIVE_INFINITY;
      let inclusive = true;
      if (a.endTime) {
        boundMs = ms(a.endTime);
      } else {
        const next = activities.slice(i + 1).find(x => x.agent === a.agent);
        if (next) { boundMs = ms(next.startTime); inclusive = false; }
      }
      while (q.length) {
        const tm = Date.parse(q[0].time);
        const within = isNaN(tm) ? true : (tm < boundMs || (inclusive && tm === boundMs));
        if (within) { a.dialogues.push(q.shift()!); } else { break; }
      }
    }

    // 3) Renseigner les relais entrant / sortant (qui communique avec qui).
    for (let i = 0; i < activities.length; i++) {
      activities[i].prevAgent = i > 0 ? activities[i - 1].agent : null;
      activities[i].nextAgent = i < activities.length - 1 ? activities[i + 1].agent : null;
    }

    // Numérotation des passages : un agent relancé par la boucle d'auto-correction
    // (TestFixer / TestRunner) ou régénéré sur build en échec (Creator) apparaît
    // plusieurs fois — on indique « passage n/total » pour expliquer la répétition.
    const total: { [k: string]: number } = {};
    for (const a of activities) { total[a.agent] = (total[a.agent] || 0) + 1; }
    const seen: { [k: string]: number } = {};
    for (const a of activities) {
      seen[a.agent] = (seen[a.agent] || 0) + 1;
      a.occurrence = seen[a.agent];
      a.occurrenceTotal = total[a.agent];
    }

    this.agentActivities = activities;

    // Suivi « live » : tant que la session tourne ET que l'utilisateur n'a pas ouvert
    // de détail, garder la dernière activité ouverte et visible.
    if (this.isSessionRunning && !this.userPinnedActivities && activities.length > this.lastActivityCount) {
      const last = activities[activities.length - 1];
      if (last) { this.expandedActivities.add(last.index); this.scrollActivityToLatest(); }
    }
    this.lastActivityCount = activities.length;
  }

  private scrollActivityToLatest() {
    if (typeof document === 'undefined') return;
    setTimeout(() => {
      const items = document.querySelectorAll('.ac-item');
      const el = items[items.length - 1] as HTMLElement | undefined;
      el?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }, 60);
  }

  toggleActivity(index: number) {
    this.userPinnedActivities = true;
    if (this.expandedActivities.has(index)) { this.expandedActivities.delete(index); }
    else { this.expandedActivities.add(index); }
  }

  // Copie un bloc de texte dans le presse-papiers, avec retour visuel sur le bouton.
  copyText(text: string, ev: Event) {
    const btn = ev.currentTarget as HTMLButtonElement | null;
    const ok = () => {
      if (!btn) return;
      const original = btn.getAttribute('data-label') || btn.innerText;
      btn.setAttribute('data-label', original);
      btn.innerText = '✓ Copié';
      btn.classList.add('copied');
      setTimeout(() => { btn.innerText = original; btn.classList.remove('copied'); }, 1300);
    };
    if (navigator.clipboard?.writeText) {
      navigator.clipboard.writeText(text).then(ok).catch(() => this.fallbackCopy(text, ok));
    } else {
      this.fallbackCopy(text, ok);
    }
  }

  private fallbackCopy(text: string, done: () => void) {
    if (typeof document === 'undefined') return;
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand('copy'); done(); } catch { /* ignore */ }
    document.body.removeChild(ta);
  }

  // Explique, pour les agents relancés en boucle, pourquoi plusieurs appels LLM ont lieu.
  loopExplanation(a: AgentActivity): string {
    const n = a.dialogues.length;
    const many = n > 1 ? ` (d'où ${n} appels LLM ici)` : '';
    switch (a.agent) {
      case 'TestDecision':
        return `Diagnostic indépendant de chaque test rouge : 1 appel LLM par test en échec${many}. `
             + `Pour chacun, l'agent décide s'il faut corriger le test, corriger le code source, ou l'ignorer.`;
      case 'TestFixer':
        return `Correction groupée par fichier : 1 appel LLM par fichier à réparer — tous les tests `
             + `en échec d'un même fichier sont corrigés ensemble${many}. L'agent est relancé à `
             + `chaque passage de la boucle d'auto-correction tant que des tests restent rouges `
             + `(max ${this.settings.maxRetries} tentative(s)).`;
      case 'TestRunner':
        return (a.occurrenceTotal && a.occurrenceTotal > 1)
          ? `Relancé (dotnet test) après chaque vague de corrections : la boucle « corriger → relancer » `
            + `se répète jusqu'à ce qu'aucun test ne soit rouge (ou la limite de tentatives atteinte).`
          : '';
      case 'Creator':
        return n > 1
          ? `Code de test régénéré car le build a échoué : nouvel appel LLM avec les erreurs de `
            + `compilation en contexte (max 2 tentatives).`
          : '';
      default:
        return '';
    }
  }

  // Raison synthétique d'un échange LLM, lue depuis l'en-tête « Objectif : … » du prompt.
  // (Les agents Décision et Correction écrivent cet en-tête stable en première ligne.)
  llmReason(t: AgentTurn): string {
    const first = (t.prompt || '').split('\n').map(l => l.trim()).find(l => l.length > 0) || '';
    const m = first.match(/^Objectif\s*:\s*(.+)$/i);
    return m ? m[1].trim() : '';
  }

  isActivityExpanded(index: number): boolean { return this.expandedActivities.has(index); }

  expandAllActivities() { this.userPinnedActivities = true; this.agentActivities.forEach(a => this.expandedActivities.add(a.index)); }
  collapseAllActivities() { this.userPinnedActivities = true; this.expandedActivities.clear(); }

  activityStatusLabel(a: AgentActivity): string {
    return a.status === 'running' ? 'En cours…' : a.status === 'error' ? 'Erreur' : 'Terminé';
  }

  // ── Artefacts de génération (onglet Pipeline) ───────────────────────────────

  private mapMetadata(m: any): CodeMetadata {
    return {
      className: m?.ClassName ?? m?.className ?? '',
      namespace: m?.Namespace ?? m?.namespace ?? '',
      dependencies: m?.Dependencies ?? m?.dependencies ?? [],
      methods: (m?.Methods ?? m?.methods ?? []).map((x: any): MethodMetadata => ({
        name: x.Name ?? x.name ?? '',
        returnType: x.ReturnType ?? x.returnType ?? '',
        parameters: (x.Parameters ?? x.parameters ?? []).map((p: any): ParameterMetadata => ({
          name: p.Name ?? p.name ?? '',
          type: p.Type ?? p.type ?? ''
        }))
      }))
    };
  }

  // Métadonnées d'analyse mono-fichier. Renvoie null si la session est multi-fichiers
  // (les métadonnées sont alors un tableau — voir analysisPerFile).
  get analysis(): CodeMetadata | null {
    const raw = this.selectedSession?.metadata;
    if (!raw) return null;
    try {
      const m: any = JSON.parse(raw);
      if (Array.isArray(m)) return null;
      return this.mapMetadata(m);
    } catch {
      return null;
    }
  }

  // Analyse par fichier (import de dossier). Vide pour une session mono-fichier.
  get analysisPerFile(): { file: string; metadata: CodeMetadata | null; error: string }[] {
    const raw = this.selectedSession?.metadata;
    if (!raw) return [];
    try {
      const m: any = JSON.parse(raw);
      if (!Array.isArray(m)) return [];
      return m.map((e: any) => ({
        file: e.File ?? e.file ?? '',
        error: e.Error ?? e.error ?? '',
        metadata: (e.Metadata ?? e.metadata) ? this.mapMetadata(e.Metadata ?? e.metadata) : null
      }));
    } catch {
      return [];
    }
  }

  // Progression d'une génération multi-fichiers ; null pour une session mono-fichier.
  get folderProgress(): { total: number; done: number; current: string } | null {
    const raw = this.selectedSession?.globalState;
    if (!raw) return null;
    try {
      const g: any = JSON.parse(raw);
      const total = g.FilesTotal ?? g.filesTotal ?? 0;
      if (!total || total <= 1) return null;
      return { total, done: g.FilesDone ?? g.filesDone ?? 0, current: g.CurrentFile ?? g.currentFile ?? '' };
    } catch {
      return null;
    }
  }

  // Nom de fichier seul à partir d'un chemin (séparateurs / ou \).
  baseName(p: string | undefined): string {
    if (!p) return '';
    const s = p.replace(/\\/g, '/');
    return s.substring(s.lastIndexOf('/') + 1);
  }

  get testStrategy(): string { return this.selectedSession?.testStrategy ?? ''; }

  formatParams(params: ParameterMetadata[]): string {
    if (!params || params.length === 0) return '()';
    return '(' + params.map(p => `${p.type} ${p.name}`).join(', ') + ')';
  }

  agentIcon(step: string): string {
    const map: { [k: string]: string } = {
      Analyzer: '🔍', Decider: '🧭', Creator: '✍️', Exporter: '📤', Verifier: '🔨',
      Discovery: '🔎', TestDiscovery: '🔎', Runner: '▶️', TestRunner: '▶️',
      Decision: '⚖️', TestDecision: '⚖️', Fixer: '🔧', TestFixer: '🔧'
    };
    return map[step] ?? '⚙️';
  }

  isErrorLog(s: string): boolean { return /erreur|error|échec|fail/i.test(s || ''); }

  loadCode(sessionId: number) {
    this.isLoadingCode = true;
    this.api.getSessionCode(sessionId).subscribe({
      next: code => {
        this.sessionCode = code;
        this.selectedSourceIdx = 0;
        this.selectedTestIdx = 0;
        this.isLoadingCode = false;
      },
      error: () => { this.isLoadingCode = false; }
    });
  }

  get currentSourceFile(): CodeFile | null {
    return this.sessionCode?.sourceFiles[this.selectedSourceIdx] ?? null;
  }

  get currentTestFile(): CodeFile | null {
    return this.sessionCode?.testFiles[this.selectedTestIdx] ?? null;
  }

  // ── Quality gate ────────────────────────────────────────────────────────────

  get qualityScore(): number {
    if (!this.testCases.length) return 0;
    return Math.round((this.countByStatus('Green') / this.testCases.length) * 100);
  }

  get qualityGateLabel(): string {
    const s = this.qualityScore;
    if (s === 100) return 'PASSED';
    if (s >= 70) return 'PARTIAL';
    return 'FAILED';
  }

  get qualityGateClass(): string {
    const s = this.qualityScore;
    if (s === 100) return 'qg-pass';
    if (s >= 70) return 'qg-partial';
    return 'qg-fail';
  }

  get failingTests(): TestCase[] {
    return this.testCases.filter(t => t.status === 'Red');
  }

  toggleIssue(key: string) {
    this.expandedIssues.has(key) ? this.expandedIssues.delete(key) : this.expandedIssues.add(key);
  }
  isIssueExpanded(key: string) { return this.expandedIssues.has(key); }

  // ── Syntax highlighting — source (SonarQube style, clean) ───────────────────

  // Applique la coloration (chaînes, attributs, mots-clés) à un fragment DÉJÀ
  // échappé. Les chaînes/attributs sont mis de côté via des jetons neutres
  // (\u0000n\u0000) AVANT la passe des mots-clés : sinon un mot-clé C# présent
  // dans le HTML injecté (ex. « class » dans <span class="hl-str">) serait
  // re-coloré et casserait la balise (le navigateur recracherait alors
  // class="hl-str"> en texte brut). Restauration en ordre inverse car un jeton
  // peut en contenir un autre d'indice inférieur (ex. [InlineData("x")]).
  private applySyntax(escaped: string): string {
    const kw = [...this.csharpKeywords].join('|');
    const stash: string[] = [];
    const stashTok = (html: string) => {
      const tok = `\u0000${stash.length}\u0000`;
      stash.push(html);
      return tok;
    };
    let out = escaped
      .replace(/"[^"]*"/g, m => stashTok(`<span class="hl-str">${m}</span>`))
      .replace(/(\[(?:Fact|Theory|InlineData|TestCase)[^\]]*\])/g, m => stashTok(`<span class="hl-attr">${m}</span>`))
      .replace(new RegExp(`\\b(${kw})\\b`, 'g'), w => `<span class="hl-kw">${w}</span>`);
    for (let i = stash.length - 1; i >= 0; i--) {
      out = out.split(`\u0000${i}\u0000`).join(stash[i]);
    }
    return out;
  }

  highlight(code: string): SafeHtml {
    const lines = code.split('\n').map((rawLine, i) => {
      const num = String(i + 1).padStart(4, '\u00a0');
      let line = rawLine
        .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
      const commentIdx = line.indexOf('//');
      let prefix = commentIdx !== -1 ? line.slice(0, commentIdx) : line;
      const sfx = commentIdx !== -1 ? `<span class="hl-cmt">${line.slice(commentIdx)}</span>` : '';
      prefix = this.applySyntax(prefix);
      return `<span class="code-line"><span class="ln">${num}</span><span class="gut-empty"> </span>${prefix}${sfx}</span>`;
    });
    return this.sanitizer.bypassSecurityTrustHtml(lines.join('\n'));
  }

  // ── Syntax highlighting — tests (Checkmarx style, annotated) ────────────────

  highlightTest(code: string): SafeHtml {
    const testMap = new Map<string, TestCase>();
    this.testCases.forEach(t => testMap.set(t.methodName, t));
    const lines = code.split('\n');

    // First pass: assign each line a status based on which test method it belongs to
    const lineStatus: (string | null)[] = new Array(lines.length).fill(null);
    let currentStatus: string | null = null;
    let pendingFactLineIdx = -1;
    let depth = 0;
    let inMethod = false;

    for (let i = 0; i < lines.length; i++) {
      const raw = lines[i];

      // Detect [Fact] / [Theory]
      if (/\[\s*(Fact|Theory)\b/.test(raw)) {
        pendingFactLineIdx = i;
      }

      // Detect test method signature
      const mMatch = raw.match(/\bpublic\b[\s\S]*?\b(?:async\s+)?(?:void|Task)\b\s+(\w+)\s*\(/);
      if (mMatch && pendingFactLineIdx !== -1 && pendingFactLineIdx >= i - 2) {
        const methodName = mMatch[1];
        const tc = testMap.get(methodName);
        currentStatus = tc?.status ?? 'Unknown';
        inMethod = true;
        depth = 0;
        // Back-fill the [Fact] line
        if (pendingFactLineIdx >= 0) lineStatus[pendingFactLineIdx] = currentStatus;
        lineStatus[i] = currentStatus;
        pendingFactLineIdx = -1;
      } else if (inMethod) {
        lineStatus[i] = currentStatus;
        for (const ch of raw) {
          if (ch === '{') depth++;
          else if (ch === '}') {
            depth--;
            if (depth <= 0) { inMethod = false; currentStatus = null; depth = 0; break; }
          }
        }
      }
    }

    // Second pass: render with gutter markers and line backgrounds
    const rendered = lines.map((rawLine, i) => {
      const num = String(i + 1).padStart(4, '\u00a0');
      const status = lineStatus[i];

      let esc = rawLine
        .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

      const isFact = /\[\s*(Fact|Theory)\b/.test(rawLine);
      const isMethodSig = /\bpublic\b[\s\S]*?\b(?:async\s+)?(?:void|Task)\b\s+\w+\s*\(/.test(rawLine);
      const isAssert = /\bAssert\./.test(rawLine);
      const isKeyLine = isFact || isMethodSig;

      // Gutter marker
      let gut = '<span class="gut-empty"> </span>';
      if (isKeyLine && status) {
        if (status === 'Green')              gut = '<span class="gut-ok" title="Test réussi">●</span>';
        else if (status === 'Red')           gut = '<span class="gut-err" title="Test échoué">●</span>';
        else if (status === 'Pending' || status === 'Running') gut = '<span class="gut-pending" title="En attente">◐</span>';
        else if (status === 'EnAttenteDecision') gut = '<span class="gut-await" title="Décision requise">◆</span>';
      } else if (isAssert && status === 'Red') {
        gut = '<span class="gut-assert" title="Assertion échouée">!</span>';
      }

      // Syntax
      const commentIdx = esc.indexOf('//');
      let prefix = commentIdx !== -1 ? esc.slice(0, commentIdx) : esc;
      const sfx = commentIdx !== -1 ? `<span class="hl-cmt">${esc.slice(commentIdx)}</span>` : '';
      prefix = this.applySyntax(prefix);

      // Line class
      let cls = 'code-line';
      if (status === 'Red' && isKeyLine) cls += ' line-fail-sig';
      else if (status === 'Red' && isAssert) cls += ' line-fail-assert';
      else if (status === 'Green' && isKeyLine) cls += ' line-ok-sig';
      else if (status === 'EnAttenteDecision' && isKeyLine) cls += ' line-await-sig';

      return `<span class="${cls}"><span class="ln">${num}</span>${gut}${prefix}${sfx}</span>`;
    });

    return this.sanitizer.bypassSecurityTrustHtml(rendered.join('\n'));
  }

  onSessionCreated(sessionId: number) {
    this.showNewSession = false;
    this.loadSessions();
    setTimeout(() => this.selectSession(sessionId), 400);
  }

  onModalCancelled() { this.showNewSession = false; }

  resumeSession() {
    if (!this.selectedSessionId) return;
    this.isResuming = true;
    this.api.resumeSession(this.selectedSessionId).subscribe({
      next: () => { this.isResuming = false; if (this.selectedSessionId) { this.loadTests(this.selectedSessionId); this.startPolling(this.selectedSessionId); } },
      error: err => { this.isResuming = false; alert(err?.error?.detail || 'Des décisions sont encore en attente.'); }
    });
  }

  rerunSession() {
    if (!this.selectedSessionId || this.isRerunning) return;
    const id = this.selectedSessionId;
    this.isRerunning = true;
    this.api.rerunSession(id).subscribe({
      next: () => {
        this.isRerunning = false;
        this.lastBuildSig = '';
        this.loadTests(id);
        this.loadLogs(id);
        this.loadConversation(id);
        this.startPolling(id);
      },
      error: err => {
        this.isRerunning = false;
        // Le pipeline a de nouveau échoué : recharge l'état pour afficher le motif à jour.
        this.loadTests(id);
        this.loadLogs(id);
        alert(err?.error?.detail || 'La relance a échoué. Consultez le motif d\'erreur affiché.');
      }
    });
  }

  applyFilter() {
    this.filteredTests = this.filterStatus === 'all'
      ? [...this.testCases]
      : this.testCases.filter(t => t.status.toLowerCase() === this.filterStatus.toLowerCase());
  }

  setFilter(s: string) { this.filterStatus = s; this.applyFilter(); this.selectedTestIds.clear(); }
  toggleSelect(id: number) { this.selectedTestIds.has(id) ? this.selectedTestIds.delete(id) : this.selectedTestIds.add(id); }
  toggleSelectAll() {
    if (this.selectedTestIds.size === this.filteredTests.length) this.selectedTestIds.clear();
    else this.filteredTests.forEach(t => this.selectedTestIds.add(t.id));
  }
  get allSelected() { return this.filteredTests.length > 0 && this.selectedTestIds.size === this.filteredTests.length; }

  setAction(testId: number, action: string) {
    this.api.setAction(testId, action).subscribe({ next: () => { if (this.selectedSessionId) this.loadTests(this.selectedSessionId); } });
  }

  bulkAction(action: string) {
    if (!this.selectedTestIds.size) return;
    this.api.bulkSetAction(Array.from(this.selectedTestIds), action).subscribe({
      next: () => { this.selectedTestIds.clear(); if (this.selectedSessionId) this.loadTests(this.selectedSessionId); }
    });
  }

  toggleError(id: number) { this.expandedErrors.has(id) ? this.expandedErrors.delete(id) : this.expandedErrors.add(id); }
  isErrorExpanded(id: number) { return this.expandedErrors.has(id); }

  loadSettings() { this.api.getSettings().subscribe({ next: s => this.settings = s, error: () => {} }); }
  saveSettings() { this.api.updateSettings(this.settings).subscribe(); }
  toggleHumanIntervention() { this.settings.humanInterventionEnabled = !this.settings.humanInterventionEnabled; this.saveSettings(); }

  statusLabel(s: string): string {
    return ({ Green: '✓ Vert', Red: '✗ Rouge', Pending: '⏳ En attente', Running: '▶ En cours', Ignored: '— Ignoré', EnAttenteDecision: '👤 Décision requise' } as any)[s] ?? s;
  }
  statusClass(s: string): string {
    return ({ Green: 'status-green', Red: 'status-red', Pending: 'status-pending', Running: 'status-running', Ignored: 'status-ignored', EnAttenteDecision: 'status-awaiting' } as any)[s] ?? '';
  }
  sessionStatusLabel(s: string): string {
    return ({ 'En_Cours': 'En cours', 'Terminé': 'Terminé', 'EnAttenteDecision': 'Décision requise', 'EchecPartiel': 'Échec partiel', 'Erreur': 'Erreur' } as any)[s] ?? s;
  }
  get isSessionAwaitingDecision() { return this.selectedSession?.status === 'EnAttenteDecision'; }
  get isSessionRunning() { return this.selectedSession?.status === 'En_Cours'; }
  get isSessionErrored() { return this.selectedSession?.status === 'Erreur'; }
  get sessionErrorMessage(): string {
    const raw = this.selectedSession?.globalState;
    if (!raw) return '';
    try { return (JSON.parse(raw)?.Error as string) || ''; } catch { return ''; }
  }
  countByStatus(status: string) { return this.testCases.filter(t => t.status === status).length; }
  truncate(s: string | undefined, max = 120) { if (!s) return ''; return s.length > max ? s.slice(0, max) + '…' : s; }
  trackById(_: number, t: TestCase) { return t.id; }
}
