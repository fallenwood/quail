<script lang="ts">
  import { api } from '../api';
  import { getRouter } from '../stores/state.svelte';

  const router = getRouter();

  let to = $state('');
  let cc = $state('');
  let bcc = $state('');
  let subject = $state('');
  let body = $state('');
  let error = $state('');
  let sending = $state(false);
  let sent = $state(false);

  // Pre-fill from compose context (reply/forward)
  if (router.composeContext) {
    to = router.composeContext.to;
    cc = router.composeContext.cc;
    bcc = router.composeContext.bcc;
    subject = router.composeContext.subject;
    body = router.composeContext.body;
    router.composeContext = null;
  }

  async function handleSend() {
    if (!to || !subject) {
      error = 'To and Subject are required';
      return;
    }
    error = '';
    sending = true;
    try {
      await api.emails.send(to, subject, body, cc || undefined, bcc || undefined);
      sent = true;
      setTimeout(() => {
        router.route = 'inbox';
      }, 1500);
    } catch (e: any) {
      error = e.message || 'Failed to send';
    } finally {
      sending = false;
    }
  }

  function cancel() {
    router.route = 'inbox';
  }
</script>

<div class="compose">
  <div class="compose-header">
    <h2>New Message</h2>
    <button class="cancel-btn" onclick={cancel} aria-label="Cancel">
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/>
      </svg>
    </button>
  </div>

  {#if sent}
    <div class="success">
      <svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/>
      </svg>
      <p>Message sent successfully!</p>
    </div>
  {:else}
    {#if error}
      <div class="error">
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <circle cx="12" cy="12" r="10"/><line x1="15" y1="9" x2="9" y2="15"/><line x1="9" y1="9" x2="15" y2="15"/>
        </svg>
        {error}
      </div>
    {/if}

    <form onsubmit={(e) => { e.preventDefault(); handleSend(); }}>
      <div class="field">
        <label for="to-field">To</label>
        <input id="to-field" type="text" bind:value={to} placeholder="recipient@example.com" required />
      </div>
      <div class="field">
        <label for="cc-field">Cc</label>
        <input id="cc-field" type="text" bind:value={cc} placeholder="Optional" />
      </div>
      <div class="field">
        <label for="bcc-field">Bcc</label>
        <input id="bcc-field" type="text" bind:value={bcc} placeholder="Optional" />
      </div>
      <div class="field">
        <label for="subject-field">Subject</label>
        <input id="subject-field" type="text" bind:value={subject} placeholder="What's this about?" required />
      </div>
      <div class="field">
        <label for="body-field">Message</label>
        <textarea id="body-field" bind:value={body} rows="14" placeholder="Write your message..."></textarea>
      </div>
      <div class="actions">
        <button type="button" class="btn-secondary" onclick={cancel}>Discard</button>
        <button type="submit" class="btn-primary" disabled={sending}>
          {#if sending}
            <svg class="spinner" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M12 2v4m0 12v4m-7.07-3.93l2.83-2.83m8.48-8.48l2.83-2.83M2 12h4m12 0h4m-3.93 7.07l-2.83-2.83M6.34 6.34L3.51 3.51"/>
            </svg>
            Sending...
          {:else}
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <line x1="22" y1="2" x2="11" y2="13"/><polygon points="22 2 15 22 11 13 2 9 22 2"/>
            </svg>
            Send
          {/if}
        </button>
      </div>
    </form>
  {/if}
</div>

<style>
  .compose {
    flex: 1;
    padding: 2rem;
    overflow-y: auto;
    background: var(--color-surface);
    max-width: 720px;
  }
  .compose-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 1.5rem;
  }
  .compose-header h2 { margin: 0; font-size: 1.3rem; font-weight: 700; }
  .cancel-btn {
    padding: 0.5rem;
    background: none;
    border: none;
    border-radius: var(--radius-sm);
    cursor: pointer;
    color: var(--color-text-muted);
    transition: color var(--transition), background var(--transition);
  }
  .cancel-btn:hover { color: var(--color-text); background: var(--color-border-light); }

  .field { margin-bottom: 1.25rem; }
  label {
    display: block;
    margin-bottom: 0.35rem;
    font-weight: 500;
    font-size: 0.8rem;
    color: var(--color-text-secondary);
    text-transform: uppercase;
    letter-spacing: 0.03em;
  }
  input, textarea {
    display: block;
    width: 100%;
    padding: 0.65rem 0.85rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    font-size: 0.9rem;
    font-family: inherit;
    color: var(--color-text);
    background: var(--color-surface);
    transition: border-color var(--transition), box-shadow var(--transition);
  }
  input:focus, textarea:focus {
    outline: none;
    border-color: var(--color-primary);
    box-shadow: 0 0 0 3px rgba(79, 70, 229, 0.1);
  }
  input::placeholder, textarea::placeholder { color: var(--color-text-muted); }
  textarea { resize: vertical; min-height: 200px; line-height: 1.6; }

  .actions {
    display: flex;
    gap: 0.75rem;
    justify-content: flex-end;
    padding-top: 0.5rem;
  }
  .btn-primary {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    padding: 0.6rem 1.25rem;
    background: var(--color-primary);
    color: white;
    border: none;
    border-radius: var(--radius-sm);
    cursor: pointer;
    font-size: 0.875rem;
    font-weight: 600;
    font-family: inherit;
    transition: background var(--transition), box-shadow var(--transition);
  }
  .btn-primary:hover { background: var(--color-primary-hover); box-shadow: 0 4px 12px rgba(79, 70, 229, 0.3); }
  .btn-primary:disabled { opacity: 0.6; cursor: default; box-shadow: none; }
  .btn-secondary {
    padding: 0.6rem 1.25rem;
    background: none;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    cursor: pointer;
    font-size: 0.875rem;
    font-family: inherit;
    color: var(--color-text-secondary);
    transition: border-color var(--transition), color var(--transition);
  }
  .btn-secondary:hover { border-color: var(--color-text-muted); color: var(--color-text); }

  .spinner { animation: spin 1s linear infinite; }
  @keyframes spin { to { transform: rotate(360deg); } }

  .error {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    background: var(--color-danger-light);
    color: var(--color-danger);
    padding: 0.75rem 1rem;
    border-radius: var(--radius-sm);
    margin-bottom: 1.25rem;
    font-size: 0.85rem;
    border: 1px solid rgba(239, 68, 68, 0.2);
  }
  .success {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 0.75rem;
    padding: 3rem;
    text-align: center;
    color: var(--color-success);
  }
  .success p { font-size: 1rem; font-weight: 500; }

  @media (max-width: 768px) {
    .compose { padding: 1.25rem; }
  }

  @media (max-width: 480px) {
    .compose { padding: 1rem; }
    .compose-header h2 { font-size: 1.1rem; }
    .actions { flex-direction: column-reverse; }
    .btn-primary, .btn-secondary { width: 100%; justify-content: center; }
  }
</style>
