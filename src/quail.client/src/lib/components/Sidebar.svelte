<script lang="ts">
  import { api, type Mailbox } from '../api';
  import { getAuth, getRouter } from '../stores/state.svelte';

  const auth = getAuth();
  const router = getRouter();

  let mailboxes = $state<Mailbox[]>([]);

  $effect(() => {
    if (auth.isLoggedIn) {
      loadMailboxes();
    }
  });

  async function loadMailboxes() {
    try {
      mailboxes = await api.mailboxes.list();
    } catch { /* ignore */ }
  }

  function selectMailbox(mb: Mailbox) {
    router.selectedMailboxId = mb.id;
    router.selectedMailboxSpecialUse = mb.specialUse;
    router.selectedEmailId = null;
    router.route = 'inbox';
    router.sidebarOpen = false;
  }

  async function handleLogout() {
    await api.auth.logout();
    auth.user = null;
    router.route = 'login';
  }
</script>

<aside class="sidebar">
  <div class="brand">
    <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <path d="M22 2L2 8.5l7.5 3L22 2z"/>
      <path d="M22 2l-8.5 17.5-3-7.5L22 2z"/>
      <path d="M9.5 11.5l3 7.5"/>
    </svg>
    <span class="brand-name">Quail</span>
  </div>

  <button class="compose-btn" onclick={() => { router.route = 'compose'; router.sidebarOpen = false; }}>
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <path d="M12 20h9"/><path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4L16.5 3.5z"/>
    </svg>
    Compose
  </button>

  <nav>
    {#each mailboxes as mb}
      <button
        class="mailbox-item"
        class:active={router.selectedMailboxId === mb.id}
        onclick={() => selectMailbox(mb)}
      >
        <svg class="mailbox-icon" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round">
          {#if mb.specialUse === 'Inbox'}
            <polyline points="22 12 16 12 14 15 10 15 8 12 2 12"/><path d="M5.45 5.11L2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/>
          {:else if mb.specialUse === 'Sent'}
            <line x1="22" y1="2" x2="11" y2="13"/><polygon points="22 2 15 22 11 13 2 9 22 2"/>
          {:else if mb.specialUse === 'Outbox'}
            <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="17 8 12 3 7 8"/><line x1="12" y1="3" x2="12" y2="15"/>
          {:else if mb.specialUse === 'Drafts'}
            <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"/><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"/>
          {:else if mb.specialUse === 'Trash'}
            <polyline points="3 6 5 6 21 6"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
          {:else if mb.specialUse === 'Junk'}
            <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/>
          {:else}
            <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
          {/if}
        </svg>
        <span class="name">{mb.name}</span>
        {#if mb.unreadCount > 0}
          <span class="badge">{mb.unreadCount}</span>
        {/if}
      </button>
    {/each}
  </nav>

  <div class="sidebar-footer">
    <div class="user-info">
      <div class="avatar">{auth.user?.username?.charAt(0).toUpperCase()}</div>
      <span class="username">{auth.user?.username}</span>
    </div>
    <button class="logout-btn" onclick={handleLogout} aria-label="Logout">
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/><polyline points="16 17 21 12 16 7"/><line x1="21" y1="12" x2="9" y2="12"/>
      </svg>
    </button>
  </div>
</aside>

<style>
  .sidebar {
    width: 260px;
    background: var(--color-surface);
    border-right: 1px solid var(--color-border);
    display: flex;
    flex-direction: column;
    height: 100vh;
  }
  .brand {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    padding: 1.25rem 1.25rem 1rem;
    color: var(--color-primary);
  }
  .brand-name {
    font-weight: 700;
    font-size: 1.2rem;
    color: var(--color-text);
  }
  .compose-btn {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 0.5rem;
    margin: 0 1rem 1rem;
    padding: 0.7rem 1rem;
    background: var(--color-primary);
    color: white;
    border: none;
    border-radius: var(--radius-md);
    cursor: pointer;
    font-size: 0.875rem;
    font-weight: 600;
    font-family: inherit;
    transition: background var(--transition), box-shadow var(--transition);
    box-shadow: var(--shadow-sm);
  }
  .compose-btn:hover {
    background: var(--color-primary-hover);
    box-shadow: 0 4px 12px rgba(79, 70, 229, 0.3);
  }
  nav {
    flex: 1;
    overflow-y: auto;
    padding: 0 0.5rem;
  }
  .mailbox-item {
    display: flex;
    align-items: center;
    width: 100%;
    padding: 0.6rem 0.75rem;
    border: none;
    background: none;
    cursor: pointer;
    text-align: left;
    gap: 0.6rem;
    font-size: 0.85rem;
    font-family: inherit;
    color: var(--color-text-secondary);
    border-radius: var(--radius-sm);
    transition: background var(--transition), color var(--transition);
  }
  .mailbox-item:hover {
    background: var(--color-border-light);
    color: var(--color-text);
  }
  .mailbox-item.active {
    background: var(--color-primary-light);
    color: var(--color-primary);
    font-weight: 500;
  }
  .mailbox-item.active .mailbox-icon {
    stroke: var(--color-primary);
  }
  .mailbox-icon {
    flex-shrink: 0;
  }
  .name { flex: 1; }
  .badge {
    background: var(--color-primary);
    color: white;
    font-size: 0.7rem;
    font-weight: 600;
    padding: 0.15rem 0.5rem;
    border-radius: 10px;
    min-width: 20px;
    text-align: center;
  }
  .sidebar-footer {
    padding: 0.75rem 1rem;
    border-top: 1px solid var(--color-border);
    display: flex;
    align-items: center;
    justify-content: space-between;
  }
  .user-info {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }
  .avatar {
    width: 32px;
    height: 32px;
    border-radius: 50%;
    background: var(--color-primary-light);
    color: var(--color-primary);
    display: flex;
    align-items: center;
    justify-content: center;
    font-weight: 700;
    font-size: 0.8rem;
  }
  .username {
    font-weight: 500;
    font-size: 0.85rem;
    color: var(--color-text);
  }
  .logout-btn {
    padding: 0.4rem;
    background: none;
    border: none;
    border-radius: var(--radius-sm);
    cursor: pointer;
    color: var(--color-text-muted);
    transition: color var(--transition), background var(--transition);
  }
  .logout-btn:hover {
    color: var(--color-danger);
    background: var(--color-danger-light);
  }

  @media (max-width: 768px) {
    .sidebar {
      width: 300px;
      box-shadow: var(--shadow-lg);
    }
    .mailbox-item {
      padding: 0.75rem;
      font-size: 0.9rem;
    }
  }
</style>
