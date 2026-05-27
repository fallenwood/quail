<script lang="ts">
  import { api, type EmailSummary } from '../api';
  import { getRouter } from '../stores/state.svelte';

  const router = getRouter();

  let emails = $state<EmailSummary[]>([]);
  let total = $state(0);
  let page = $state(1);
  let loading = $state(false);

  $effect(() => {
    loadEmails(router.selectedMailboxId, page);
  });

  async function loadEmails(mailboxId: number | null, p: number) {
    loading = true;
    try {
      const result = await api.emails.list(mailboxId ?? undefined, p);
      emails = result.messages;
      total = result.total;
    } catch { /* ignore */ } finally {
      loading = false;
    }
  }

  function openEmail(email: EmailSummary) {
    router.selectedEmailId = email.id;
    router.route = 'email';
  }

  function formatDate(dateStr: string): string {
    const d = new Date(dateStr);
    const now = new Date();
    if (d.toDateString() === now.toDateString()) {
      return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }
    return d.toLocaleDateString([], { month: 'short', day: 'numeric' });
  }

  async function deleteEmail(e: Event, id: number) {
    e.stopPropagation();
    await api.emails.delete(id);
    emails = emails.filter(em => em.id !== id);
  }

  async function restoreEmail(e: Event, id: number) {
    e.stopPropagation();
    await api.emails.restore(id);
    emails = emails.filter(em => em.id !== id);
    total--;
  }

  function isTrash(): boolean {
    return router.selectedMailboxSpecialUse === 'Trash';
  }
</script>

