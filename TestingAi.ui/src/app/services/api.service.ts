import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Session, TestCase, PipelineSettings } from '../models/test-models';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly base = 'http://localhost:5182';
  constructor(private http: HttpClient) {}

  getSessions(): Observable<Session[]> { return this.http.get<Session[]>(`${this.base}/api/sessions`); }
  getSession(id: number): Observable<Session> { return this.http.get<Session>(`${this.base}/api/sessions/${id}`); }
  startSession(req: { testProjectPath: string; sourceProjectPath?: string }): Observable<any> { return this.http.post(`${this.base}/api/sessions`, req); }
  resumeSession(id: number): Observable<any> { return this.http.post(`${this.base}/api/sessions/${id}/resume`, {}); }
  getTestCases(sessionId: number): Observable<TestCase[]> { return this.http.get<TestCase[]>(`${this.base}/api/sessions/${sessionId}/tests`); }
  setAction(testId: number, action: string): Observable<any> { return this.http.put(`${this.base}/api/tests/${testId}/action`, { action }); }
  bulkSetAction(testIds: number[], action: string): Observable<any> { return this.http.put(`${this.base}/api/tests/bulk-action`, { testIds, action }); }
  getSettings(): Observable<PipelineSettings> { return this.http.get<PipelineSettings>(`${this.base}/api/settings`); }
  updateSettings(s: PipelineSettings): Observable<any> { return this.http.put(`${this.base}/api/settings`, s); }
  getLogs(sessionId: number): Observable<any[]> { return this.http.get<any[]>(`${this.base}/api/sessions/${sessionId}/logs`); }
}
