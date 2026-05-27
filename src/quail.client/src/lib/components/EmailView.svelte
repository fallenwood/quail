<script lang="ts">
  import { api, type EmailDetail } from '../api';
  import { getAuth, getRouter } from '../stores/state.svelte';
  import type { ComposeContext } from '../stores/state.svelte';
  import DOMPurify from 'dompurify';

  const auth = getAuth();
  const router = getRouter();

  let email = $state<EmailDetail | null>(null);
  let loading = $state(true);

  $effect(() => {
    if (router.selectedEmailId) {
      loadEmail(router.selectedEmailId);
    }
  });

  async function loadEmail(id: number) {
    loading = true;
    try {
      email = await api.emails.get(id);
    } catch { /* ignore */ } finally {
      loading = false;
    }
  }

  function goBack() {
    router.selectedEmailId = null;
    router.route = 'inbox';
  }

  async function handleDelete() {
    if (email) {
      await api.emails.delete(email.id);
      goBack();
    }
  }

  async function handleRestore() {
    if (email) {
      await api.emails.restore(email.id);
      goBack();
    }
  }

  function isTrash(): boolean {
    return router.selectedMailboxSpecialUse === 'Trash';
  }

  function formatDate(dateStr: string): string {
    return new Date(dateStr).toLocaleString();
  }

  function buildQuotedBody(e: EmailDetail): string {
    const date = formatDate(e.date);
    const header = `\n\nOn ${date}, ${e.from} wrote:\n`;
    const originalText = e.textBody || '';
    const quoted = originalText.split('\n').map(line => `> ${line}`).join('\n');
    return header + quoted;
  }

  function handleReply() {
    if (!email) return;
    const subject = email.subject.startsWith('Re:') ? email.subject : `Re: ${email.subject}`;
    router.composeContext = {
      to: email.from,
      cc: '',
      bcc: '',
      subject,
      body: buildQuotedBody(email),
    };
    router.route = 'compose';
  }

  function handleReplyAll() {
    if (!email) return;
    const currentEmail = auth.user?.email || '';
    const subject = email.subject.startsWith('Re:') ? email.subject : `Re: ${email.subject}`;

    // To = original sender
    const to = email.from;

    // CC = original To + CC, minus current user and original sender
    const originalTo = email.to.split(',').map(a => a.trim());
    const originalCc = email.cc ? email.cc.split(',').map(a => a.trim()) : [];
    const allCc = [...originalTo, ...originalCc]
      .filter(a => a && a !== currentEmail && a !== email!.from);
    const cc = [...new Set(allCc)].join(', ');

    router.composeContext = {
      to,
      cc,
      bcc: '',
      subject,
      body: buildQuotedBody(email),
    };
    router.route = 'compose';
  }

  function handleForward() {
    if (!email) return;
    const subject = email.subject.startsWith('Fwd:') ? email.subject : `Fwd: ${email.subject}`;
    const date = formatDate(email.date);
    const header = `\n\n---------- Forwarded message ----------\nFrom: ${email.from}\nDate: ${date}\nSubject: ${email.subject}\nTo: ${email.to}\n${email.cc ? `Cc: ${email.cc}\n` : ''}\n`;
    const originalText = email.textBody || '';

    router.composeContext = {
      to: '',
      cc: '',
      bcc: '',
      subject,
      body: header + originalText,
    };
    router.route = 'compose';
  }
</script>

