const API_BASE = '/api';

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      ...options.headers,
    },
    ...options,
  });

  if (!res.ok) {
    const error = await res.json().catch(() => ({ error: res.statusText }));
    throw new Error(error.error || res.statusText);
  }

  if (res.status === 204 || res.headers.get('content-length') === '0') {
    return {} as T;
  }

  return res.json();
}

export interface User {
  id: number;
  username: string;
  email: string;
}

export interface Mailbox {
  id: number;
  name: string;
  specialUse: string | null;
  messageCount: number;
  unreadCount: number;
}

export interface EmailSummary {
  id: number;
  uid: number;
  from: string;
  to: string;
  subject: string;
  preview: string | null;
  date: string;
  isRead: boolean;
  isFlagged: boolean;
}

export interface EmailDetail {
  id: number;
  uid: number;
  from: string;
  to: string;
  cc: string | null;
  subject: string;
  textBody: string | null;
  htmlBody: string | null;
  date: string;
  isRead: boolean;
}

export interface EmailList {
  total: number;
  page: number;
  pageSize: number;
  messages: EmailSummary[];
}

export const api = {
  auth: {
    register(username: string, email: string, password: string) {
      return request<User>('/auth/register', {
        method: 'POST',
        body: JSON.stringify({ username, email, password }),
      });
    },
    login(username: string, password: string) {
      return request<User>('/auth/login', {
        method: 'POST',
        body: JSON.stringify({ username, password }),
      });
    },
    token(username: string, password: string) {
      return request<{ token: string; username: string; email: string }>('/auth/token', {
        method: 'POST',
        body: JSON.stringify({ username, password }),
      });
    },
    logout() {
      return request('/auth/logout', { method: 'POST' });
    },
    me() {
      return request<User>('/auth/me');
    },
  },
  mailboxes: {
    list() {
      return request<Mailbox[]>('/mailboxes');
    },
    create(name: string) {
      return request<Mailbox>('/mailboxes', {
        method: 'POST',
        body: JSON.stringify({ name }),
      });
    },
    delete(id: number) {
      return request(`/mailboxes/${id}`, { method: 'DELETE' });
    },
  },
  emails: {
    list(mailboxId?: number, page = 1) {
      const params = new URLSearchParams({ page: page.toString() });
      if (mailboxId) params.set('mailboxId', mailboxId.toString());
      return request<EmailList>(`/emails?${params}`);
    },
    get(id: number) {
      return request<EmailDetail>(`/emails/${id}`);
    },
    send(to: string, subject: string, body: string, cc?: string, bcc?: string, isHtml = false) {
      return request('/emails', {
        method: 'POST',
        body: JSON.stringify({ to, subject, body, cc: cc || null, bcc: bcc || null, isHtml }),
      });
    },
    markRead(id: number) {
      return request(`/emails/${id}/read`, { method: 'PUT' });
    },
    markUnread(id: number) {
      return request(`/emails/${id}/unread`, { method: 'PUT' });
    },
    move(id: number, targetMailboxId: number) {
      return request(`/emails/${id}/move`, {
        method: 'PUT',
        body: JSON.stringify({ targetMailboxId }),
      });
    },
    delete(id: number) {
      return request(`/emails/${id}`, { method: 'DELETE' });
    },
    restore(id: number) {
      return request(`/emails/${id}/restore`, { method: 'PUT' });
    },
  },
};