<div class="inbox">
  <div class="inbox-header">
    <div class="header-left">
      <h2>Inbox</h2>
      <span class="count">{total} messages</span>
    </div>
  </div>

  {#if loading}
    <div class="skeleton-list">
      {#each Array(6) as _}
        <div class="skeleton-row">
          <div class="skeleton-avatar"></div>
          <div class="skeleton-content">
            <div class="skeleton-line short"></div>
            <div class="skeleton-line long"></div>
          </div>
          <div class="skeleton-line tiny"></div>
        </div>
      {/each}
    </div>
  {:else if emails.length === 0}
    <div class="empty">
      <svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" class="empty-icon">
        <polyline points="22 12 16 12 14 15 10 15 8 12 2 12"/>
        <path d="M5.45 5.11L2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/>
      </svg>
      <p>No messages yet</p>
    </div>
  {:else}
    <div class="email-list">
      {#each emails as email}
        <div
          class="email-row"
          class:unread={!email.isRead}
          role="button"
          tabindex="0"
          onclick={() => openEmail(email)}
          onkeydown={(e) => { if (e.key === 'Enter') openEmail(email); }}
        >
          <div class="sender-avatar">{email.from.charAt(0).toUpperCase()}</div>
          <div class="email-body">
            <div class="email-top">
              <span class="email-from">{email.from}</span>
              <span class="email-date">{formatDate(email.date)}</span>
            </div>
            <div class="email-subject">{email.subject}</div>
            {#if email.preview}
              <div class="email-preview">{email.preview}</div>
            {/if}
          </div>
          <div class="email-actions">
            {#if isTrash()}
              <button class="icon-btn restore" onclick={(e) => restoreEmail(e, email.id)} title="Restore" aria-label="Restore">
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                  <polyline points="1 4 1 10 7 10"/><path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10"/>
                </svg>
              </button>
            {/if}
            <button class="icon-btn delete" onclick={(e) => deleteEmail(e, email.id)} title="Delete" aria-label="Delete">
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <polyline points="3 6 5 6 21 6"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
              </svg>
            </button>
          </div>
        </div>
      {/each}
    </div>

    {#if total > 50}
      <div class="pagination">
        <button disabled={page <= 1} onclick={() => page--}>
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="15 18 9 12 15 6"/></svg>
          Previous
        </button>
        <span class="page-info">Page {page}</span>
        <button disabled={emails.length < 50} onclick={() => page++}>
          Next
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="9 18 15 12 9 6"/></svg>
        </button>
      </div>
    {/if}
  {/if}
</div>

<style>
  .inbox { flex: 1; overflow-y: auto; background: var(--color-surface); }
  .inbox-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 1.25rem 1.5rem;
    border-bottom: 1px solid var(--color-border);
    position: sticky;
    top: 0;
    background: var(--color-surface);
    z-index: 10;
  }
  .header-left { display: flex; align-items: baseline; gap: 0.75rem; }
  .inbox-header h2 { margin: 0; font-size: 1.25rem; font-weight: 700; }
  .count { color: var(--color-text-muted); font-size: 0.8rem; }

  .skeleton-list { padding: 0.5rem 0; }
  .skeleton-row {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 1rem 1.5rem;
  }
  .skeleton-avatar {
    width: 36px;
    height: 36px;
    border-radius: 50%;
    background: var(--color-border-light);
    animation: pulse 1.5s infinite;
  }
  .skeleton-content { flex: 1; display: flex; flex-direction: column; gap: 0.4rem; }
  .skeleton-line {
    height: 12px;
    border-radius: 4px;
    background: var(--color-border-light);
    animation: pulse 1.5s infinite;
  }
  .skeleton-line.short { width: 30%; }
  .skeleton-line.long { width: 70%; }
  .skeleton-line.tiny { width: 50px; }
  @keyframes pulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.5; }
  }

  .empty {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    padding: 4rem 2rem;
    color: var(--color-text-muted);
    gap: 1rem;
  }
  .empty-icon { opacity: 0.4; }
  .empty p { font-size: 0.9rem; }

  .email-list { display: flex; flex-direction: column; }
  .email-row {
    display: flex;
    align-items: flex-start;
    padding: 0.875rem 1.5rem;
    border-bottom: 1px solid var(--color-border-light);
    background: var(--color-surface);
    cursor: pointer;
    gap: 0.75rem;
    transition: background var(--transition);
  }
  .email-row:hover { background: var(--color-bg); }
  .email-row:hover .email-actions { opacity: 1; }
  .email-row.unread {
    background: var(--color-primary-light);
  }
  .email-row.unread .email-from { font-weight: 600; color: var(--color-text); }
  .email-row.unread .email-subject { font-weight: 600; }

  .sender-avatar {
    width: 36px;
    height: 36px;
    border-radius: 50%;
    background: var(--color-border-light);
    color: var(--color-text-secondary);
    display: flex;
    align-items: center;
    justify-content: center;
    font-weight: 600;
    font-size: 0.8rem;
    flex-shrink: 0;
    margin-top: 0.1rem;
  }
  .email-body { flex: 1; min-width: 0; }
  .email-top {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 0.15rem;
  }
  .email-from {
    font-size: 0.85rem;
    color: var(--color-text-secondary);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .email-date {
    font-size: 0.75rem;
    color: var(--color-text-muted);
    white-space: nowrap;
    flex-shrink: 0;
    margin-left: 0.5rem;
  }
  .email-subject {
    font-size: 0.875rem;
    color: var(--color-text);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .email-preview {
    font-size: 0.8rem;
    color: var(--color-text-muted);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    margin-top: 0.15rem;
  }
  .email-actions {
    display: flex;
    gap: 0.25rem;
    opacity: 0;
    transition: opacity var(--transition);
    flex-shrink: 0;
    margin-top: 0.1rem;
  }
  .icon-btn {
    padding: 0.35rem;
    background: none;
    border: none;
    border-radius: var(--radius-sm);
    cursor: pointer;
    color: var(--color-text-muted);
    transition: color var(--transition), background var(--transition);
  }
  .icon-btn.delete:hover { color: var(--color-danger); background: var(--color-danger-light); }
  .icon-btn.restore:hover { color: var(--color-success); background: var(--color-success-light); }

  .pagination {
    display: flex;
    justify-content: center;
    align-items: center;
    gap: 1rem;
    padding: 1rem;
    border-top: 1px solid var(--color-border);
  }
  .pagination button {
    display: flex;
    align-items: center;
    gap: 0.35rem;
    padding: 0.4rem 0.75rem;
    border: 1px solid var(--color-border);
    background: var(--color-surface);
    border-radius: var(--radius-sm);
    cursor: pointer;
    font-size: 0.8rem;
    font-family: inherit;
    color: var(--color-text-secondary);
    transition: border-color var(--transition), color var(--transition);
  }
  .pagination button:hover:not(:disabled) { border-color: var(--color-primary); color: var(--color-primary); }
  .pagination button:disabled { opacity: 0.4; cursor: default; }
  .page-info { font-size: 0.8rem; color: var(--color-text-muted); }

  @media (max-width: 768px) {
    .email-row { padding: 0.75rem 1rem; }
    .inbox-header { padding: 1rem; }
    .email-actions { opacity: 1; }
    .sender-avatar { width: 32px; height: 32px; font-size: 0.75rem; }
  }

  @media (max-width: 480px) {
    .email-row { padding: 0.75rem; }
    .inbox-header { padding: 0.75rem; }
    .inbox-header h2 { font-size: 1.1rem; }
  }
</style>