<div class="email-view">
  {#if loading}
    <div class="loading-state">
      <div class="skeleton-header"></div>
      <div class="skeleton-body">
        <div class="skeleton-line" style="width: 60%"></div>
        <div class="skeleton-line" style="width: 40%"></div>
        <div class="skeleton-line" style="width: 80%"></div>
        <div class="skeleton-line" style="width: 50%"></div>
      </div>
    </div>
  {:else if email}
    <div class="email-header">
      <button class="back-btn" onclick={goBack} aria-label="Back to inbox">
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <line x1="19" y1="12" x2="5" y2="12"/><polyline points="12 19 5 12 12 5"/>
        </svg>
        Back
      </button>
      <div class="header-actions">
        {#if isTrash()}
          <button class="action-btn restore" onclick={handleRestore} aria-label="Restore email">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <polyline points="1 4 1 10 7 10"/><path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10"/>
            </svg>
            Restore
          </button>
        {/if}
        <button class="action-btn delete" onclick={handleDelete} aria-label="Delete email">
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="3 6 5 6 21 6"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
          </svg>
          Delete
        </button>
      </div>
    </div>

    <div class="email-detail">
      <h1 class="subject">{email.subject}</h1>
      <div class="meta">
        <div class="meta-row">
          <span class="meta-label">From</span>
          <span class="meta-value">{email.from}</span>
        </div>
        <div class="meta-row">
          <span class="meta-label">To</span>
          <span class="meta-value">{email.to}</span>
        </div>
        {#if email.cc}
          <div class="meta-row">
            <span class="meta-label">Cc</span>
            <span class="meta-value">{email.cc}</span>
          </div>
        {/if}
        <div class="meta-row">
          <span class="meta-label">Date</span>
          <span class="meta-value">{formatDate(email.date)}</span>
        </div>
      </div>

      <div class="body">
        {#if email.htmlBody}
          {@html DOMPurify.sanitize(email.htmlBody, { FORBID_TAGS: ['style', 'iframe', 'form', 'object', 'embed', 'base', 'meta', 'link', 'script'], FORBID_ATTR: ['onerror', 'onload', 'onclick', 'onmouseover', 'onfocus', 'onblur', 'action', 'formaction', 'srcdoc'] })}
        {:else}
          <pre>{email.textBody}</pre>
        {/if}
      </div>

      <div class="reply-actions">
        <button class="reply-btn" onclick={handleReply}>
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="9 17 4 12 9 7"/><path d="M20 18v-2a4 4 0 0 0-4-4H4"/>
          </svg>
          Reply
        </button>
        <button class="reply-btn" onclick={handleReplyAll}>
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="7 17 2 12 7 7"/><polyline points="12 17 7 12 12 7"/><path d="M22 18v-2a4 4 0 0 0-4-4H7"/>
          </svg>
          Reply All
        </button>
        <button class="reply-btn" onclick={handleForward}>
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <polyline points="15 17 20 12 15 7"/><path d="M4 18v-2a4 4 0 0 1 4-4h12"/>
          </svg>
          Forward
        </button>
      </div>
    </div>
  {:else}
    <div class="empty">
      <p>Email not found</p>
    </div>
  {/if}
</div>

<style>
  .email-view { flex: 1; overflow-y: auto; background: var(--color-surface); }

  .loading-state { padding: 1.5rem; }
  .skeleton-header {
    height: 28px;
    width: 50%;
    background: var(--color-border-light);
    border-radius: 4px;
    margin-bottom: 1.5rem;
    animation: pulse 1.5s infinite;
  }
  .skeleton-body { display: flex; flex-direction: column; gap: 0.75rem; }
  .skeleton-body .skeleton-line {
    height: 14px;
    background: var(--color-border-light);
    border-radius: 4px;
    animation: pulse 1.5s infinite;
  }
  @keyframes pulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.5; }
  }

  .empty { padding: 4rem 2rem; text-align: center; color: var(--color-text-muted); }

  .email-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 0.75rem 1.5rem;
    border-bottom: 1px solid var(--color-border);
    position: sticky;
    top: 0;
    background: var(--color-surface);
    z-index: 10;
  }
  .header-actions { display: flex; gap: 0.5rem; }
  .back-btn {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    padding: 0.4rem 0.75rem;
    border: 1px solid var(--color-border);
    background: var(--color-surface);
    border-radius: var(--radius-sm);
    cursor: pointer;
    font-size: 0.85rem;
    font-family: inherit;
    color: var(--color-text-secondary);
    transition: border-color var(--transition), color var(--transition);
  }
  .back-btn:hover { border-color: var(--color-text-muted); color: var(--color-text); }
  .action-btn {
    display: flex;
    align-items: center;
    gap: 0.35rem;
    padding: 0.4rem 0.75rem;
    border: 1px solid var(--color-border);
    background: var(--color-surface);
    border-radius: var(--radius-sm);
    cursor: pointer;
    font-size: 0.85rem;
    font-family: inherit;
    color: var(--color-text-secondary);
    transition: all var(--transition);
  }
  .action-btn.delete:hover { color: var(--color-danger); border-color: var(--color-danger); background: var(--color-danger-light); }
  .action-btn.restore:hover { color: var(--color-success); border-color: var(--color-success); background: var(--color-success-light); }

  .email-detail { padding: 2rem 2rem 3rem; max-width: 800px; }
  .subject {
    font-size: 1.4rem;
    font-weight: 700;
    margin: 0 0 1.25rem;
    line-height: 1.35;
    word-break: break-word;
  }
  .meta {
    background: var(--color-bg);
    padding: 1rem 1.25rem;
    border-radius: var(--radius-md);
    margin-bottom: 2rem;
    border: 1px solid var(--color-border-light);
  }
  .meta-row {
    display: flex;
    gap: 0.75rem;
    font-size: 0.85rem;
    margin-bottom: 0.35rem;
    align-items: baseline;
  }
  .meta-row:last-child { margin-bottom: 0; }
  .meta-label {
    color: var(--color-text-muted);
    min-width: 40px;
    font-weight: 500;
  }
  .meta-value { color: var(--color-text-secondary); word-break: break-all; }
  .body { line-height: 1.7; color: var(--color-text); overflow-x: auto; }
  .body pre { white-space: pre-wrap; word-break: break-word; font-family: inherit; }

  .reply-actions {
    display: flex;
    gap: 0.5rem;
    margin-top: 2rem;
    padding-top: 1.5rem;
    border-top: 1px solid var(--color-border);
  }
  .reply-btn {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    padding: 0.5rem 1rem;
    border: 1px solid var(--color-border);
    background: var(--color-surface);
    border-radius: var(--radius-sm);
    cursor: pointer;
    font-size: 0.85rem;
    font-family: inherit;
    color: var(--color-text-secondary);
    transition: all var(--transition);
  }
  .reply-btn:hover {
    border-color: var(--color-primary);
    color: var(--color-primary);
    background: var(--color-primary-light);
  }

  @media (max-width: 768px) {
    .email-detail { padding: 1.25rem; }
    .subject { font-size: 1.2rem; }
    .email-header { padding: 0.75rem 1rem; }
    .reply-actions { flex-wrap: wrap; }
  }

  @media (max-width: 480px) {
    .email-detail { padding: 1rem; }
    .subject { font-size: 1.05rem; }
    .action-btn { padding: 0.4rem; }
    .reply-btn { flex: 1; justify-content: center; padding: 0.6rem 0.5rem; font-size: 0.8rem; }
  }
</style>
