import { Component, EventEmitter, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../services/api.service';

type Tab = 'file' | 'code' | 'path';

@Component({
  selector: 'app-new-session-modal',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './new-session-modal.component.html',
  styleUrl: './new-session-modal.component.scss'
})
export class NewSessionModalComponent {
  @Output() sessionCreated = new EventEmitter<number>();
  @Output() cancelled = new EventEmitter<void>();

  activeTab: Tab = 'file';

  testPath = '';
  sourcePath = '';

  fileName = '';
  sourceCode = '';
  importedFileName = '';
  dragOver = false;

  isLoading = false;
  error = '';

  constructor(private api: ApiService) {}

  selectTab(tab: Tab) {
    this.activeTab = tab;
    this.error = '';
  }

  onFileChange(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) this.readFile(file);
  }

  onDrop(event: DragEvent) {
    event.preventDefault();
    this.dragOver = false;
    const file = event.dataTransfer?.files?.[0];
    if (file && file.name.toLowerCase().endsWith('.cs')) {
      this.readFile(file);
    } else {
      this.error = 'Veuillez déposer un fichier .cs';
    }
  }

  onDragOver(event: DragEvent) { event.preventDefault(); this.dragOver = true; }
  onDragLeave() { this.dragOver = false; }

  private readFile(file: File) {
    this.importedFileName = file.name;
    this.fileName = file.name;
    this.error = '';
    const reader = new FileReader();
    reader.onload = e => {
      this.sourceCode = (e.target?.result as string) ?? '';
    };
    reader.readAsText(file, 'utf-8');
  }

  clearFile() {
    this.importedFileName = '';
    this.sourceCode = '';
    this.fileName = '';
  }

  submit() {
    this.error = '';

    if (this.activeTab === 'path') {
      if (!this.testPath.trim()) {
        this.error = 'Le chemin du projet de tests est requis.';
        return;
      }
      this.isLoading = true;
      this.api.startSession({ testProjectPath: this.testPath, sourceProjectPath: this.sourcePath }).subscribe({
        next: res => { this.isLoading = false; this.sessionCreated.emit(res.sessionId); },
        error: err => { this.isLoading = false; this.error = err?.error?.detail || 'Erreur lors du démarrage.'; }
      });
      return;
    }

    if (!this.sourceCode.trim()) {
      this.error = 'Le code source ne peut pas être vide.';
      return;
    }
    this.isLoading = true;
    this.api.startSessionFromCode({
      sourceCode: this.sourceCode,
      fileName: this.fileName?.trim() || 'MyCode.cs'
    }).subscribe({
      next: res => { this.isLoading = false; this.sessionCreated.emit(res.sessionId); },
      error: err => {
        this.isLoading = false;
        this.error = err?.error?.detail || err?.error?.title || 'Erreur lors du démarrage.';
      }
    });
  }

  cancel() { this.cancelled.emit(); }

  get canSubmit(): boolean {
    if (this.isLoading) return false;
    if (this.activeTab === 'path') return !!this.testPath.trim();
    return !!this.sourceCode.trim();
  }

  get lineCount(): number {
    if (!this.sourceCode) return 0;
    return this.sourceCode.split('\n').length;
  }

  get codePreview(): string {
    if (!this.sourceCode) return '';
    const lines = this.sourceCode.split('\n').slice(0, 12);
    const preview = lines.join('\n');
    return this.sourceCode.split('\n').length > 12 ? preview + '\n…' : preview;
  }
}
