import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { interval, Subscription } from 'rxjs';
import { switchMap } from 'rxjs/operators';
import { ApiService } from '../services/api.service';
import { Session, TestCase, PipelineSettings, TestStatus } from '../models/test-models';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule],
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

  showNewSession = false;
  newTestPath = '';
  newSourcePath = '';
  isStarting = false;
  isResuming = false;
  startError = '';

  private pollSub?: Subscription;

  constructor(private api: ApiService) {}

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
    this.pollSub = interval(4000).pipe(switchMap(() => this.api.getTestCases(sessionId))).subscribe({
      next: tests => {
        this.testCases = tests; this.applyFilter();
        this.api.getSession(sessionId).subscribe(s => {
          this.selectedSession = s;
          const idx = this.sessions.findIndex(x => x.id === sessionId);
          if (idx >= 0) this.sessions[idx] = s;
        });
      }
    });
  }

  startNewSession() {
    if (!this.newTestPath.trim()) return;
    this.isStarting = true; this.startError = '';
    this.api.startSession({ testProjectPath: this.newTestPath, sourceProjectPath: this.newSourcePath }).subscribe({
      next: res => {
        this.isStarting = false; this.showNewSession = false;
        this.newTestPath = ''; this.newSourcePath = '';
        this.loadSessions();
        setTimeout(() => this.selectSession(res.sessionId), 500);
      },
      error: err => { this.isStarting = false; this.startError = err?.error?.detail || 'Erreur lors du démarrage'; }
    });
  }

  resumeSession() {
    if (!this.selectedSessionId) return;
    this.isResuming = true;
    this.api.resumeSession(this.selectedSessionId).subscribe({
      next: () => { this.isResuming = false; if (this.selectedSessionId) this.loadTests(this.selectedSessionId); },
      error: err => { this.isResuming = false; alert(err?.error?.detail || 'Des décisions sont encore en attente.'); }
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
  countByStatus(status: string) { return this.testCases.filter(t => t.status === status).length; }
  truncate(s: string | undefined, max = 120) { if (!s) return ''; return s.length > max ? s.slice(0, max) + '…' : s; }
  trackById(_: number, t: TestCase) { return t.id; }
}
