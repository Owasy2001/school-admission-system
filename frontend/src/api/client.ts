export interface SchoolClassItem {
  id: string;
  name: string;
  totalSeats: number;
  heldSeats: number;
  confirmedSeats: number;
  availableSeats: number;
}

export interface ClassCreatedResponse {
  id: string;
  name: string;
  totalSeats: number;
}

export interface HoldCreatedResponse {
  holdId: string;
  classId: string;
  studentId: string;
  status: 'HELD' | 'CONFIRMED' | 'EXPIRED' | 'CANCELLED';
  expiresAt: string;
}

export interface HoldStatusResponse {
  holdId: string;
  classId: string;
  status: 'HELD' | 'CONFIRMED' | 'EXPIRED' | 'CANCELLED';
  expiresAt: string;
  studentId?: string;
  className?: string;
}

export interface HoldConfirmResponse {
  holdId: string;
  status: 'CONFIRMED';
}

export interface StudentHoldItem {
  holdId: string;
  classId: string;
  className: string;
  status: 'HELD' | 'CONFIRMED' | 'EXPIRED' | 'CANCELLED';
  expiresAt: string;
}

export function normalizeDigits(value: string): string {
  let result = '';
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code >= 0x09e6 && code <= 0x09ef) {
      result += String(code - 0x09e6);
    } else if (code >= 48 && code <= 57) {
      result += value[i];
    }
  }
  return result;
}

export class ApiError extends Error {
  status: number;
  code: string;

  constructor(message: string, status: number, code = 'API_ERROR') {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
  }
}

export function getApiBaseUrl(): string {
  const runtimeUrl =
    typeof window !== 'undefined' ? window.__APP_CONFIG__?.API_URL : undefined;
  const envUrl =
    runtimeUrl ||
    import.meta.env.VITE_API_URL ||
    import.meta.env.VITE_API_BASE_URL ||
    import.meta.env.VITE_BACKEND_URL ||
    'http://localhost:8080';

  return envUrl.replace(/\/+$/, '');
}

async function requestJson<T>(
  path: string,
  options: RequestInit = {},
  studentId?: string
): Promise<T> {
  const headers = new Headers(options.headers || {});
  if (options.body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }
  if (studentId) {
    headers.set('X-Student-Id', normalizeDigits(studentId));
  }

  let response: Response;
  try {
    response = await fetch(`${getApiBaseUrl()}${path}`, {
      ...options,
      headers
    });
  } catch {
    throw new ApiError(
      'Unable to connect to the admission server. Please verify the backend service is running.',
      0,
      'NETWORK_ERROR'
    );
  }

  if (response.status === 204) {
    return undefined as T;
  }

  let payload: unknown = null;
  try {
    payload = await response.json();
  } catch {
    payload = null;
  }

  if (!response.ok) {
    const errObj = payload as { error?: string; message?: string } | null;
    const serverMessage = errObj?.message;
    const errorCode = errObj?.error || 'HTTP_ERROR';

    if (serverMessage) {
      throw new ApiError(serverMessage, response.status, errorCode);
    }

    if (response.status === 409) {
      throw new ApiError(
        'No seats are available or you already have an active/confirmed application in this class.',
        409,
        errorCode
      );
    }
    if (response.status === 400) {
      throw new ApiError(
        'Invalid input provided. Please check your 17-digit Birth Registration Number and form fields.',
        400,
        errorCode
      );
    }
    if (response.status === 403) {
      throw new ApiError(
        'You do not have permission to modify another student\'s application.',
        403,
        errorCode
      );
    }
    if (response.status === 404) {
      throw new ApiError('The requested record was not found.', 404, errorCode);
    }

    throw new ApiError(
      `Request failed with status ${response.status}. Please try again.`,
      response.status,
      errorCode
    );
  }

  return payload as T;
}

export const admissionApi = {
  createClass(name: string, totalSeats: number): Promise<ClassCreatedResponse> {
    return requestJson<ClassCreatedResponse>('/classes', {
      method: 'POST',
      body: JSON.stringify({ name, totalSeats })
    });
  },

  getClasses(): Promise<SchoolClassItem[]> {
    return requestJson<SchoolClassItem[]>('/classes', { method: 'GET' });
  },

  getClassById(classId: string): Promise<SchoolClassItem> {
    return requestJson<SchoolClassItem>(`/classes/${encodeURIComponent(classId)}`, {
      method: 'GET'
    });
  },

  createHold(classId: string, studentId: string): Promise<HoldCreatedResponse> {
    return requestJson<HoldCreatedResponse>(
      `/classes/${encodeURIComponent(classId)}/holds`,
      { method: 'POST' },
      studentId
    );
  },

  getHold(holdId: string, studentId?: string): Promise<HoldStatusResponse> {
    return requestJson<HoldStatusResponse>(
      `/holds/${encodeURIComponent(holdId)}`,
      { method: 'GET' },
      studentId
    );
  },

  confirmHold(holdId: string, studentId: string): Promise<HoldConfirmResponse> {
    return requestJson<HoldConfirmResponse>(
      `/holds/${encodeURIComponent(holdId)}/confirm`,
      { method: 'POST' },
      studentId
    );
  },

  cancelHold(holdId: string, studentId: string): Promise<void> {
    return requestJson<void>(
      `/holds/${encodeURIComponent(holdId)}`,
      { method: 'DELETE' },
      studentId
    );
  },

  getMyHolds(studentId: string): Promise<StudentHoldItem[]> {
    return requestJson<StudentHoldItem[]>('/me/holds', { method: 'GET' }, studentId);
  }
};
