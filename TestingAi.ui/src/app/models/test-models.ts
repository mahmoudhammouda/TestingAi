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
