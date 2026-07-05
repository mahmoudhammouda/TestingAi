import { Component, EventEmitter, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../services/api.service';

type Tab = 'file' | 'code' | 'path' | 'folder';

interface FolderEntry { relativePath: string; content: string; selected: boolean; isCsproj: boolean; }

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

  // ── Import de dossier (webkitdirectory) ──
  folderName = '';
  folderFiles: FolderEntry[] = [];
  folderHasProject = false;
  folderBlocked = '';

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

  // ── Import de dossier ────────────────────────────────────────────────
  private static readonly excludedDir = /(^|\/)(bin|obj|\.git|\.vs|\.idea|node_modules)(\/|$)/i;

  private static relPath(f: File): string {
    return ((f as unknown as { webkitRelativePath?: string }).webkitRelativePath) || f.name;
  }

  onFolderChange(event: Event) {
    const input = event.target as HTMLInputElement;
    const files = input.files;
    if (!files || files.length === 0) return;

    this.error = '';
    this.folderBlocked = '';
    this.folderFiles = [];
    this.folderHasProject = false;

    const all = Array.from(files);
    const rootSeg = NewSessionModalComponent.relPath(all[0]).split('/')[0];
    this.folderName = rootSeg || 'dossier';

    const keep = (f: File) => !NewSessionModalComponent.excludedDir.test(NewSessionModalComponent.relPath(f));
    const slnCount = all.filter(f => keep(f) && f.name.toLowerCase().endsWith('.sln')).length;
    const csprojCount = all.filter(f => keep(f) && f.name.toLowerCase().endsWith('.csproj')).length;

    if (slnCount > 0 || csprojCount > 1) {
      this.folderBlocked =
        'Ce dossier contient une solution (.sln) ou plusieurs projets (.csproj). ' +
        'La prise en charge des solutions complètes arrivera dans une prochaine étape. ' +
        'Importez plutôt un dossier de fichiers .cs, ou un dossier contenant un unique projet .csproj.';
      return;
    }

    const relevant = all.filter(f => {
      if (!keep(f)) return false;
      const l = f.name.toLowerCase();
      return l.endsWith('.cs') || l.endsWith('.csproj');
    });

    Promise.all(relevant.map(f => this.readEntry(f))).then(entries => {
      this.folderFiles = entries.sort((a, b) => a.relativePath.localeCompare(b.relativePath));
      this.folderHasProject = this.folderFiles.some(f => f.isCsproj);
    });
  }

  private readEntry(file: File): Promise<FolderEntry> {
    const relativePath = NewSessionModalComponent.relPath(file);
    const isCsproj = relativePath.toLowerCase().endsWith('.csproj');
    return new Promise(resolve => {
      const reader = new FileReader();
      reader.onload = e => resolve({ relativePath, content: (e.target?.result as string) ?? '', selected: !isCsproj, isCsproj });
      reader.onerror = () => resolve({ relativePath, content: '', selected: !isCsproj, isCsproj });
      reader.readAsText(file, 'utf-8');
    });
  }

  get csFiles(): FolderEntry[] { return this.folderFiles.filter(f => !f.isCsproj); }
  get selectedCsCount(): number { return this.csFiles.filter(f => f.selected).length; }
  get folderWarnMany(): boolean { return this.csFiles.length > 20; }

  toggleFolderFile(f: FolderEntry) { f.selected = !f.selected; }
  toggleAllFolderFiles(select: boolean) { this.csFiles.forEach(f => f.selected = select); }

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

    if (this.activeTab === 'folder') {
      if (this.folderBlocked) { this.error = this.folderBlocked; return; }
      const cs = this.csFiles;
      if (cs.length === 0) { this.error = 'Aucun fichier .cs dans le dossier sélectionné.'; return; }
      const selected = cs.filter(f => f.selected);
      if (selected.length === 0) { this.error = 'Sélectionnez au moins un fichier .cs à couvrir.'; return; }
      this.isLoading = true;
      this.api.startSessionFromFolder({
        files: this.folderFiles.map(f => ({ relativePath: f.relativePath, content: f.content })),
        generateFor: selected.map(f => f.relativePath)
      }).subscribe({
        next: res => { this.isLoading = false; this.sessionCreated.emit(res.sessionId); },
        error: err => { this.isLoading = false; this.error = err?.error?.detail || err?.error?.title || 'Erreur lors du démarrage.'; }
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
    if (this.activeTab === 'folder') return !this.folderBlocked && this.selectedCsCount > 0;
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
